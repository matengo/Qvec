using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Qvec.Core.Format;

namespace Qvec.Core
{
    public enum DistanceFunction : int
    {
        DotProduct = 0,
        Cosine = 1
    }

    public class QvecDatabase : IDisposable
    {
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _headerAccessor;
        private MemoryMappedViewAccessor _dataAccessor;

        /// <summary>The backing file, kept so that <see cref="Vacuum"/> can replace and remap it.</summary>
        private readonly string _path;

        /// <summary>
        /// Base pointer of the data view, acquired once for the lifetime of the database.
        /// Acquiring and releasing it per read turned out to dominate index construction.
        /// </summary>
        private unsafe byte* _dataBasePtr;

        /// <summary>
        /// Base pointer of the header view, acquired once and released in Dispose.
        /// </summary>
        private unsafe byte* _headerBasePtr;
        private readonly ReaderWriterLockSlim _lock = new();

        private V4Header _header;
        private const int HeaderSize = V4Header.HeaderSizeValue;
        private const int MetadataDescriptorSize = 16;
        private const int GuidSize = 16;
        private long _vectorSectionOffset;
        private long _graphSectionOffset;
        private long _metadataSectionOffset;
        private long _metadataHeapOffset;
        private long _guidSectionOffset;
        private long _tombstoneSectionOffset;
        private long _freeListSectionOffset;
        private readonly Dictionary<Guid, int> _guidIndex = new();
        private TombstoneSet _deletedIndices;
        private readonly int[] _cachedEmptyNeighbors;

        // Inverterat index: field -> value -> set of entry indices
        private readonly Dictionary<string, Dictionary<string, HashSet<int>>> _fieldIndex = new();


        
        public QvecDatabase(string path, int dim = 1536, int max = 1000, int maxNeighbors = 32, int maxLayers = 5, DistanceFunction distanceFunction = DistanceFunction.DotProduct)
            : this(path, dim, max, maxNeighbors, maxLayers, distanceFunction, honourHeaderSilently: false)
        {
        }

        /// <summary>
        /// Opens an existing database using only the parameters recorded in its header.
        /// This is the safe way to reopen a file when the creation parameters are not known
        /// to the caller -- unlike the constructor, it cannot be given conflicting values.
        /// </summary>
        /// <exception cref="FileNotFoundException">The file does not exist.</exception>
        /// <exception cref="QvecFormatException">The file is not a valid Qvec database.</exception>
        public static QvecDatabase Open(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Qvec database not found: '{path}'.", path);

            return new QvecDatabase(path, 0, 0, 0, 0, default, honourHeaderSilently: true);
        }

        private QvecDatabase(string path, int dim, int max, int maxNeighbors, int maxLayers,
                             DistanceFunction distanceFunction, bool honourHeaderSilently)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            _path = Path.GetFullPath(path);

            bool exists = File.Exists(path);

            if (exists && new FileInfo(path).Length == 0)
            {
                throw new QvecFormatException(
                    $"'{path}' exists but is empty, so it is not a valid Qvec database. " +
                    "Delete the file to create a new database at this path.");
            }

            // The header is the single source of truth for an existing file. It must be read
            // BEFORE any section offset is computed, otherwise offsets derived from the
            // caller's arguments silently point into the wrong part of the file -- the file
            // would appear healthy while every lookup returned nothing.
            if (exists)
            {
                _header = ReadAndValidateHeader(path);

                if (!honourHeaderSilently)
                {
                    EnsureHeaderMatchesRequest(path, _header, dim, max, maxNeighbors, maxLayers, distanceFunction);
                }
            }
            else
            {
                ValidateCreationParameters(dim, max, maxNeighbors, maxLayers);

                _header = QvecFormatLayout.CreateInitial(
                    dim, max, maxNeighbors, maxLayers,
                    QvecFormatLayout.RecommendMetadataHeapCapacity(max),
                    distanceFunction);
            }

            // From here on, only header values are used. Never the constructor arguments.
            _deletedIndices = new TombstoneSet(_header.MaxCount);

            // Section offsets come exclusively from the file's own section table, so a v4
            // file is self-describing: nothing about where a section lives is recomputed
            // from what the caller happened to pass in.
            CaptureSectionOffsets();

            long totalSize = _header.FileLength;

            if (exists)
            {
                long actualLength = new FileInfo(path).Length;
                if (actualLength < totalSize)
                {
                    throw new QvecFormatException(
                        $"'{path}' is truncated: the header describes a database requiring {totalSize} bytes " +
                        $"but the file is only {actualLength} bytes.");
                }
            }

            if (!exists)
            {
                // Lay the file out sparsely so that sizing a database for a million vectors
                // costs address space rather than disk. MemoryMappedFile.CreateFromFile would
                // otherwise allocate every cluster up front.
                SparseFile.CreateSized(path, totalSize);
            }

            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, null, totalSize);
            _headerAccessor = _mmf.CreateViewAccessor(0, HeaderSize);
            _dataAccessor = _mmf.CreateViewAccessor(HeaderSize, totalSize - HeaderSize);

            int totalSlots = _header.MaxLayers * _header.MaxNeighbors;
            _cachedEmptyNeighbors = new int[totalSlots];
            Array.Fill(_cachedEmptyNeighbors, -1);

            if (!exists)
            {
                CommitHeader();
            }
            else
            {
                RebuildGuidIndex();
                LoadTombstones();
            }
        }

        // --- FILFORMAT ---


        private static void ValidateCreationParameters(int dim, int max, int maxNeighbors, int maxLayers)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dim);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxNeighbors, 2);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLayers);
        }

        /// <summary>
        /// Reads the header straight off disk with a plain FileStream. A memory-mapped view
        /// cannot be used here because sizing the mapping already requires the header.
        /// All structural validation, including the CRC-32 check, happens in
        /// <see cref="V4Header.Read"/>.
        /// </summary>
        private static V4Header ReadAndValidateHeader(string path)
        {
            long length = new FileInfo(path).Length;

            if (length < HeaderSize)
            {
                throw new QvecFormatException(
                    $"'{path}' is only {length} bytes and cannot contain a Qvec v4 header " +
                    $"({HeaderSize} bytes required).");
            }

            var buffer = new byte[HeaderSize];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.ReadExactly(buffer, 0, HeaderSize);
            }

            V4Header header = V4Header.Read(buffer, length);
            header.EnsureCleanOpen(path);
            return header;
        }

        /// <summary>
        /// Rejects a reopen whose arguments contradict the stored header. Honouring the header
        /// silently would hide a real caller bug, and honouring the arguments would corrupt
        /// every read -- so the mismatch is surfaced instead. Use <see cref="Open(string)"/>
        /// when the creation parameters are not known.
        /// </summary>
        private static void EnsureHeaderMatchesRequest(
            string path, V4Header header,
            int dim, int max, int maxNeighbors, int maxLayers, DistanceFunction distanceFunction)
        {
            static string Describe(string name, int stored, int requested)
                => $"{name}: file has {stored}, caller requested {requested}";

            var mismatches = new List<string>();

            if (header.VectorDimension != dim) mismatches.Add(Describe("dim", header.VectorDimension, dim));
            if (header.MaxCount != max) mismatches.Add(Describe("max", header.MaxCount, max));
            if (header.MaxNeighbors != maxNeighbors) mismatches.Add(Describe("maxNeighbors", header.MaxNeighbors, maxNeighbors));
            if (header.MaxLayers != maxLayers) mismatches.Add(Describe("maxLayers", header.MaxLayers, maxLayers));
            if (header.DistanceFunction != distanceFunction)
                mismatches.Add($"distanceFunction: file has {header.DistanceFunction}, caller requested {distanceFunction}");

            if (mismatches.Count > 0)
            {
                throw new QvecFormatException(
                    $"'{path}' was created with different parameters than requested " +
                    $"({string.Join("; ", mismatches)}). Reopen with the original parameters, " +
                    $"or use QvecDatabase.Open(path) to adopt the values stored in the file.");
            }
        }

        // --- PUBLIKT KONTRAKT / VALIDERING ---

        private bool _headerDirty;

        /// <summary>
        /// Publishes a header that says "a write is in progress". Called before data is
        /// written so that a crash mid-write leaves the flag set on disk and the next
        /// <see cref="Open"/> refuses the file instead of reading half-written data as if it
        /// were sound. Cleared by <see cref="CommitHeader"/> once the data is complete.
        /// <para>
        /// This deliberately does not flush. Ordering within the mapped file is enough to make
        /// a process crash detectable, and forcing 4 KiB to disk before every row would cost
        /// far more than the guarantee is worth. <see cref="Flush"/> is the durability barrier
        /// for a machine-level crash.
        /// </para>
        /// Callers must hold the write lock.
        /// </summary>
        private void BeginWrite()
        {
            if (_headerDirty) return;

            _header.WriteInProgress = 1;
            WriteHeaderImage();
            _headerDirty = true;
        }

        /// <summary>
        /// Serialises the in-memory header and publishes it as the file's committed state.
        /// The CRC-32 is recomputed over the whole 4 KiB header region on every commit, so a
        /// torn or tampered header is detected the next time the file is opened.
        /// Callers must hold the write lock.
        /// </summary>
        private void CommitHeader()
        {
            _header.Generation++;
            _header.WriteInProgress = 0;
            _header.UpdatedUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteHeaderImage();
            _headerDirty = false;
        }

        private void WriteHeaderImage()
        {
            _header.WriteTo(HeaderSpan);
        }

        /// <summary>
        /// The header region as a directly addressable span. Going through
        /// <c>MemoryMappedViewAccessor.WriteArray</c> marshals one element at a time, which
        /// made every insert pay for 4 KiB of per-byte marshalling.
        /// </summary>
        private unsafe Span<byte> HeaderSpan
        {
            get
            {
                if (_headerBasePtr == null)
                {
                    _headerAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _headerBasePtr);
                }
                return new Span<byte>(_headerBasePtr, HeaderSize);
            }
        }

        /// <summary>
        /// Flushes every pending write to disk. Data views are flushed before the header so a
        /// crash can never leave a committed header pointing at data that was never written.
        /// </summary>
        public void Flush()
        {
            _lock.EnterWriteLock();
            try
            {
                _dataAccessor.Flush();
                CommitHeader();
                _headerAccessor.Flush();
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Default search breadth. The previous default of 50 measured only ~80% recall@1 at
        /// maxNeighbors=32; 200 reaches ~99% with a modest latency cost, which is the right
        /// trade-off for a database whose whole purpose is finding the nearest vector.
        /// </summary>
        public const int DefaultEfSearch = 200;

        /// <summary>
        /// Default build-time breadth. Previously this was hardcoded to MaxNeighbors, far below
        /// the ~200 used by reference HNSW implementations, which produced a poorly connected
        /// graph that no amount of search-time tuning could fully compensate for.
        /// </summary>
        public const int DefaultEfConstruction = 200;

        private int _efConstruction = DefaultEfConstruction;

        /// <summary>
        /// Breadth of the candidate search performed while inserting. Higher values build a
        /// better-connected graph at the cost of slower inserts. Not persisted: it affects only
        /// how future inserts behave, not how existing data is read.
        /// </summary>
        public int EfConstruction
        {
            get => _efConstruction;
            set
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value, _header.MaxNeighbors);
                _efConstruction = value;
            }
        }

        /// <summary>Vektordimensionen databasen skapades med.</summary>
        public int VectorDimension => _header.VectorDimension;

        /// <summary>The distance function this database was created with, read from its header.</summary>
        public DistanceFunction DistanceFunction => _header.DistanceFunction;

        /// <summary>Maximalt antal poster databasen kan rymma.</summary>
        public int MaxCount => _header.MaxCount;

        /// <summary>Antal soft-deletade poster.</summary>
        public int DeletedCount => _header.DeletedCount;

        /// <summary>Antal levande (icke-raderade) poster.</summary>
        public int LiveCount => _header.CurrentCount - _header.DeletedCount;

        private void ValidateVector(float[] vector, string paramName)
        {
            ArgumentNullException.ThrowIfNull(vector, paramName);
            if (vector.Length != _header.VectorDimension)
                throw new QvecDimensionException(_header.VectorDimension, vector.Length, paramName);
        }

        private static void ValidateTopK(int topK)
        {
            if (topK <= 0)
                throw new ArgumentOutOfRangeException(nameof(topK), topK, "topK must be greater than zero.");
        }

        private static void ValidateEfSearch(int efSearch)
        {
            if (efSearch <= 0)
                throw new ArgumentOutOfRangeException(nameof(efSearch), efSearch, "efSearch must be greater than zero.");
        }

        /// <summary>
        /// True när det inte finns någon levande post att söka bland. Används för att
        /// undvika att HNSW-navigeringen seedar resultatet med en obefintlig nod 0.
        /// </summary>
        private bool IsEffectivelyEmpty => _header.CurrentCount == 0 || LiveCount <= 0;

        /// <summary>
        /// Returnerar en giltig startnod för HNSW-navigering, eller -1 om ingen finns.
        /// Skyddar mot att EntryPoint pekar på en raderad nod.
        /// </summary>
        private int ResolveEntryPoint()
        {
            int entryPoint = _header.EntryPoint;
            if (entryPoint >= 0 && entryPoint < _header.CurrentCount && !_deletedIndices.Contains(entryPoint))
                return entryPoint;

            for (int i = 0; i < _header.CurrentCount; i++)
            {
                if (!_deletedIndices.Contains(i)) return i;
            }
            return -1;
        }

        // --- KÄRNA: SIMD MATEMATIK ---
        public static float DotProduct(float[] left, float[] right)
        {
            return DotProduct(left, right, left.Length);
        }

        internal static float DotProduct(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            int i = 0;
            float dot = 0;
            int vectorSize = Vector<float>.Count;
            int count = Math.Min(left.Length, right.Length);

            for (; i <= count - vectorSize; i += vectorSize)
            {
                dot += Vector.Dot(
                    new Vector<float>(left.Slice(i, vectorSize)),
                    new Vector<float>(right.Slice(i, vectorSize)));
            }
            for (; i < count; i++) dot += left[i] * right[i];
            return dot;
        }

        public static float DotProduct(float[] left, float[] right, int count)
        {
            int i = 0;
            float dot = 0;
            int vectorSize = Vector<float>.Count;

            for (; i <= count - vectorSize; i += vectorSize)
            {
                dot += Vector.Dot(new Vector<float>(left, i), new Vector<float>(right, i));
            }
            for (; i < count; i++) dot += left[i] * right[i];
            return dot;
        }

        /// <summary>
        /// Returns a vector suitable for scoring against stored data. For cosine distance the
        /// vector must be unit length, but normalising the caller's array in place silently
        /// corrupted their data -- callers reusing a buffer got a mutated vector back. A copy
        /// is made instead; for dot product the original is returned untouched.
        /// </summary>
        private float[] PrepareVector(float[] vector)
        {
            if (_header.DistanceFunction != DistanceFunction.Cosine) return vector;

            var copy = new float[vector.Length];
            Array.Copy(vector, copy, vector.Length);
            NormalizeVector(copy);
            return copy;
        }

        /// <summary>
        /// Normaliserar en vektor till enhetslängd (L2-norm = 1).
        /// När vektorer är normaliserade blir dot product == cosine similarity.
        /// </summary>
        public static void NormalizeVector(float[] vector)
        {
            float norm = MathF.Sqrt(DotProduct(vector, vector));
            if (norm > 0f)
            {
                float invNorm = 1f / norm;
                int i = 0;
                int vectorSize = Vector<float>.Count;
                var invNormVec = new Vector<float>(invNorm);

                for (; i <= vector.Length - vectorSize; i += vectorSize)
                {
                    var v = new Vector<float>(vector, i);
                    (v * invNormVec).CopyTo(vector, i);
                }
                for (; i < vector.Length; i++) vector[i] *= invNorm;
            }
        }

        // --- SKRIVNING ---

        /// <summary>
        /// Reserves a row for a new entry, reusing a tombstoned slot when one is available.
        /// Without reuse, deleting an entry permanently consumed capacity: a database could
        /// report zero live entries and still refuse every insert.
        /// </summary>
        private int AllocateSlot()
        {
            if (_deletedIndices.Count > 0)
            {
                // Lowest index first, so the file stays as compact as possible.
                int slot = _deletedIndices.Min();
                _deletedIndices.Remove(slot);
                WriteTombstone(slot, 0);
                _header.DeletedCount--;
                return slot;
            }

            if (_header.CurrentCount >= _header.MaxCount)
            {
                if (!AutoGrow) throw new QvecFullException(_header.MaxCount);
                Grow(_header.MaxCountRaw + 1, _header.MetadataHeapUsed);
            }

            return _header.CurrentCount++;
        }

        /// <summary>
        /// Rebuilds the database in place, dropping everything that deletes and updates left
        /// behind: tombstoned rows, the graph edges that pointed at them, and orphaned metadata
        /// blobs in the append-only heap.
        /// <para>
        /// Deleting a row only marks it; updating a vector writes a new row and tombstones the
        /// old one; updating metadata appends a new blob and orphans the previous one. None of
        /// that space comes back on its own. Vacuum is what reclaims it, and it is also the only
        /// operation that rebuilds the HNSW graph from scratch, so a database whose graph has
        /// degraded through many deletions gets its recall back.
        /// </para>
        /// <para>
        /// The rebuild happens in a sibling file that replaces the original only once it is
        /// complete, so an interrupted vacuum leaves the original database untouched. External
        /// ids are preserved; row indices are not, so any inverted field index is remapped.
        /// </para>
        /// </summary>
        public void Vacuum()
        {
            _lock.EnterWriteLock();
            try
            {
                var live = new List<(int OldIndex, Guid Id, float[] Vector, string Metadata)>();
                for (int i = 0; i < _header.CurrentCount; i++)
                {
                    if (_deletedIndices.Contains(i)) continue;
                    var exact = new float[_header.VectorDimension];
                    ReadVectorInto(i, exact, 0);
                    live.Add((i, ReadGuidFromDisk(i), exact, GetMetadata(i)));
                }

                string rebuiltPath = _path + ".vacuum";
                if (File.Exists(rebuiltPath)) File.Delete(rebuiltPath);

                try
                {
                    using (var rebuilt = new QvecDatabase(
                        rebuiltPath,
                        _header.VectorDimension,
                        _header.MaxCount,
                        _header.MaxNeighbors,
                        _header.MaxLayers,
                        _header.DistanceFunction))
                    {
                        foreach (var entry in live)
                        {
                            rebuilt.AddEntry(entry.Vector, entry.Metadata, entry.Id);
                        }
                    }

                    ReleaseMapping();
                    File.Move(rebuiltPath, _path, overwrite: true);
                }
                catch
                {
                    if (File.Exists(rebuiltPath)) TryDelete(rebuiltPath);
                    throw;
                }

                RemapFromDisk();
                RemapFieldIndex(live.Select(e => e.OldIndex).ToArray());
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Row indices are assigned sequentially by the rebuild, so the entry that was at
        /// <c>oldIndices[n]</c> is now at <c>n</c>. Without this the inverted index would point
        /// at rows that now hold completely different entries.
        /// </summary>
        private void RemapFieldIndex(int[] oldIndices)
        {
            if (_fieldIndex.Count == 0) return;

            var translation = new Dictionary<int, int>(oldIndices.Length);
            for (int newIndex = 0; newIndex < oldIndices.Length; newIndex++)
                translation[oldIndices[newIndex]] = newIndex;

            foreach (var valueMap in _fieldIndex.Values)
            {
                foreach (var (value, indices) in valueMap.ToList())
                {
                    var translated = new HashSet<int>();
                    foreach (int old in indices)
                    {
                        if (translation.TryGetValue(old, out int fresh)) translated.Add(fresh);
                    }

                    if (translated.Count == 0) valueMap.Remove(value);
                    else valueMap[value] = translated;
                }
            }
        }

        /// <summary>
        /// Reads the current section offsets out of the header's own section table. Called on
        /// open and again after every grow, because growing moves every section.
        /// </summary>
        private void CaptureSectionOffsets()
        {
            _vectorSectionOffset = _header.GetRequiredSection(V4SectionIds.Vectors).Offset;
            _graphSectionOffset = _header.GetRequiredSection(V4SectionIds.Graph).Offset;
            _metadataSectionOffset = _header.GetRequiredSection(V4SectionIds.MetadataDescriptors).Offset;
            _metadataHeapOffset = _header.GetRequiredSection(V4SectionIds.MetadataHeap).Offset;
            _guidSectionOffset = _header.GetRequiredSection(V4SectionIds.Guids).Offset;
            _tombstoneSectionOffset = _header.GetRequiredSection(V4SectionIds.Tombstones).Offset;
            _freeListSectionOffset = _header.GetRequiredSection(V4SectionIds.FreeList).Offset;
        }

        /// <summary>
        /// Grows the file so it can hold <paramref name="requiredMaxCount"/> rows and a metadata
        /// heap of at least <paramref name="requiredHeapEnd"/> bytes.
        /// <para>
        /// Growth is geometric, so filling a database costs amortised constant time per insert
        /// even though each grow rewrites the file. The whole operation happens under the write
        /// lock, which excludes readers, so a reader sees either the old mapping or the new one
        /// and never a half-finished remap.
        /// </para>
        /// <para>
        /// The header is left marked <c>WriteInProgress</c> for the duration. If the process dies
        /// mid-grow the file is left structurally inconsistent, and the flag is what makes the
        /// next <see cref="Open"/> refuse it rather than read scrambled data.
        /// </para>
        /// Callers must hold the write lock.
        /// </summary>
        private void Grow(long requiredMaxCount, long requiredHeapEnd)
        {
            long newMaxCount = Math.Max(
                requiredMaxCount,
                QvecFormatLayout.RecommendGrownMaxCount(_header.MaxCountRaw));

            long newHeapCapacity = _header.MetadataHeapCapacity;
            if (requiredHeapEnd > newHeapCapacity)
            {
                newHeapCapacity = QvecFormatLayout.RecommendGrownMetadataHeapCapacity(
                    _header.MetadataHeapCapacity, requiredHeapEnd);
            }

            var grown = QvecFormatLayout.CreateGrown(_header, newMaxCount, newHeapCapacity);

            // Publish "a write is in progress" against the OLD geometry before anything moves,
            // so a crash during the move is detectable.
            BeginWrite();
            _dataAccessor.Flush();
            _headerAccessor.Flush();

            var moves = PlanSectionMoves(_header, grown);

            ReleaseMapping();

            try
            {
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    stream.SetLength(grown.FileLength);
                    MoveSections(stream, moves);
                    stream.Flush(flushToDisk: true);
                }

                _previousMaxCount = _header.MaxCountRaw;
                _header = grown;
                RemapAfterGrow();
                InitialiseNewCapacity();
                CommitHeader();
                _headerAccessor.Flush();

                // The caller is in the middle of a write, so the header must go straight back to
                // "write in progress" -- now against the new geometry.
                BeginWrite();
            }
            catch
            {
                // The file is now in an unknown state and the mapping is gone. Reopening is the
                // only safe recovery, and it will fail loudly if the grow corrupted the file.
                RemapFromDisk();
                throw;
            }
        }

        /// <summary>
        /// Where each section lives before and after a grow. Sections are laid out in a fixed
        /// order and all of them grow, so every section moves to a higher offset.
        /// </summary>
        private readonly record struct SectionMove(
            uint SectionId, long OldOffset, long OldLength, long NewOffset, long NewLength);

        private static List<SectionMove> PlanSectionMoves(V4Header oldHeader, V4Header newHeader)
        {
            var moves = new List<SectionMove>(oldHeader.Sections.Length);

            foreach (var oldEntry in oldHeader.Sections)
            {
                if ((oldEntry.SectionFlags & SectionFlags.Present) == 0) continue;

                var newEntry = newHeader.GetRequiredSection(oldEntry.SectionId);
                moves.Add(new SectionMove(
                    oldEntry.SectionId, oldEntry.Offset, oldEntry.Length, newEntry.Offset, newEntry.Length));
            }

            return moves;
        }

        /// <summary>
        /// Copies section contents to their new offsets, back to front. Every destination is at a
        /// higher offset than its source and the regions can overlap, so moving front to back
        /// would overwrite data that has not been copied yet.
        /// </summary>
        private static void MoveSections(FileStream stream, List<SectionMove> moves)
        {
            const int ChunkSize = 4 * 1024 * 1024;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

            try
            {
                foreach (var move in moves.OrderByDescending(m => m.OldOffset))
                {
                    if (move.NewOffset == move.OldOffset) continue;

                    long remaining = Math.Min(move.OldLength, move.NewLength);

                    // Copy the tail first so an overlapping forward move cannot clobber itself.
                    while (remaining > 0)
                    {
                        int chunk = (int)Math.Min(remaining, ChunkSize);
                        long sourceAt = move.OldOffset + remaining - chunk;
                        long destinationAt = move.NewOffset + remaining - chunk;

                        stream.Position = sourceAt;
                        stream.ReadExactly(buffer, 0, chunk);

                        stream.Position = destinationAt;
                        stream.Write(buffer, 0, chunk);

                        remaining -= chunk;
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        /// <summary>
        /// Zero is a valid value for most sections, but graph neighbours and the free list use -1
        /// as "empty". A freshly grown region is zero-filled by the file system, so those two
        /// sections have to be initialised explicitly or row 0 would look like every new node's
        /// neighbour.
        /// </summary>
        private void InitialiseNewCapacity()
        {
            for (int index = (int)_previousMaxCount; index < _header.MaxCount; index++)
            {
                InitNeighborsOnDisk(index);
            }
        }

        /// <summary>Row capacity before the grow in progress, used to initialise only the new rows.</summary>
        private long _previousMaxCount;

        /// <summary>
        /// Re-establishes the mapping over the grown file. Unlike <see cref="RemapFromDisk"/> this
        /// keeps the in-memory header, which is the authority for the new geometry and has not
        /// been written to disk yet.
        /// </summary>
        private void RemapAfterGrow()
        {
            _mmf = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, null, _header.FileLength);
            _headerAccessor = _mmf.CreateViewAccessor(0, HeaderSize);
            _dataAccessor = _mmf.CreateViewAccessor(HeaderSize, _header.FileLength - HeaderSize);

            CaptureSectionOffsets();

            // TombstoneSet is documented as never resizing, because Contains() is read without a
            // lock. Replacing the instance is safe here only because Grow holds the write lock,
            // which excludes every reader.
            var grownTombstones = new TombstoneSet(_header.MaxCount);
            for (int i = 0; i < _previousMaxCount; i++)
            {
                if (_deletedIndices.Contains(i)) grownTombstones.Add(i);
            }
            _deletedIndices = grownTombstones;
        }

        private unsafe void ReleaseMapping()        {
            if (_dataBasePtr != null)
            {
                _dataAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _dataBasePtr = null;
            }

            if (_headerBasePtr != null)
            {
                _headerAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _headerBasePtr = null;
            }

            _dataAccessor.Dispose();
            _headerAccessor.Dispose();
            _mmf.Dispose();
        }

        /// <summary>
        /// Re-establishes the mapping and every in-memory projection of it after the backing
        /// file has been replaced. The section layout is unchanged, so only the header and the
        /// derived lookups need to be reloaded.
        /// </summary>
        private void RemapFromDisk()
        {
            _header = ReadAndValidateHeader(_path);

            long totalSize = _header.FileLength;
            _mmf = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, null, totalSize);
            _headerAccessor = _mmf.CreateViewAccessor(0, HeaderSize);
            _dataAccessor = _mmf.CreateViewAccessor(HeaderSize, totalSize - HeaderSize);

            _guidIndex.Clear();
            _deletedIndices.Clear();
            RebuildGuidIndex();
            LoadTombstones();
            _headerDirty = false;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { /* best effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best effort cleanup */ }
        }

        /// <summary>
        /// Whether the file grows automatically when it runs out of row capacity or metadata heap
        /// space. On by default, so <c>max</c> is a starting size rather than a hard ceiling.
        /// <para>
        /// <see cref="PartitionedQvecDatabase"/> turns this off: partitioning exists precisely to
        /// keep individual files bounded, so a full partition must roll over to a new one rather
        /// than grow. With growth disabled a full database throws <see cref="QvecFullException"/>.
        /// </para>
        /// </summary>
        public bool AutoGrow { get; set; } = true;

        public Guid AddEntry(float[] vector, string metadata, Guid? externalId = null)
        {
            ValidateVector(vector, nameof(vector));
            ArgumentNullException.ThrowIfNull(metadata);

            _lock.EnterWriteLock();
            try
            {
                Guid docId = externalId ?? Guid.NewGuid();

                if (_guidIndex.ContainsKey(docId))
                    return docId;

                int index = AllocateSlot();
                int level = RandomLayer();

                vector = PrepareVector(vector);

                WriteVectorToDisk(index, vector);
                WriteMetadataToDisk(index, metadata);
                WriteGuidToDisk(index, docId);
                InitNeighborsOnDisk(index);

                _guidIndex[docId] = index;

                if (LiveCount == 1)
                {
                    _header.EntryPoint = index;
                    _header.EntryPointLevel = level;
                }
                else
                {
                    // If every earlier node was deleted there is nothing to attach to, so the
                    // new node has to become the entry point itself.
                    if (!ConnectNewNode(index, vector, level) || level > _header.EntryPointLevel)
                    {
                        _header.EntryPoint = index;
                        _header.EntryPointLevel = level;
                    }
                }

                CommitHeader();
                return docId;
            }
            finally { _lock.ExitWriteLock(); }
        }
        private void WriteVectorToDisk(int index, float[] vector)
        {
            BeginWrite();
            // Beräkna offset: Hoppa över Header, sedan alla tidigare vektorer
            long offset = _vectorSectionOffset + (long)index * _header.VectorDimension * sizeof(float);

            // Vi skriver arrayen direkt till den mappade vyn
            // offset - HeaderSize eftersom _dataAccessor startar efter headern
            _dataAccessor.WriteArray(offset - HeaderSize, vector, 0, _header.VectorDimension);
        }
        private void WriteMetadataToDisk(int index, string metadata)
        {
            BeginWrite();
            long descriptorPos = (_metadataSectionOffset - HeaderSize) + (long)index * MetadataDescriptorSize;
            byte[] bytes = Encoding.UTF8.GetBytes(metadata);

            if (bytes.Length == 0)
            {
                _dataAccessor.Write(descriptorPos, 0L);
                _dataAccessor.Write(descriptorPos + 8, 0);
                _dataAccessor.Write(descriptorPos + 12, 0);
                return;
            }

            // The heap is append-only: updating metadata writes a new blob and leaves the old
            // one as garbage. Vacuum reclaims it. That keeps writes single-pass and means a
            // reader never observes a half-overwritten value.
            long heapOffset = _header.MetadataHeapUsed;
            if (heapOffset + bytes.Length > _header.MetadataHeapCapacity)
            {
                if (!AutoGrow)
                {
                    throw new QvecFullException(
                        $"The metadata heap is full: {_header.MetadataHeapCapacity} bytes reserved, " +
                        $"{heapOffset} used, {bytes.Length} more requested. Run Vacuum() to reclaim " +
                        "space left behind by updated or deleted entries.");
                }

                Grow(_header.MaxCountRaw, heapOffset + bytes.Length);
                descriptorPos = (_metadataSectionOffset - HeaderSize) + (long)index * MetadataDescriptorSize;
            }

            _dataAccessor.WriteArray((_metadataHeapOffset - HeaderSize) + heapOffset, bytes, 0, bytes.Length);
            _header.MetadataHeapUsed = heapOffset + bytes.Length;

            _dataAccessor.Write(descriptorPos, heapOffset);
            _dataAccessor.Write(descriptorPos + 8, bytes.Length);
            _dataAccessor.Write(descriptorPos + 12, 0);
        }

        private void InitNeighborsOnDisk(int index)
        {
            BeginWrite();
            long offset = _graphSectionOffset + (long)index * _cachedEmptyNeighbors.Length * sizeof(int);
            _dataAccessor.WriteArray(offset - HeaderSize, _cachedEmptyNeighbors, 0, _cachedEmptyNeighbors.Length);
        }
        // --- INVERTERAT INDEX ---

        /// <summary>
        /// Lägger till en post i det inverterade indexet för ett specifikt fält och värde.
        /// </summary>
        public void AddFieldIndex(int entryIndex, IEnumerable<(string Field, string Value)> fields)
        {
            foreach (var (field, value) in fields)
            {
                if (value == null) continue;
                if (!_fieldIndex.TryGetValue(field, out var valueMap))
                {
                    valueMap = new Dictionary<string, HashSet<int>>();
                    _fieldIndex[field] = valueMap;
                }
                if (!valueMap.TryGetValue(value, out var indices))
                {
                    indices = new HashSet<int>();
                    valueMap[value] = indices;
                }
                indices.Add(entryIndex);
            }
        }

        /// <summary>
        /// Indexerar en post via dess dokument-Guid. Detta är det säkra alternativet till
        /// <see cref="AddFieldIndex(int, IEnumerable{ValueTuple{string, string}})"/>: callers
        /// previously had to guess the row number with <c>GetCount() - 1</c>, which pointed at
        /// the wrong row whenever an insert was deduplicated or reused a tombstoned slot.
        /// </summary>
        /// <returns>False if the Guid is unknown.</returns>
        public bool AddFieldIndex(Guid id, IEnumerable<(string Field, string Value)> fields)
        {
            ArgumentNullException.ThrowIfNull(fields);

            _lock.EnterWriteLock();
            try
            {
                if (!_guidIndex.TryGetValue(id, out int entryIndex)) return false;
                AddFieldIndex(entryIndex, fields);
                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Replaces every indexed term for an entry. Used when metadata changes and the
        /// previously indexed values are no longer accurate; without this, an updated entry
        /// stayed visible under its old field values and invisible under its new ones.
        /// </summary>
        /// <returns>False if the Guid is unknown.</returns>
        public bool ReindexFields(Guid id, IEnumerable<(string Field, string Value)> fields)
        {
            ArgumentNullException.ThrowIfNull(fields);

            _lock.EnterWriteLock();
            try
            {
                if (!_guidIndex.TryGetValue(id, out int entryIndex)) return false;
                RemoveFieldIndex(entryIndex);
                AddFieldIndex(entryIndex, fields);
                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Collects every (field, value) pair currently indexed for a row, so the entry can be
        /// re-registered after an update moves it to a different row.
        /// </summary>
        private List<(string Field, string Value)> GetFieldTerms(int entryIndex)
        {
            var terms = new List<(string, string)>();
            foreach (var (field, valueMap) in _fieldIndex)
            {
                foreach (var (value, indices) in valueMap)
                {
                    if (indices.Contains(entryIndex)) terms.Add((field, value));
                }
            }
            return terms;
        }

        /// <summary>
        /// Tar bort en post från det inverterade indexet.
        /// </summary>
        internal void RemoveFieldIndex(int entryIndex)
        {
            foreach (var valueMap in _fieldIndex.Values)
            {
                foreach (var indices in valueMap.Values)
                {
                    indices.Remove(entryIndex);
                }
            }
        }

        /// <summary>
        /// O(1)-sökning via det inverterade indexet. Kräver att fältet har indexerats vid insert.
        /// </summary>
        public List<(Guid Id, string Metadata)> WhereIndexed(string field, string value, int maxResults = 100)
        {
            _lock.EnterReadLock();
            try
            {
                if (!_fieldIndex.TryGetValue(field, out var valueMap) ||
                    !valueMap.TryGetValue(value, out var indices))
                    return new List<(Guid Id, string Metadata)>();

                return indices
                    .Where(i => !_deletedIndices.Contains(i))
                    .Take(maxResults)
                    .Select(i => (ReadGuidFromDisk(i), GetMetadata(i)))
                    .ToList();
            }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>
        /// O(1)-sökning via det inverterade indexet med flera fält (AND / intersection).
        /// </summary>
        public List<(Guid Id, string Metadata)> WhereIndexed(IReadOnlyList<(string Field, string Value)> lookups, int maxResults = 100)
        {
            if (lookups.Count == 0) return new List<(Guid Id, string Metadata)>();
            if (lookups.Count == 1) return WhereIndexed(lookups[0].Field, lookups[0].Value, maxResults);

            _lock.EnterReadLock();
            try
            {
                HashSet<int>? result = null;

                foreach (var (field, value) in lookups)
                {
                    if (!_fieldIndex.TryGetValue(field, out var valueMap) ||
                        !valueMap.TryGetValue(value, out var indices))
                        return new List<(Guid Id, string Metadata)>();

                    if (result == null)
                        result = new HashSet<int>(indices);
                    else
                        result.IntersectWith(indices);

                    if (result.Count == 0) return new List<(Guid Id, string Metadata)>();
                }

                return result!
                    .Where(i => !_deletedIndices.Contains(i))
                    .Take(maxResults)
                    .Select(i => (ReadGuidFromDisk(i), GetMetadata(i)))
                    .ToList();
            }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>
        /// Bygger om det inverterade indexet från disk. Anropas vid uppstart om extractor finns.
        /// </summary>
        public void RebuildFieldIndex(Func<string, IEnumerable<(string Field, string Value)>> extractor)
        {
            _lock.EnterWriteLock();
            try
            {
                _fieldIndex.Clear();
                for (int i = 0; i < _header.CurrentCount; i++)
                {
                    if (_deletedIndices.Contains(i)) continue;
                    string meta = GetMetadata(i);
                    var fields = extractor(meta);
                    AddFieldIndex(i, fields);
                }
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Returnerar kandidat-index från det inverterade indexet för en eller flera fält (AND).
        /// Returnerar null om något fält saknar matchning.
        /// </summary>
        public HashSet<int>? GetIndexedCandidates(IReadOnlyList<(string Field, string Value)> lookups)
        {
            if (lookups.Count == 0) return null;

            HashSet<int>? result = null;

            foreach (var (field, value) in lookups)
            {
                if (!_fieldIndex.TryGetValue(field, out var valueMap) ||
                    !valueMap.TryGetValue(value, out var indices))
                    return new HashSet<int>();

                if (result == null)
                    result = new HashSet<int>(indices);
                else
                    result.IntersectWith(indices);

                if (result.Count == 0) return result;
            }

            return result;
        }

        /// <summary>
        /// Vektorsökning enbart över förfiltrerade kandidat-index.
        /// Beräknar similarity enbart för poster i candidates — ingen HNSW, ingen full scan.
        /// </summary>
        public List<(Guid Id, float Score, string Metadata)> SearchWithCandidates(float[] query, HashSet<int> candidates, int topK)
        {
            ValidateVector(query, nameof(query));
            ArgumentNullException.ThrowIfNull(candidates);
            ValidateTopK(topK);

            _lock.EnterReadLock();
            try
            {
                query = PrepareVector(query);

                var scored = new List<(int Index, float Score)>(candidates.Count);
                float[] v = ArrayPool<float>.Shared.Rent(_header.VectorDimension);
                try
                {
                    foreach (int i in candidates)
                    {
                        if (_deletedIndices.Contains(i)) continue;

                        long offset = (long)i * _header.VectorDimension * sizeof(float);
                        _dataAccessor.ReadArray(offset, v, 0, _header.VectorDimension);
                        float score = DotProduct(query, v, _header.VectorDimension);
                        scored.Add((i, score));
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(v);
                }

                return scored
                    .OrderByDescending(s => s.Score)
                    .Take(topK)
                    .Select(s => (ReadGuidFromDisk(s.Index), s.Score, GetMetadata(s.Index)))
                    .ToList();
            }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>
        /// Filtrerar poster enbart på metadata utan vektorsökning.
        /// Använder parallell scan för stora dataset, sekventiell för små.
        /// </summary>
        public List<(Guid Id, string Metadata)> Where(Func<string, bool> filter, int maxResults = 100)
        {
            _lock.EnterReadLock();
            try
            {
                int count = _header.CurrentCount;

                if (count < 512)
                    return WhereSequential(filter, maxResults);

                return WhereParallelInternal(filter, maxResults, count);
            }
            finally { _lock.ExitReadLock(); }
        }

        private List<(Guid Id, string Metadata)> WhereSequential(Func<string, bool> filter, int maxResults)
        {
            var results = new List<(Guid Id, string Metadata)>();
            for (int i = 0; i < _header.CurrentCount; i++)
            {
                if (_deletedIndices.Contains(i)) continue;
                string meta = GetMetadata(i);
                if (filter(meta))
                {
                    results.Add((ReadGuidFromDisk(i), meta));
                    if (results.Count >= maxResults) break;
                }
            }
            return results;
        }

        private List<(Guid Id, string Metadata)> WhereParallelInternal(Func<string, bool> filter, int maxResults, int count)
        {
            var matches = new ConcurrentBag<(int Index, Guid Id, string Metadata)>();

            Parallel.For(0, count, (i, state) =>
            {
                if (matches.Count >= maxResults)
                {
                    state.Stop();
                    return;
                }
                if (_deletedIndices.Contains(i)) return;

                string meta = GetMetadata(i);
                if (filter(meta))
                {
                    matches.Add((i, ReadGuidFromDisk(i), meta));
                }
            });

            return matches
                .OrderBy(m => m.Index)
                .Take(maxResults)
                .Select(m => (m.Id, m.Metadata))
                .ToList();
        }

        /// <summary>
        /// SimpleSearch är en grundläggande linjär sökning som inte utnyttjar HNSW-graf
        /// </summary>
        /// <param name="query"></param>
        /// <param name="topK"></param>
        /// <param name="filter"></param>
        /// <returns></returns>
        public List<(Guid Id, float Score, string Metadata)> SearchSimple(float[] query, int topK, Func<string, bool>? filter = null)
        {
            ValidateVector(query, nameof(query));
            ValidateTopK(topK);

            _lock.EnterReadLock();
            try
            {
                if (IsEffectivelyEmpty) return new List<(Guid, float, string)>();

                query = PrepareVector(query);

                var candidates = new List<(int Index, float Score)>();
                for (int i = 0; i < _header.CurrentCount; i++)
                {
                    if (_deletedIndices.Contains(i)) continue;
                    string meta = GetMetadata(i);
                    if (filter != null && !filter(meta)) continue;

                    float[] v = ArrayPool<float>.Shared.Rent(_header.VectorDimension);
                    try
                    {
                        _dataAccessor.ReadArray((long)i * _header.VectorDimension * sizeof(float), v, 0, _header.VectorDimension);
                        float score = DotProduct(query, v, _header.VectorDimension);
                        candidates.Add((i, score));
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(v);
                    }
                }

                return candidates.OrderByDescending(c => c.Score)
                                 .Take(topK)
                                 .Select(c => (ReadGuidFromDisk(c.Index), c.Score, GetMetadata(c.Index)))
                                 .ToList();
            }
            finally { _lock.ExitReadLock(); }
        }
        /// <summary>
        /// SearchSimpleParallel är en optimerad version av SearchSimple som utnyttjar alla CPU-kärnor.
        /// </summary>
        /// <param name="query"></param>
        /// <param name="topK"></param>
        /// <param name="filter"></param>
        /// <returns></returns>
        public unsafe List<(Guid Id, float Score, string Metadata)> SearchSimpleParallel(float[] query, int topK, Func<string, bool>? filter = null)
        {
            ValidateVector(query, nameof(query));
            ValidateTopK(topK);

            // This method previously took no lock at all, so a writer could commit while the
            // parallel scan was reading the mapped file. Every sibling search takes the read
            // lock; this one now does too.
            _lock.EnterReadLock();
            try
            {
                return SearchSimpleParallelCore(query, topK, filter);
            }
            finally { _lock.ExitReadLock(); }
        }

        private unsafe List<(Guid Id, float Score, string Metadata)> SearchSimpleParallelCore(float[] query, int topK, Func<string, bool>? filter)
        {
            if (IsEffectivelyEmpty) return new List<(Guid, float, string)>();

            query = PrepareVector(query);

            int count = _header.CurrentCount;
            int dim = _header.VectorDimension;

            var partialResults = new (int Index, float Score)[count];

            byte* basePtr = null;
            _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

            try
            {
                basePtr += _dataAccessor.PointerOffset;

                // The vector section does not necessarily start at the beginning of the data
                // accessor. It happens to today, which is exactly why omitting this term was a
                // latent bug rather than a visible one.
                float* vectorBasePtr = (float*)(basePtr + (_vectorSectionOffset - HeaderSize));

                Parallel.For(0, count, i =>
                {
                    if (_deletedIndices.Contains(i))
                    {
                        partialResults[i] = (i, float.MinValue);
                        return;
                    }
                    string meta = GetMetadata(i);
                    if (filter != null && !filter(meta))
                    {
                        partialResults[i] = (i, float.MinValue);
                        return;
                    }

                    float* currentVecPtr = vectorBasePtr + (i * dim);

                    float score = DotProductUnsafe(query, currentVecPtr, dim);
                    partialResults[i] = (i, score);
                });
            }
            finally
            {
                _dataAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            return partialResults
                .Where(r => r.Score > float.MinValue)
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .Select(r => (ReadGuidFromDisk(r.Index), r.Score, GetMetadata(r.Index)))
                .ToList();
        }
        /// <summary>
        /// HybridHNSW är en sökmetod som kombinerar den snabba HNSW-navigeringen med möjligheten att filtrera på metadata under sökprocessen.
        /// </summary>
        /// <param name="query"></param>
        /// <param name="filter"></param>
        /// <param name="topK"></param>
        /// <returns></returns>
        public List<(Guid Id, float Score, string Metadata)> Search(float[] query, Func<string, bool> filter, int topK = 5, int efSearch = DefaultEfSearch)
        {
            ValidateVector(query, nameof(query));
            ArgumentNullException.ThrowIfNull(filter);
            ValidateTopK(topK);
            ValidateEfSearch(efSearch);

            _lock.EnterReadLock();
            try
            {
                if (IsEffectivelyEmpty) return new List<(Guid, float, string)>();

                query = PrepareVector(query);

                int entryPoint = ResolveEntryPoint();
                if (entryPoint < 0) return new List<(Guid, float, string)>();

                for (int level = _header.EntryPointLevel; level >= 1; level--)
                {
                    entryPoint = GreedyClosest(query, entryPoint, level);
                }

                int liveCount = LiveCount;
                int ef = Math.Max(topK, efSearch);

                // Filtering happens inside the graph walk (see SearchLayerFiltered), so a
                // selective filter no longer starves the result set the way post-filtering did.
                // The budget keeps a pathologically selective filter from touring the whole graph
                // more expensively than a linear scan would cost.
                int visitBudget = Math.Min(liveCount, Math.Max(ef * 16, 1024));

                var nearest = SearchLayerFiltered(
                    query, entryPoint, 0, ef, filter, visitBudget, out _);

                var matches = nearest
                    .OrderByDescending(r => r.Score)
                    .Take(topK)
                    .Select(r => (ReadGuidFromDisk(r.Id), r.Score, r.Meta))
                    .ToList();

                if (matches.Count >= topK) return matches;

                // Short of topK. Either the walk hit its budget, or matching entries are genuinely
                // unreachable from the entry point: a distant cluster can end up with outgoing
                // edges only, so no walk will ever reach it. Neither case is distinguishable
                // cheaply, and under-returning silently is the worse failure, so fall back to an
                // exact scan. This only costs O(N) when the filter really does match fewer than
                // topK reachable entries.
                return ExhaustiveFilteredSearch(query, filter, topK);
            }
            finally { _lock.ExitReadLock(); }
        }

        private long _filteredFallbackCount;

        /// <summary>
        /// Number of filtered searches that had to fall back to an exhaustive scan because the
        /// graph walk could not find <c>topK</c> matches. Exposed for diagnostics and tests: a
        /// persistently non-zero value on non-selective filters means the graph is poorly
        /// connected, and the filtered search is silently costing O(N) per query.
        /// </summary>
        public long FilteredSearchFallbackCount => Interlocked.Read(ref _filteredFallbackCount);

        /// <summary>
        /// Linear, filter-aware scan used as the correctness backstop for filtered search.
        /// The caller must already hold the read lock and have prepared the query vector.
        /// </summary>
        private List<(Guid Id, float Score, string Metadata)> ExhaustiveFilteredSearch(
            float[] query, Func<string, bool> filter, int topK)
        {
            Interlocked.Increment(ref _filteredFallbackCount);

            var matches = new List<(int Index, float Score, string Meta)>();

            for (int i = 0; i < _header.CurrentCount; i++)
            {
                if (_deletedIndices.Contains(i)) continue;

                string meta = GetMetadata(i);
                if (!filter(meta)) continue;

                float[] vector = GetVector(i);
                try
                {
                    matches.Add((i, DotProduct(query, vector, _header.VectorDimension), meta));
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(vector);
                }
            }

            return matches.OrderByDescending(m => m.Score)
                          .Take(topK)
                          .Select(m => (ReadGuidFromDisk(m.Index), m.Score, m.Meta))
                          .ToList();
        }

        /// <summary>
        /// HNSW search som navigerar genom lagren och returnerar en lista med de bästa matchningarna, inklusive metadata.
        /// </summary>
        /// <param name="query"></param>
        /// <param name="topK"></param>
        /// <param name="efSearch">Sökbredd i bottenlagret. Högre = bättre recall men långsammare. Standard: 200.</param>
        /// <returns></returns>
        public List<(Guid Id, float Score, string Metadata)> Search(float[] query, int topK = 5, int efSearch = DefaultEfSearch)
        {
            ValidateVector(query, nameof(query));
            ValidateTopK(topK);
            ValidateEfSearch(efSearch);

            _lock.EnterReadLock();
            try
            {
                if (IsEffectivelyEmpty) return new List<(Guid, float, string)>();

                query = PrepareVector(query);

                int entryPoint = ResolveEntryPoint();
                if (entryPoint < 0) return new List<(Guid, float, string)>();

                for (int level = _header.EntryPointLevel; level >= 1; level--)
                {
                    entryPoint = GreedyClosest(query, entryPoint, level);
                }

                int ef = Math.Max(topK, efSearch);
                var nearest = SearchLayerNearest(query, entryPoint, 0, ef);

                return nearest
                    .OrderByDescending(r => r.Score)
                    .Take(topK)
                    .Select(r => (ReadGuidFromDisk(r.Id), r.Score, GetMetadata(r.Id)))
                    .ToList();
            }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>
        /// Wires a freshly written node into the graph. Returns false when there is no live
        /// node to attach to, in which case the caller must promote the new node to entry point.
        /// </summary>
        private bool ConnectNewNode(int newIndex, float[] newVector, int newLevel)
        {
            int currentElement = ResolveEntryPoint();
            if (currentElement < 0 || currentElement == newIndex) return false;
            for (int level = _header.EntryPointLevel; level > newLevel; level--)
            {
                currentElement = GreedyClosest(newVector, currentElement, level);
            }

            for (int level = Math.Min(newLevel, _header.MaxLayers - 1); level >= 0; level--)
            {
                var candidates = SearchLayerNearest(newVector, currentElement, level, EfConstruction);
                var nearest = SelectNeighborsHeuristic(candidates, _header.MaxNeighbors);

                WriteNeighborsAtLevel(newIndex, level, nearest);

                foreach (var (neighborId, _) in nearest)
                {
                    if (neighborId < 0) break;
                    AddNeighborConnection(neighborId, level, newIndex, newVector);
                }

                if (candidates.Length > 0 && candidates[0].Id >= 0)
                {
                    currentElement = candidates[0].Id;
                }
            }

            return true;
        }

        /// <summary>
        /// Malkov &amp; Yashunin algorithm 4: pick <paramref name="m"/> neighbours out of a larger
        /// candidate set, preferring candidates that are closer to the new node than to any
        /// already-selected neighbour. Taking the plain top-m instead produces clustered,
        /// redundant links and leaves parts of the graph unreachable, which is the main reason
        /// recall was far below target at default parameters.
        /// </summary>
        private (int Id, float Score)[] SelectNeighborsHeuristic((int Id, float Score)[] candidates, int m)
        {
            if (candidates.Length <= m) return candidates;

            var ordered = candidates.Where(c => c.Id >= 0)
                                    .OrderByDescending(c => c.Score)
                                    .ToArray();

            if (ordered.Length <= m) return ordered;

            int dim = _header.VectorDimension;

            // Candidate vectors are loaded once into a contiguous buffer. Re-reading them from
            // the mapped file inside the O(m^2) comparison loop made every insert dominated by
            // I/O and turned index construction into the slowest part of the library.
            float[] vectors = ArrayPool<float>.Shared.Rent(ordered.Length * dim);
            try
            {
                for (int i = 0; i < ordered.Length; i++)
                    ReadVectorInto(ordered[i].Id, vectors, i * dim);

                return PruneNeighbors(ordered, vectors, dim, m);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(vectors);
            }
        }

        /// <summary>
        /// Neighbour selection heuristic from Malkov &amp; Yashunin, algorithm 4.
        /// Keeps a candidate only when it is closer to the owning node than to any candidate
        /// already selected, which preserves long-range links instead of collapsing the
        /// neighbourhood into a single tight cluster.
        /// </summary>
        /// <param name="ordered">Candidates sorted by descending similarity to the owner.</param>
        /// <param name="vectors">Candidate vectors, laid out contiguously in <paramref name="ordered"/> order.</param>
        private static (int Id, float Score)[] PruneNeighbors(
            (int Id, float Score)[] ordered, float[] vectors, int dim, int m)
        {
            var selected = new List<(int Id, float Score)>(m);
            var selectedSlots = new List<int>(m);
            var discarded = new List<(int Id, float Score)>();

            for (int i = 0; i < ordered.Length && selected.Count < m; i++)
            {
                var candidate = ordered[i];
                var candidateVector = vectors.AsSpan(i * dim, dim);

                bool closerToOwnerThanToNeighbours = true;
                foreach (int slot in selectedSlots)
                {
                    // Score is a similarity: higher means closer.
                    if (DotProduct(candidateVector, vectors.AsSpan(slot * dim, dim)) > candidate.Score)
                    {
                        closerToOwnerThanToNeighbours = false;
                        break;
                    }
                }

                if (closerToOwnerThanToNeighbours)
                {
                    selected.Add(candidate);
                    selectedSlots.Add(i);
                }
                else
                {
                    discarded.Add(candidate);
                }
            }

            // keepPrunedConnections: top up with the best discarded candidates rather than
            // returning an under-filled neighbour list.
            for (int i = 0; i < discarded.Count && selected.Count < m; i++)
                selected.Add(discarded[i]);

            return selected.ToArray();
        }
        private int GreedyClosest(float[] query, int entryPoint, int level)
        {
            int current = entryPoint;
            float currentScore = CalculateScore(query, current);
            int[] neighbors = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    GetNeighborsAtLevel(current, level, neighbors);
                    for (int j = 0; j < _header.MaxNeighbors; j++)
                    {
                        if (neighbors[j] == -1) break;
                        if (_deletedIndices.Contains(neighbors[j])) continue;
                        float score = CalculateScore(query, neighbors[j]);
                        if (score > currentScore)
                        {
                            currentScore = score;
                            current = neighbors[j];
                            changed = true;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighbors);
            }
            return current;
        }
        private (int Id, float Score)[] SearchLayerNearest(float[] query, int entryPoint, int level, int ef)
        {
            var visited = new HashSet<int> { entryPoint };
            float entryScore = CalculateScore(query, entryPoint);

            var candidates = new PriorityQueue<int, float>();
            candidates.Enqueue(entryPoint, -entryScore);

            var results = new PriorityQueue<int, float>();
            results.Enqueue(entryPoint, entryScore);
            float worstScore = entryScore;

            int[] neighborBuffer = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                while (candidates.TryDequeue(out int candidateId, out float negScore))
                {
                    float candidateScore = -negScore;
                    if (candidateScore < worstScore && results.Count >= ef)
                        break;

                    GetNeighborsAtLevel(candidateId, level, neighborBuffer);
                    for (int j = 0; j < _header.MaxNeighbors; j++)
                    {
                        int neighbor = neighborBuffer[j];
                        if (neighbor < 0) break;
                        if (!visited.Add(neighbor)) continue;
                        if (_deletedIndices.Contains(neighbor)) continue;

                        float score = CalculateScore(query, neighbor);

                        if (results.Count < ef || score > worstScore)
                        {
                            candidates.Enqueue(neighbor, -score);
                            results.Enqueue(neighbor, score);

                            if (results.Count > ef)
                            {
                                results.Dequeue();
                            }

                            results.TryPeek(out _, out worstScore);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighborBuffer);
            }

            var resultArray = new (int Id, float Score)[results.Count];
            int idx = results.Count - 1;
            while (results.TryDequeue(out int id, out float score))
            {
                resultArray[idx--] = (id, score);
            }
            return resultArray;
        }

        /// <summary>
        /// Filter-aware layer search: the graph walk traverses through every live node, but only
        /// nodes satisfying <paramref name="filter"/> are admitted to the result set.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the difference between filtering <em>inside</em> the navigation and filtering
        /// the output of an unfiltered top-k. Post-filtering retrieves the globally nearest
        /// <c>ef</c> nodes and then discards the non-matching ones, so a selective filter can
        /// discard all of them and return nothing while good matches sat just outside the window.
        /// Here the result set fills only with matches, so the walk keeps expanding until it has
        /// <c>ef</c> of them — the frontier is free to route through non-matching nodes on the way.
        /// </para>
        /// <para>
        /// A very selective filter could otherwise drag the walk across the entire graph, which is
        /// slower than a linear scan. <paramref name="visitBudget"/> bounds the work; when it is
        /// exhausted the caller is told so via <paramref name="budgetExhausted"/> and can fall back
        /// to <see cref="ExhaustiveFilteredSearch"/>, which is O(N) but exact.
        /// </para>
        /// </remarks>
        private (int Id, float Score, string Meta)[] SearchLayerFiltered(
            float[] query, int entryPoint, int level, int ef,
            Func<string, bool> filter, int visitBudget, out bool budgetExhausted)
        {
            budgetExhausted = false;

            var visited = new HashSet<int> { entryPoint };
            var metadata = new Dictionary<int, string>();

            float entryScore = CalculateScore(query, entryPoint);

            var candidates = new PriorityQueue<int, float>();
            candidates.Enqueue(entryPoint, -entryScore);

            // Only matching nodes enter the result set; the entry point is not special-cased.
            var results = new PriorityQueue<int, float>();
            float worstScore = float.MinValue;

            void TryAdmit(int node, float score)
            {
                string meta = GetMetadata(node);
                metadata[node] = meta;
                if (!filter(meta)) return;

                if (results.Count < ef || score > worstScore)
                {
                    results.Enqueue(node, score);
                    if (results.Count > ef) results.Dequeue();
                    results.TryPeek(out _, out worstScore);
                }
            }

            if (!_deletedIndices.Contains(entryPoint))
                TryAdmit(entryPoint, entryScore);

            int[] neighborBuffer = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                while (candidates.TryDequeue(out int candidateId, out float negScore))
                {
                    // Stop only once the result set is full, since until then a worse-scoring
                    // candidate may still be the only route to a matching node.
                    if (results.Count >= ef && -negScore < worstScore)
                        break;

                    if (visited.Count > visitBudget)
                    {
                        budgetExhausted = true;
                        break;
                    }

                    GetNeighborsAtLevel(candidateId, level, neighborBuffer);
                    for (int j = 0; j < _header.MaxNeighbors; j++)
                    {
                        int neighbor = neighborBuffer[j];
                        if (neighbor < 0) break;
                        if (!visited.Add(neighbor)) continue;
                        if (_deletedIndices.Contains(neighbor)) continue;

                        float score = CalculateScore(query, neighbor);

                        // The frontier is deliberately unfiltered: a non-matching node is still a
                        // valid stepping stone towards matching ones.
                        candidates.Enqueue(neighbor, -score);
                        TryAdmit(neighbor, score);
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighborBuffer);
            }

            var resultArray = new (int Id, float Score, string Meta)[results.Count];
            int idx = results.Count - 1;
            while (results.TryDequeue(out int id, out float score))
            {
                resultArray[idx--] = (id, score, metadata[id]);
            }
            return resultArray;
        }

        private void WriteNeighborsAtLevel(int nodeIndex, int level, (int Id, float Score)[] neighbors)
        {
            BeginWrite();
            long position = (_graphSectionOffset - HeaderSize) +
                            (long)nodeIndex * _header.MaxLayers * _header.MaxNeighbors * sizeof(int) +
                            (long)level * _header.MaxNeighbors * sizeof(int);

            int count = Math.Min(neighbors.Length, _header.MaxNeighbors);
            int[] toWrite = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                for (int i = 0; i < count; i++) toWrite[i] = neighbors[i].Id;
                for (int i = count; i < _header.MaxNeighbors; i++) toWrite[i] = -1;
                _dataAccessor.WriteArray(position, toWrite, 0, _header.MaxNeighbors);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(toWrite);
            }
        }

        private void WriteNeighborsAtLevel(int nodeIndex, int level, int[] neighborIds)
        {
            BeginWrite();
            long position = (_graphSectionOffset - HeaderSize) +
                            (long)nodeIndex * _header.MaxLayers * _header.MaxNeighbors * sizeof(int) +
                            (long)level * _header.MaxNeighbors * sizeof(int);

            int count = Math.Min(neighborIds.Length, _header.MaxNeighbors);
            int[] toWrite = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                Array.Copy(neighborIds, toWrite, count);
                for (int i = count; i < _header.MaxNeighbors; i++) toWrite[i] = -1;
                _dataAccessor.WriteArray(position, toWrite, 0, _header.MaxNeighbors);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(toWrite);
            }
        }

        /// <summary>
        /// Adds a back-reference from an existing node to a newly inserted one.
        /// When the neighbour list is full the replacement decision goes through the HNSW
        /// pruning heuristic rather than a plain "evict the worst" rule. Evicting purely on
        /// score meant a tightly clustered region never accepted links from a distant cluster,
        /// leaving whole groups of entries with outgoing edges only — unreachable from the
        /// entry point, and therefore invisible to search.
        /// </summary>
        private void AddNeighborConnection(int existingNode, int level, int newNode, float[] newVector)
        {
            int[] neighbors = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                GetNeighborsAtLevel(existingNode, level, neighbors);

                for (int i = 0; i < _header.MaxNeighbors; i++)
                {
                    if (neighbors[i] == -1)
                    {
                        neighbors[i] = newNode;
                        WriteNeighborsAtLevel(existingNode, level, neighbors);
                        return;
                    }

                    if (neighbors[i] == newNode) return;
                }

                float[] existingVector = GetVector(existingNode);
                int dim = _header.VectorDimension;
                int slots = _header.MaxNeighbors + 1;
                float[] candidateVectors = ArrayPool<float>.Shared.Rent(slots * dim);
                try
                {
                    var candidates = new (int Id, float Score)[slots];

                    for (int i = 0; i < _header.MaxNeighbors; i++)
                    {
                        ReadVectorInto(neighbors[i], candidateVectors, i * dim);
                        candidates[i] = (
                            neighbors[i],
                            DotProduct(existingVector.AsSpan(0, dim), candidateVectors.AsSpan(i * dim, dim)));
                    }

                    int newSlot = _header.MaxNeighbors;
                    newVector.AsSpan(0, dim).CopyTo(candidateVectors.AsSpan(newSlot * dim, dim));
                    candidates[newSlot] = (newNode, DotProduct(existingVector, newVector, dim));

                    // Reorder candidates (and their vectors) by descending similarity so the
                    // pruning pass can run entirely in memory.
                    int[] order = Enumerable.Range(0, slots).ToArray();
                    Array.Sort(order, (a, b) => candidates[b].Score.CompareTo(candidates[a].Score));

                    var ordered = new (int Id, float Score)[slots];
                    float[] orderedVectors = ArrayPool<float>.Shared.Rent(slots * dim);
                    try
                    {
                        for (int i = 0; i < slots; i++)
                        {
                            ordered[i] = candidates[order[i]];
                            candidateVectors.AsSpan(order[i] * dim, dim)
                                            .CopyTo(orderedVectors.AsSpan(i * dim, dim));
                        }

                        var selected = PruneNeighbors(ordered, orderedVectors, dim, _header.MaxNeighbors);
                        WriteNeighborsAtLevel(existingNode, level, selected);
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(orderedVectors);
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(candidateVectors);
                    ArrayPool<float>.Shared.Return(existingVector);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighbors);
            }
        }

        private void GetNeighborsAtLevel(int nodeIndex, int level, int[] buffer)
        {
            long position = (_graphSectionOffset - HeaderSize) +
                            (long)nodeIndex * _header.MaxLayers * _header.MaxNeighbors * sizeof(int) +
                            (long)level * _header.MaxNeighbors * sizeof(int);

            _dataAccessor.ReadArray(position, buffer, 0, _header.MaxNeighbors);
        }

        private int RandomLayer()
        {
            double r = Random.Shared.NextDouble();
            if (r <= 0) r = 0.0001;

            int level = (int)(-Math.Log(r) * _header.LayerProbability);
            return Math.Min(level, _header.MaxLayers - 1);
        }
        private float CalculateScore(float[] query, int targetIndex)
        {
            float[] targetVector = ArrayPool<float>.Shared.Rent(_header.VectorDimension);
            try
            {
                long offset = (long)targetIndex * _header.VectorDimension * sizeof(float);
                _dataAccessor.ReadArray(offset, targetVector, 0, _header.VectorDimension);
                return DotProduct(query, targetVector, _header.VectorDimension);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(targetVector);
            }
        }
        /// <summary>
        /// Rebuilds the entire HNSW graph from the stored vectors.
        /// The previous implementation ran <c>Parallel.For</c> with no locking, wrote progress
        /// to <see cref="Console"/> from inside a library, included tombstoned rows and made
        /// every node its own nearest neighbour. It now runs sequentially under the write lock
        /// and re-inserts each live entry through the normal insert path.
        /// </summary>
        public void RebuildIndex()
        {
            _lock.EnterWriteLock();
            try
            {
                var live = new List<int>();
                for (int i = 0; i < _header.CurrentCount; i++)
                {
                    if (!IsDeleted(i)) live.Add(i);
                }

                for (int i = 0; i < _header.CurrentCount; i++)
                    InitNeighborsOnDisk(i);

                _header.EntryPoint = -1;
                _header.EntryPointLevel = 0;

                foreach (int index in live)
                {
                    int level = RandomLayer();
                    float[] vector = GetVector(index);
                    try
                    {
                        if (_header.EntryPoint < 0)
                        {
                            _header.EntryPoint = index;
                            _header.EntryPointLevel = level;
                            continue;
                        }

                        if (!ConnectNewNode(index, vector, level) || level > _header.EntryPointLevel)
                        {
                            _header.EntryPoint = index;
                            _header.EntryPointLevel = level;
                        }
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(vector);
                    }
                }

                CommitHeader();
            }
            finally { _lock.ExitWriteLock(); }
        }
        public int GetCount()
        {
            _lock.EnterReadLock();
            try
            {
                return _header.CurrentCount;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Re-reads the committed header off the mapped file and validates it in full,
        /// including the CRC-32 and every section-table invariant. Unlike the previous
        /// magic-number-only check, this actually detects a torn or tampered header.
        /// </summary>
        public bool IsHealthy()
        {
            _lock.EnterReadLock();
            try
            {
                V4Header onDisk = V4Header.Read(HeaderSpan, _header.FileLength);
                return onDisk.WriteInProgress == 0;
            }
            catch (QvecFormatException)
            {
                return false;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        public Dictionary<int, int> GetStats()
        {
            var stats = new Dictionary<int, int>();
            for (int l = 0; l < _header.MaxLayers; l++) stats[l] = 0;

            _lock.EnterReadLock();
            int[] neighbors = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                for (int i = 0; i < _header.CurrentCount; i++)
                {
                    if (_deletedIndices.Contains(i)) continue;
                    for (int level = 0; level < _header.MaxLayers; level++)
                    {
                        GetNeighborsAtLevel(i, level, neighbors);
                        if (_header.MaxNeighbors > 0 && neighbors[0] != -1)
                        {
                            stats[level]++;
                        }
                        else if (level == 0 && _header.CurrentCount > 0)
                        {
                            stats[0]++;
                            break;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighbors);
                _lock.ExitReadLock();
            }

            return stats;
        }
        public int GetEntryPoint()
        {
            _lock.EnterReadLock();
            try
            {
                // Vi returnerar indexet för den aktuella startpunkten från headern
                return _header.EntryPoint;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        private float[] GetVector(int index)
        {
            float[] vector = ArrayPool<float>.Shared.Rent(_header.VectorDimension);
            ReadVectorInto(index, vector, 0);
            return vector;
        }

        /// <summary>
        /// Reads a stored vector directly into a slice of a caller-supplied buffer.
        /// Uses the mapped pointer rather than <c>ReadArray</c>, whose per-element marshalling
        /// dominated index construction once the pruning heuristic was introduced.
        /// </summary>
        private unsafe void ReadVectorInto(int index, float[] destination, int destinationOffset)
        {
            int dim = _header.VectorDimension;
            long offset = _vectorSectionOffset + (long)index * dim * sizeof(float) - HeaderSize;
            long bytes = (long)dim * sizeof(float);

            byte* source = DataBasePointer + offset;
            fixed (float* destPtr = &destination[destinationOffset])
            {
                Buffer.MemoryCopy(source, destPtr, bytes, bytes);
            }
        }

        private unsafe byte* DataBasePointer
        {
            get
            {
                if (_dataBasePtr == null)
                {
                    byte* basePtr = null;
                    _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                    _dataBasePtr = basePtr + _dataAccessor.PointerOffset;
                }
                return _dataBasePtr;
            }
        }

        // SIMD DotProduct som arbetar direkt mot en rå pekare
        private static unsafe float DotProductUnsafe(float[] left, float* right, int dim)
        {
            int i = 0;
            float dot = 0;
            int vectorSize = Vector<float>.Count;

            fixed (float* pLeft = left)
            {
                for (; i <= dim - vectorSize; i += vectorSize)
                {
                    // Ladda 8 floats (AVX2) från både array och pekare samtidigt
                    var v1 = *(Vector<float>*)(pLeft + i);
                    var v2 = *(Vector<float>*)(right + i);
                    dot += Vector.Dot(v1, v2);
                }
            }

            // Resterande element
            for (; i < dim; i++) dot += left[i] * right[i];
            return dot;
        }
        private string GetMetadata(int index)
        {
            long descriptorPos = (_metadataSectionOffset - HeaderSize) + (long)index * MetadataDescriptorSize;
            long heapOffset = _dataAccessor.ReadInt64(descriptorPos);
            int length = _dataAccessor.ReadInt32(descriptorPos + 8);

            if (length <= 0) return string.Empty;

            if (heapOffset < 0 || heapOffset + length > _header.MetadataHeapCapacity)
            {
                throw new QvecFormatException(
                    $"Row {index} has a corrupt metadata descriptor: offset={heapOffset}, length={length}, " +
                    $"heap capacity={_header.MetadataHeapCapacity}.");
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                _dataAccessor.ReadArray((_metadataHeapOffset - HeaderSize) + heapOffset, buffer, 0, length);
                return Encoding.UTF8.GetString(buffer, 0, length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void WriteGuidToDisk(int index, Guid guid)
        {
            BeginWrite();
            long offset = (_guidSectionOffset - HeaderSize) + (long)index * GuidSize;
            byte[] bytes = guid.ToByteArray();
            _dataAccessor.WriteArray(offset, bytes, 0, GuidSize);
        }

        private Guid ReadGuidFromDisk(int index)
        {
            Span<byte> bytes = stackalloc byte[GuidSize];
            long offset = (_guidSectionOffset - HeaderSize) + (long)index * GuidSize;
            for (int i = 0; i < GuidSize; i++)
                bytes[i] = _dataAccessor.ReadByte(offset + i);
            return new Guid(bytes);
        }

        private void RebuildGuidIndex()
        {
            _guidIndex.Clear();
            _guidIndex.EnsureCapacity(_header.CurrentCount);
            for (int i = 0; i < _header.CurrentCount; i++)
            {
                Guid id = ReadGuidFromDisk(i);
                _guidIndex[id] = i;
            }
        }


        public (float[] Vector, string Metadata)? GetByGuid(Guid id)
        {
            _lock.EnterReadLock();
            try
            {
                if (!_guidIndex.TryGetValue(id, out int index))
                    return null;
                float[] rented = GetVector(index);
                float[] vector = rented.AsSpan(0, _header.VectorDimension).ToArray();
                ArrayPool<float>.Shared.Return(rented);
                return (vector, GetMetadata(index));
            }
            finally { _lock.ExitReadLock(); }
        }

        public int SyncFrom(QvecDatabase source)
        {
            int synced = 0;
            for (int i = 0; i < source._header.CurrentCount; i++)
            {
                if (source._deletedIndices.Contains(i)) continue;

                Guid docId = source.ReadGuidFromDisk(i);
                if (_guidIndex.ContainsKey(docId))
                    continue;

                float[] vector = source.GetVector(i);
                try
                {
                    string metadata = source.GetMetadata(i);
                    AddEntry(vector.AsSpan(0, source._header.VectorDimension).ToArray(), metadata, externalId: docId);
                    synced++;
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(vector);
                }
            }
            return synced;
        }

        public bool Delete(Guid id)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_guidIndex.TryGetValue(id, out int index))
                    return false;

                SoftDelete(index);
                _guidIndex.Remove(id);

                _header.DeletedCount++;
                CommitHeader();
                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        private void SoftDelete(int index)
        {
            WriteTombstone(index, 1);
            _deletedIndices.Add(index);
            RemoveFieldIndex(index);
            DisconnectNode(index);

            if (_header.EntryPoint == index)
            {
                FindNewEntryPoint();
            }
        }

        /// <summary>
        /// Removes a node from the graph and repairs the hole it leaves behind.
        /// Simply deleting the back-references was not enough: the removed node was often the
        /// only bridge between its neighbours, so deleting it silently partitioned the graph and
        /// made live entries unreachable from the entry point. Its neighbours are therefore
        /// cross-linked to each other before the node is unlinked.
        /// </summary>
        private void DisconnectNode(int deletedIndex)
        {
            int[] neighbors = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                for (int level = 0; level < _header.MaxLayers; level++)
                {
                    GetNeighborsAtLevel(deletedIndex, level, neighbors);

                    int count = 0;
                    while (count < _header.MaxNeighbors && neighbors[count] != -1) count++;
                    if (count == 0)
                    {
                        InitNeighborsAtLevel(deletedIndex, level);
                        continue;
                    }

                    int[] live = new int[count];
                    Array.Copy(neighbors, live, count);

                    for (int j = 0; j < count; j++)
                        RemoveNeighborReference(live[j], level, deletedIndex);

                    // Bridge the survivors so the neighbourhood stays connected.
                    for (int j = 0; j < count; j++)
                    {
                        if (IsDeleted(live[j])) continue;

                        float[] vector = GetVector(live[j]);
                        try
                        {
                            for (int k = 0; k < count; k++)
                            {
                                if (j == k || IsDeleted(live[k])) continue;
                                AddNeighborConnection(live[k], level, live[j], vector);
                            }
                        }
                        finally
                        {
                            ArrayPool<float>.Shared.Return(vector);
                        }
                    }

                    InitNeighborsAtLevel(deletedIndex, level);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighbors);
            }
        }

        private void RemoveNeighborReference(int nodeIndex, int level, int targetToRemove)
        {
            int[] neighbors = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                GetNeighborsAtLevel(nodeIndex, level, neighbors);

                // Compact out *every* occurrence. Returning after the first one left duplicate
                // references to a deleted node behind, which reintroduced tombstones into search.
                int write = 0;
                bool changed = false;
                for (int i = 0; i < _header.MaxNeighbors; i++)
                {
                    int n = neighbors[i];
                    if (n == targetToRemove) { changed = true; continue; }
                    neighbors[write++] = n;
                }

                if (!changed) return;

                for (int i = write; i < _header.MaxNeighbors; i++)
                    neighbors[i] = -1;

                WriteNeighborsAtLevel(nodeIndex, level, neighbors);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighbors);
            }
        }

        private void InitNeighborsAtLevel(int nodeIndex, int level)
        {
            BeginWrite();
            long position = (_graphSectionOffset - HeaderSize) +
                            (long)nodeIndex * _header.MaxLayers * _header.MaxNeighbors * sizeof(int) +
                            (long)level * _header.MaxNeighbors * sizeof(int);

            int[] empty = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                Array.Fill(empty, -1, 0, _header.MaxNeighbors);
                _dataAccessor.WriteArray(position, empty, 0, _header.MaxNeighbors);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(empty);
            }
        }

        /// <summary>
        /// Derives a node's top layer from the graph. The level is not stored per node, so it
        /// is recovered as the highest layer on which the node has at least one neighbour.
        /// </summary>
        private int GetNodeLevel(int nodeIndex)
        {
            int[] buffer = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                for (int level = _header.MaxLayers - 1; level > 0; level--)
                {
                    GetNeighborsAtLevel(nodeIndex, level, buffer);
                    for (int i = 0; i < _header.MaxNeighbors; i++)
                    {
                        if (buffer[i] >= 0) return level;
                    }
                }
                return 0;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Picks a replacement entry point after the current one is deleted.
        /// Chooses the live node sitting on the highest layer: taking the first live node and
        /// resetting the level to 0 would collapse the whole hierarchy into a single layer and
        /// silently degrade every subsequent search into a near-linear scan.
        /// </summary>
        private void FindNewEntryPoint()
        {
            int best = -1;
            int bestLevel = -1;

            for (int i = 0; i < _header.CurrentCount; i++)
            {
                if (_deletedIndices.Contains(i)) continue;

                int level = GetNodeLevel(i);
                if (level > bestLevel)
                {
                    best = i;
                    bestLevel = level;
                    if (bestLevel == _header.MaxLayers - 1) break;
                }
            }

            // -1 signals "no live node"; ResolveEntryPoint turns that into an empty result
            // instead of seeding the search with a tombstone at index 0.
            _header.EntryPoint = best;
            _header.EntryPointLevel = best >= 0 ? bestLevel : 0;
        }

        private bool IsDeleted(int index) => _deletedIndices.Contains(index);

        private void WriteTombstone(int index, byte value)
        {
            BeginWrite();
            long offset = (_tombstoneSectionOffset - HeaderSize) + index;
            _dataAccessor.Write(offset, value);
        }

        private byte ReadTombstone(int index)
        {
            long offset = (_tombstoneSectionOffset - HeaderSize) + index;
            return _dataAccessor.ReadByte(offset);
        }

        private void LoadTombstones()
        {
            _deletedIndices.Clear();
            for (int i = 0; i < _header.CurrentCount; i++)
            {
                if (ReadTombstone(i) != 0)
                {
                    _deletedIndices.Add(i);
                    _guidIndex.Remove(ReadGuidFromDisk(i));
                }
            }
        }

        public bool UpdateMetadata(Guid id, string newMetadata)
        {
            ArgumentNullException.ThrowIfNull(newMetadata);

            _lock.EnterWriteLock();
            try
            {
                if (!_guidIndex.TryGetValue(id, out int index))
                    return false;

                WriteMetadataToDisk(index, newMetadata);
                CommitHeader();
                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public bool UpdateVector(Guid id, float[] newVector)
        {
            ValidateVector(newVector, nameof(newVector));

            _lock.EnterWriteLock();
            try
            {
                if (!_guidIndex.TryGetValue(id, out int oldIndex))
                    return false;

                string metadata = GetMetadata(oldIndex);

                // Capture the index terms before the row is torn down; the entry is about to
                // move to a different row and would otherwise silently drop out of every
                // indexed query.
                var terms = GetFieldTerms(oldIndex);

                _guidIndex.Remove(id);
                SoftDelete(oldIndex);
                _header.DeletedCount++;

                AddEntryInternal(newVector, metadata, id);

                if (terms.Count > 0 && _guidIndex.TryGetValue(id, out int newIndex))
                    AddFieldIndex(newIndex, terms);

                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public bool Update(Guid id, float[]? newVector, string? newMetadata)
        {
            if (newVector is not null) ValidateVector(newVector, nameof(newVector));
            if (newVector is null && newMetadata is null)
                throw new ArgumentException("At least one of newVector or newMetadata must be supplied.");

            _lock.EnterWriteLock();
            try
            {
                if (!_guidIndex.TryGetValue(id, out int oldIndex))
                    return false;

                if (newVector == null)
                {
                    WriteMetadataToDisk(oldIndex, newMetadata!);
                    CommitHeader();
                    return true;
                }

                string metadata = newMetadata ?? GetMetadata(oldIndex);
                var terms = GetFieldTerms(oldIndex);

                _guidIndex.Remove(id);
                SoftDelete(oldIndex);
                _header.DeletedCount++;

                AddEntryInternal(newVector, metadata, id);

                if (terms.Count > 0 && _guidIndex.TryGetValue(id, out int newIndex))
                    AddFieldIndex(newIndex, terms);

                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        private Guid AddEntryInternal(float[] vector, string metadata, Guid docId)
        {
            if (_guidIndex.ContainsKey(docId))
                return docId;

            int index = AllocateSlot();
            int level = RandomLayer();

            vector = PrepareVector(vector);

            WriteVectorToDisk(index, vector);
            WriteMetadataToDisk(index, metadata);
            WriteGuidToDisk(index, docId);
            InitNeighborsOnDisk(index);

            _guidIndex[docId] = index;

            if (LiveCount == 1)
            {
                _header.EntryPoint = index;
                _header.EntryPointLevel = level;
            }
            else
            {
                if (!ConnectNewNode(index, vector, level) || level > _header.EntryPointLevel)
                {
                    _header.EntryPoint = index;
                    _header.EntryPointLevel = level;
                }
            }

            CommitHeader();
            return docId;
        }

        public unsafe void Dispose()
        {
            // Publish a clean, committed header so a database that is only ever written to and
            // disposed does not look like it crashed mid-write. The data view is deliberately
            // not flushed here: the OS writes back dirty pages of a mapped file anyway, and
            // forcing the whole mapping to disk on every Dispose is far too expensive. Callers
            // that need a hard durability barrier call Flush() explicitly.
            //
            // The write lock is acquired with a timeout rather than unconditionally: a reader
            // holding the read lock may be blocked on caller-supplied code (a filter delegate),
            // and that caller may in turn be waiting for Dispose to return. Blocking here would
            // turn that into a deadlock. Dispose during an in-flight read is already a caller
            // error, so on timeout we publish the header anyway -- the header region is never
            // touched by readers, so the write is safe even without the lock.
            try
            {
                bool locked = _lock.TryEnterWriteLock(TimeSpan.FromSeconds(1));
                try { CommitHeader(); }
                finally { if (locked) _lock.ExitWriteLock(); }
            }
            catch (ObjectDisposedException) { /* already torn down */ }

            if (_dataBasePtr != null)
            {
                _dataAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _dataBasePtr = null;
            }

            if (_headerBasePtr != null)
            {
                _headerAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _headerBasePtr = null;
            }

            _headerAccessor.Dispose();
            _dataAccessor.Dispose();
            _mmf.Dispose();
            _lock.Dispose();
        }
    }
}