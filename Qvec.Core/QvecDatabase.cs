using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Qvec.Core.Format;
using Qvec.Core.Quantization;
using Qvec.Core.Sync;

namespace Qvec.Core
{
    public enum DistanceFunction : int
    {
        DotProduct = 0,
        Cosine = 1,

        /// <summary>
        /// Straight-line distance. Unlike the other two this ranks by position rather than
        /// direction, which is what the standard ANN corpora and most image and audio
        /// embeddings mean by a nearest neighbour. Vectors are stored unnormalised.
        /// </summary>
        Euclidean = 2
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
        /// <summary>Offset of the float vector section (1). Set in float mode and in rescored int8 mode.</summary>
        private long _floatVectorSectionOffset;
        /// <summary>Offset of section 8 (int8 codes); only meaningful when <see cref="_quantized"/>.</summary>
        private long _codesSectionOffset;
        /// <summary>Offset of section 10 (per-vector int8 parameters); only meaningful when <see cref="_quantized"/>.</summary>
        private long _quantParamsSectionOffset;
        /// <summary>True when the graph is walked on byte codes in section 8 instead of floats in section 1.</summary>
        private bool _quantized;
        /// <summary>
        /// True when the file is int8 but also keeps the original floats, so search candidates are
        /// re-ranked exactly and <see cref="GetByGuid"/> returns the original vector.
        /// </summary>
        private bool _rescore;
        private long _graphSectionOffset;
        private long _metadataSectionOffset;
        private long _metadataHeapOffset;
        private long _guidSectionOffset;
        private long _tombstoneSectionOffset;
        private long _freeListSectionOffset;
        /// <summary>True when the file carries per-row versions (format version 6). The one branch every mutation takes.</summary>
        private bool _tracking;
        /// <summary>Offset of section 11 (24 B per row: Hlc + Origin); only meaningful when <see cref="_tracking"/>.</summary>
        private long _entryVersionsSectionOffset;
        /// <summary>Offset of section 12 (change-log ring); only meaningful when <see cref="_tracking"/>.</summary>
        private long _changeLogSectionOffset;
        /// <summary>
        /// Tombstone versions for documents that are not live, rebuilt from the change-log ring on
        /// open (deleted rows are recycled, so the version cannot live on the row). A document
        /// whose tombstone has rotated out of the ring is simply forgotten; see design 4.4.
        /// </summary>
        private readonly Dictionary<Guid, EntryVersion> _deletedVersions = new();
        private readonly Dictionary<Guid, int> _guidIndex = new();
        private TombstoneSet _deletedIndices;
        private readonly int[] _cachedEmptyNeighbors;

        // Null means "use Random.Shared", i.e. the randomized default. A non-null instance is
        // not thread-safe, but every RandomLayer() call happens under the write lock.
        private readonly Random? _layerRng;

        // Inverterat index: field -> value -> set of entry indices
        private readonly Dictionary<string, Dictionary<string, HashSet<int>>> _fieldIndex = new();


        
        /// <param name="indexSeed">
        /// Optional seed for the HNSW layer assignment. When omitted the layer draw is
        /// randomized, which is the right default: a fixed layer pattern in every deployment
        /// would make the index structure predictable from the insert order alone. Supplying a
        /// seed makes index construction reproducible, which is what benchmarks and recall
        /// regression tests need in order to be re-derivable.
        /// </param>
        /// <param name="quantization">
        /// How vectors are stored. <see cref="VectorQuantization.Int8"/> cuts vector storage to
        /// roughly a quarter at the cost of approximate scores. Recorded in the file header, so
        /// a reopen must pass the same value (or use <see cref="Open(string)"/>).
        /// </param>
        /// <param name="changeTracking">
        /// Enables per-document version tracking for replication when creating a new file; see
        /// <see cref="ChangeTrackingOptions"/>. Off by default and free when off. Reopening a
        /// tracked file without this argument keeps tracking on; reopening an untracked file
        /// with it throws -- use <see cref="EnableChangeTracking"/> to convert an existing file.
        /// </param>
        public QvecDatabase(string path, int dim = 1536, int max = 1000, int maxNeighbors = 32, int maxLayers = 5, DistanceFunction distanceFunction = DistanceFunction.DotProduct, int? indexSeed = null, VectorQuantization quantization = VectorQuantization.None, ChangeTrackingOptions? changeTracking = null)
            : this(path, dim, max, maxNeighbors, maxLayers, distanceFunction, honourHeaderSilently: false, indexSeed: indexSeed, quantization: quantization, changeTracking: changeTracking)
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
                             DistanceFunction distanceFunction, bool honourHeaderSilently,
                             int? indexSeed = null, VectorQuantization quantization = VectorQuantization.None,
                             ChangeTrackingOptions? changeTracking = null)
        {
            _layerRng = indexSeed is int seed ? new Random(seed) : null;
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            changeTracking?.Validate();
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
                    EnsureHeaderMatchesRequest(path, _header, dim, max, maxNeighbors, maxLayers, distanceFunction, quantization, changeTracking);
                }
            }
            else
            {
                ValidateCreationParameters(dim, max, maxNeighbors, maxLayers);

                _header = QvecFormatLayout.CreateInitial(
                    dim, max, maxNeighbors, maxLayers,
                    QvecFormatLayout.RecommendMetadataHeapCapacity(max),
                    distanceFunction,
                    quantization,
                    trackChanges: changeTracking is not null,
                    changeLogCapacity: changeTracking?.LogCapacity ?? 0);

                if (changeTracking?.ReplicaId is { } replicaId)
                {
                    _header.ReplicaId = replicaId;
                }
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

            int totalSlots = GraphNodeStride;
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
            LoadDeletedVersions();
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
            int dim, int max, int maxNeighbors, int maxLayers, DistanceFunction distanceFunction,
            VectorQuantization quantization, ChangeTrackingOptions? changeTracking)
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
            if (QvecFormatLayout.EffectiveQuantization(header) != quantization)
                mismatches.Add($"quantization: file has {QvecFormatLayout.EffectiveQuantization(header)}, caller requested {quantization}");

            // Tracking is a property of the file rather than of the request: a tracked file stays
            // tracked whether or not the caller mentions it. The reverse is not silently honoured,
            // because turning tracking on rewrites the file and deserves an explicit call.
            if (changeTracking is not null)
            {
                if (!header.HasChangeTracking)
                {
                    throw new QvecFormatException(
                        $"'{path}' was created without change tracking. Call EnableChangeTracking on the open database to convert it; " +
                        "the constructor only enables tracking when it creates the file.");
                }

                if (changeTracking.ReplicaId is { } replicaId && replicaId != header.ReplicaId)
                    mismatches.Add($"changeTracking.ReplicaId: file has {header.ReplicaId}, caller requested {replicaId}");
                if (changeTracking.LogCapacity is { } capacity && capacity != header.ChangeLogCapacity)
                    mismatches.Add($"changeTracking.LogCapacity: file has {header.ChangeLogCapacity}, caller requested {capacity}");
            }

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

        /// <summary>The vector dimension this database was created with.</summary>
        /// <summary>Absolute path of the database file.</summary>
        public string FilePath => _path;

        public int VectorDimension => _header.VectorDimension;

        /// <summary>The distance function this database was created with, read from its header.</summary>
        public DistanceFunction DistanceFunction => _header.DistanceFunction;

        /// <summary>
        /// How vectors are stored on disk, derived from the header and its section table.
        /// <see cref="VectorQuantization.Int8Rescored"/> is reported when an int8 file also
        /// carries the optional float section.
        /// </summary>
        public VectorQuantization Quantization => QvecFormatLayout.EffectiveQuantization(_header);

        // --- CHANGE TRACKING ---

        /// <summary>
        /// Whether the file records a <see cref="EntryVersion"/> per document, the prerequisite for
        /// replication. See <see cref="ChangeTrackingOptions"/>.
        /// </summary>
        public bool IsChangeTrackingEnabled => _tracking;

        /// <summary>This replica's identity, or <see cref="Guid.Empty"/> when tracking is off.</summary>
        public Guid ReplicaId => _header.ReplicaId;

        /// <summary>The most recent hybrid logical clock value this replica has issued; 0 when tracking is off.</summary>
        public long LastHlc => _header.LastHlc;

        /// <summary>Records the change-log ring can hold; 0 when tracking is off.</summary>
        public long ChangeLogCapacity => _header.ChangeLogCapacity;

        /// <summary>
        /// The version of a live document. False when tracking is off or the document does not exist
        /// (or has been deleted -- tombstone versions live in the change log, not on the row).
        /// </summary>
        public bool TryGetVersion(Guid id, out EntryVersion version)
        {
            _lock.EnterReadLock();
            try
            {
                if (_tracking && _guidIndex.TryGetValue(id, out int index))
                {
                    version = ReadVersionFromDisk(index);
                    return true;
                }

                version = default;
                return false;
            }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>
        /// Turns on change tracking for an existing database. The file is re-laid out with the two
        /// tracking sections appended (existing sections do not move), every live row is stamped
        /// with a fresh version from this replica, and the file becomes format version 6 -- from
        /// this point on it is no longer readable by Qvec 2.0.0. The first peer to pull from this
        /// replica will see every document as a change; that is intentional.
        /// </summary>
        /// <exception cref="InvalidOperationException">Tracking is already enabled.</exception>
        public void EnableChangeTracking(ChangeTrackingOptions? options = null)
        {
            options ??= new ChangeTrackingOptions();
            options.Validate();

            _lock.EnterWriteLock();
            try
            {
                if (_tracking)
                    throw new InvalidOperationException("Change tracking is already enabled on this database.");

                var tracked = QvecFormatLayout.CreateTracked(_header, options.LogCapacity ?? 0);
                if (options.ReplicaId is { } replicaId) tracked.ReplicaId = replicaId;

                ApplyRelayout(tracked);

                for (int index = 0; index < _header.CurrentCount; index++)
                {
                    if (_deletedIndices.Contains(index)) continue;
                    RecordLocalUpsert(index, ReadGuidFromDisk(index));
                }

                CommitHeader();
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>Sequence number of the newest change-log record; 0 when tracking is off or nothing has been written.</summary>
        public long ChangeSeq => _header.ChangeSeq;

        /// <summary>Records currently held in the ring.</summary>
        public long ChangeLogCount => _header.ChangeLogCount;

        /// <summary>
        /// Sequence number of the oldest record still in the ring. A cursor older than
        /// <c>OldestChangeSeq - 1</c> can no longer be served by <see cref="GetChanges"/>.
        /// </summary>
        public long OldestChangeSeq => _header.ChangeSeq - _header.ChangeLogCount + 1;

        /// <summary>
        /// Extracts index terms from metadata. When set, <see cref="ApplyChanges"/> keeps the
        /// inverted index current for rows it writes; <see cref="RebuildFieldIndex"/> sets it.
        /// </summary>
        public Func<string, IEnumerable<(string Field, string Value)>>? FieldIndexExtractor { get; set; }

        /// <summary>
        /// Reads the change log after <paramref name="sinceSeq"/> and returns each touched
        /// document once, with its current state: a live row becomes an <see cref="ChangeType.Upsert"/>
        /// carrying vector and metadata, anything else a <see cref="ChangeType.Delete"/>.
        /// Intermediate versions are not reconstructed -- that is last-writer-wins by design.
        /// </summary>
        /// <param name="sinceSeq">Cursor: the last sequence number the caller has already seen; 0 for everything.</param>
        /// <param name="maxItems">Upper bound on distinct documents in the batch.</param>
        /// <param name="excludeOrigin">
        /// Drop documents whose current version was written by this replica. Use the pulling
        /// peer's id so it is not sent its own changes back.
        /// </param>
        /// <exception cref="SyncCursorTooOldException">The ring has rotated past <paramref name="sinceSeq"/>.</exception>
        public ChangeBatch GetChanges(long sinceSeq, int maxItems, Guid? excludeOrigin = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sinceSeq);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);

            _lock.EnterReadLock();
            try
            {
                if (!_tracking)
                    throw new InvalidOperationException("Change tracking is not enabled on this database; call EnableChangeTracking first.");
                if (sinceSeq > _header.ChangeSeq)
                    throw new ArgumentOutOfRangeException(nameof(sinceSeq), sinceSeq, $"Cursor is ahead of the log head ({_header.ChangeSeq}).");

                long oldest = OldestChangeSeq;
                if (sinceSeq < oldest - 1)
                    throw new SyncCursorTooOldException(sinceSeq, oldest);

                var latest = new Dictionary<Guid, ChangeRecord>();
                var order = new List<Guid>();
                long toSeq = sinceSeq;
                for (long seq = sinceSeq + 1; seq <= _header.ChangeSeq; seq++)
                {
                    var record = ReadChangeRecord(SlotOfSeq(seq));
                    if (!latest.ContainsKey(record.DocumentId))
                    {
                        if (latest.Count == maxItems) break;
                        order.Add(record.DocumentId);
                    }
                    latest[record.DocumentId] = record;
                    toSeq = seq;
                }

                bool int8Payload = _quantized && !_rescore;
                var items = new List<ChangeItem>(order.Count);
                foreach (var id in order)
                {
                    if (_guidIndex.TryGetValue(id, out int index))
                    {
                        var version = ReadVersionFromDisk(index);
                        if (version.Origin == excludeOrigin) continue;
                        items.Add(int8Payload ? BuildInt8Upsert(id, index, version) : BuildFloatUpsert(id, index, version));
                    }
                    else
                    {
                        var record = latest[id];
                        var version = _deletedVersions.TryGetValue(id, out var tombstone)
                            ? tombstone
                            : new EntryVersion(record.Hlc, record.Origin);
                        if (version.Origin == excludeOrigin) continue;
                        items.Add(new ChangeItem { Type = ChangeType.Delete, DocumentId = id, Version = version });
                    }
                }

                return new ChangeBatch
                {
                    SourceReplicaId = _header.ReplicaId,
                    Dimension = _header.VectorDimension,
                    DistanceFunction = _header.DistanceFunction,
                    Payload = int8Payload ? ChangePayloadKind.Int8 : ChangePayloadKind.Float,
                    FromSeq = sinceSeq + 1,
                    ToSeq = toSeq,
                    Items = items,
                    HasMore = toSeq < _header.ChangeSeq
                };
            }
            finally { _lock.ExitReadLock(); }
        }

        private ChangeItem BuildFloatUpsert(Guid id, int index, EntryVersion version)
        {
            var vector = new float[_header.VectorDimension];
            ReadVectorInto(index, vector, 0);
            return new ChangeItem { Type = ChangeType.Upsert, DocumentId = id, Version = version, Vector = vector, Metadata = GetMetadata(index) };
        }

        private unsafe ChangeItem BuildInt8Upsert(Guid id, int index, EntryVersion version)
        {
            var codes = new ReadOnlySpan<byte>(CodesPointer(index), _header.VectorDimension).ToArray();
            return new ChangeItem
            {
                Type = ChangeType.Upsert, DocumentId = id, Version = version,
                Codes = codes, Parameters = ParamsAt(index), Metadata = GetMetadata(index)
            };
        }

        /// <summary>
        /// Merges a peer's changes with last-writer-wins on <see cref="EntryVersion"/>: an item is
        /// applied only if its version is newer than what this replica holds for the document
        /// (live row or tombstone). Applied items keep the remote version and are appended to this
        /// replica's own log so that its peers see them too. Idempotent.
        /// </summary>
        /// <exception cref="ArgumentException">The batch's dimension does not match this database.</exception>
        public ApplyResult ApplyChanges(ChangeBatch batch)
        {
            ArgumentNullException.ThrowIfNull(batch);

            _lock.EnterWriteLock();
            try
            {
                if (!_tracking)
                    throw new InvalidOperationException("Change tracking is not enabled on this database; call EnableChangeTracking first.");
                if (batch.Dimension != _header.VectorDimension)
                    throw new ArgumentException($"Batch dimension {batch.Dimension} does not match the database dimension {_header.VectorDimension}.", nameof(batch));

                bool localInt8 = _quantized && !_rescore;
                var results = new List<ApplyItemResult>(batch.Items.Count);
                int applied = 0, skipped = 0, rejected = 0;

                for (int i = 0; i < batch.Items.Count; i++)
                {
                    var item = batch.Items[i];
                    var outcome = ApplyOne(item, batch.Payload, localInt8, out string? reason);
                    switch (outcome)
                    {
                        case ApplyOutcome.Applied: applied++; break;
                        case ApplyOutcome.Skipped: skipped++; break;
                        default: rejected++; break;
                    }
                    results.Add(new ApplyItemResult(i, item.DocumentId, outcome, reason));
                }

                return new ApplyResult { Applied = applied, Skipped = skipped, Rejected = rejected, Items = results };
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Writes a bit-exact, committed copy of this database to <paramref name="destination"/>.
        /// The copy keeps this replica's <see cref="ReplicaId"/> and its whole change log, so a new
        /// peer can bootstrap by opening it after <see cref="AdoptAsReplica"/> and then pull only the
        /// delta after <see cref="SnapshotInfo.ChangeSeq"/>. Writers are blocked for the duration of
        /// the copy; readers are not. Nothing is re-indexed.
        /// </summary>
        /// <exception cref="InvalidOperationException">Change tracking is not enabled.</exception>
        public unsafe SnapshotInfo ExportSnapshot(Stream destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            _lock.EnterWriteLock();
            try
            {
                if (!_tracking)
                    throw new InvalidOperationException("Change tracking is not enabled on this database; a snapshot without versions cannot be synchronised.");

                // Publish a clean header first so the copy is openable, then copy straight from the
                // mapping: the file itself is held with FileShare.None while open.
                CommitHeader();

                destination.Write(HeaderSpan);
                long remaining = _header.FileLength - HeaderSize;
                byte* cursor = DataBasePointer;
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(remaining, 1 << 20);
                    destination.Write(new ReadOnlySpan<byte>(cursor, chunk));
                    cursor += chunk;
                    remaining -= chunk;
                }
                destination.Flush();

                return new SnapshotInfo(_header.ReplicaId, _header.ChangeSeq, _header.FileLength);
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Turns a snapshot file (see <see cref="ExportSnapshot"/>) into an independent replica by
        /// giving it a new <see cref="ReplicaId"/>. Versions, the change log and every row are
        /// untouched, so the file stays byte-compatible with what its peers already know about it.
        /// The identity it had is returned along with the newest sequence it contains; a sync agent
        /// records that pair as its cursor for the source peer. The file must not be open.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="newReplicaId"/> is empty or equals the file's current identity.</exception>
        /// <exception cref="QvecFormatException">The file is not a tracked Qvec database or was not closed cleanly.</exception>
        public static void AdoptAsReplica(string path, Guid newReplicaId, out Guid sourceReplicaId, out long sourceSeq)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            if (newReplicaId == Guid.Empty)
                throw new ArgumentException("A replica id must not be empty.", nameof(newReplicaId));

            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var image = new byte[HeaderSize];
            fs.ReadExactly(image);

            V4Header header = V4Header.Read(image, fs.Length);
            header.EnsureCleanOpen(path);
            if (!header.HasChangeTracking)
                throw new QvecFormatException($"'{path}' was created without change tracking and cannot be adopted as a replica.");
            if (header.ReplicaId == newReplicaId)
                throw new ArgumentException("The file already has this replica id; two replicas sharing an identity would treat each other's writes as their own.", nameof(newReplicaId));

            sourceReplicaId = header.ReplicaId;
            sourceSeq = header.ChangeSeq;

            header.ReplicaId = newReplicaId;
            header.Generation++;
            header.UpdatedUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            header.WriteTo(image);

            fs.Position = 0;
            fs.Write(image);
            fs.Flush(flushToDisk: true);
        }

        private ApplyOutcome ApplyOne(ChangeItem item, ChangePayloadKind payload, bool localInt8, out string? reason)
        {
            reason = null;
            int dim = _header.VectorDimension;

            bool live = _guidIndex.TryGetValue(item.DocumentId, out int oldIndex);
            EntryVersion? local = live ? ReadVersionFromDisk(oldIndex)
                : _deletedVersions.TryGetValue(item.DocumentId, out var tombstone) ? tombstone
                : null;
            if (local is { } current && item.Version <= current)
                return ApplyOutcome.Skipped;

            if (item.Type == ChangeType.Delete)
            {
                if (live)
                {
                    RemoveLiveRow(item.DocumentId, oldIndex);
                }
                RecordRemoteDelete(item.DocumentId, item.Version);
                CommitHeader();
                return ApplyOutcome.Applied;
            }

            if (item.Type != ChangeType.Upsert)
            {
                reason = $"Unknown change type {(byte)item.Type}.";
                return ApplyOutcome.Rejected;
            }
            if (item.Metadata is null)
            {
                reason = "Upsert without metadata.";
                return ApplyOutcome.Rejected;
            }

            float[] floats;
            if (payload == ChangePayloadKind.Int8)
            {
                if (!localInt8)
                {
                    reason = "Int8 payload cannot be applied to a database that stores float vectors; the source has no floats to send.";
                    return ApplyOutcome.Rejected;
                }
                if (item.Codes is null || item.Parameters is null)
                {
                    reason = "Int8 upsert without codes or parameters.";
                    return ApplyOutcome.Rejected;
                }
                if (item.Codes.Length != dim)
                {
                    reason = $"Vector dimension {item.Codes.Length} does not match the database dimension {dim}.";
                    return ApplyOutcome.Rejected;
                }
                floats = new float[dim];
                Int8Quantizer.Dequantize(item.Codes, item.Parameters.Value, floats);
            }
            else
            {
                if (item.Vector is null)
                {
                    reason = "Upsert without vector.";
                    return ApplyOutcome.Rejected;
                }
                if (item.Vector.Length != dim)
                {
                    reason = $"Vector dimension {item.Vector.Length} does not match the database dimension {dim}.";
                    return ApplyOutcome.Rejected;
                }
                floats = item.Vector;
            }

            List<(string Field, string Value)>? inheritedTerms = null;
            if (live)
            {
                if (FieldIndexExtractor is null) inheritedTerms = GetFieldTerms(oldIndex);
                RemoveLiveRow(item.DocumentId, oldIndex);
            }

            AddEntryInternal(floats, item.Metadata, item.DocumentId, item.Version);
            int newIndex = _guidIndex[item.DocumentId];

            if (payload == ChangePayloadKind.Int8)
            {
                // Store the peer's exact codes so replicas stay bit-identical instead of
                // re-quantising a dequantised approximation.
                WriteCodesToDisk(newIndex, item.Codes!, item.Parameters!.Value);
            }

            if (FieldIndexExtractor is { } extractor) AddFieldIndex(newIndex, extractor(item.Metadata));
            else if (inheritedTerms is { Count: > 0 }) AddFieldIndex(newIndex, inheritedTerms);

            return ApplyOutcome.Applied;
        }

        /// <summary>Soft-deletes a live row and forgets its GUID. Callers append the log record and commit.</summary>
        private void RemoveLiveRow(Guid id, int index)
        {
            _guidIndex.Remove(id);
            SoftDelete(index);
            _header.DeletedCount++;
        }

        /// <summary>Issues the next local HLC value and records it in the header. Callers hold the write lock.</summary>
        private long TickLocalHlc()
        {
            long next = HybridLogicalClock.Next(_header.LastHlc);
            _header.LastHlc = next;
            return next;
        }

        /// <summary>
        /// Stamps a row with a new local version and logs the upsert. Every local mutation path
        /// calls this after writing the row and before <see cref="CommitHeader"/>; the check is
        /// the only tracking cost an untracked database ever pays.
        /// </summary>
        private void RecordLocalUpsert(int index, Guid docId)
        {
            if (!_tracking) return;
            var version = new EntryVersion(TickLocalHlc(), _header.ReplicaId);
            WriteVersionToDisk(index, version.Hlc, version.Origin);
            AppendChangeRecord(ChangeType.Upsert, docId, version);
            _deletedVersions.Remove(docId);
        }

        private void RecordLocalDelete(Guid docId)
        {
            if (!_tracking) return;
            var version = new EntryVersion(TickLocalHlc(), _header.ReplicaId);
            AppendChangeRecord(ChangeType.Delete, docId, version);
            _deletedVersions[docId] = version;
        }

        /// <summary>Stamps a row with a version received from a peer and advances the local clock past it.</summary>
        private void RecordRemoteUpsert(int index, Guid docId, EntryVersion version)
        {
            _header.LastHlc = HybridLogicalClock.Receive(_header.LastHlc, version.Hlc);
            WriteVersionToDisk(index, version.Hlc, version.Origin);
            AppendChangeRecord(ChangeType.Upsert, docId, version);
            _deletedVersions.Remove(docId);
        }

        private void RecordRemoteDelete(Guid docId, EntryVersion version)
        {
            _header.LastHlc = HybridLogicalClock.Receive(_header.LastHlc, version.Hlc);
            AppendChangeRecord(ChangeType.Delete, docId, version);
            _deletedVersions[docId] = version;
        }

        private unsafe void WriteVersionToDisk(int index, long hlc, Guid origin)
        {
            BeginWrite();
            byte* row = DataBasePointer + (_entryVersionsSectionOffset - HeaderSize) + (long)index * V4Header.EntryVersionSize;
            BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(row, sizeof(long)), hlc);
            origin.TryWriteBytes(new Span<byte>(row + sizeof(long), GuidSize));
        }

        private unsafe EntryVersion ReadVersionFromDisk(int index)
        {
            byte* row = DataBasePointer + (_entryVersionsSectionOffset - HeaderSize) + (long)index * V4Header.EntryVersionSize;
            long hlc = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(row, sizeof(long)));
            return new EntryVersion(hlc, new Guid(new ReadOnlySpan<byte>(row + sizeof(long), GuidSize)));
        }

        // --- CHANGE LOG RING ---

        /// <summary>One 64-byte slot in section 12. Layout per design 4.2; bytes 49..63 are reserved zero.</summary>
        private readonly record struct ChangeRecord(long Seq, long Hlc, Guid DocumentId, Guid Origin, ChangeType Type);

        private unsafe byte* ChangeRecordPointer(long slot)
            => DataBasePointer + (_changeLogSectionOffset - HeaderSize) + slot * V4Header.ChangeRecordSize;

        /// <summary>Ring slot holding sequence number <paramref name="seq"/>; only valid for seq within [OldestChangeSeq, ChangeSeq].</summary>
        private long SlotOfSeq(long seq)
        {
            long capacity = _header.ChangeLogCapacity;
            long newestSlot = (_header.ChangeLogHead - 1 + capacity) % capacity;
            return ((newestSlot - (_header.ChangeSeq - seq)) % capacity + capacity) % capacity;
        }

        /// <summary>
        /// Writes the record into the next ring slot and advances the header counters. The header
        /// is not committed here: a record beyond the committed <c>ChangeLogCount</c> after a crash
        /// is ignored on open, the same way a row beyond <c>CurrentCount</c> is.
        /// </summary>
        private void AppendChangeRecord(ChangeType type, Guid docId, EntryVersion version)
        {
            BeginWrite();
            long capacity = _header.ChangeLogCapacity;
            long seq = _header.ChangeSeq + 1;
            WriteChangeRecord(_header.ChangeLogHead, new ChangeRecord(seq, version.Hlc, docId, version.Origin, type));

            _header.ChangeSeq = seq;
            _header.ChangeLogHead = (_header.ChangeLogHead + 1) % capacity;
            _header.ChangeLogCount = Math.Min(_header.ChangeLogCount + 1, capacity);
        }

        private unsafe void WriteChangeRecord(long slot, ChangeRecord record)
        {
            var span = new Span<byte>(ChangeRecordPointer(slot), (int)V4Header.ChangeRecordSize);
            span.Clear();
            BinaryPrimitives.WriteInt64LittleEndian(span, record.Seq);
            BinaryPrimitives.WriteInt64LittleEndian(span[8..], record.Hlc);
            record.DocumentId.TryWriteBytes(span[16..32]);
            record.Origin.TryWriteBytes(span[32..48]);
            span[48] = (byte)record.Type;
        }

        private unsafe ChangeRecord ReadChangeRecord(long slot)
        {
            var span = new ReadOnlySpan<byte>(ChangeRecordPointer(slot), (int)V4Header.ChangeRecordSize);
            return new ChangeRecord(
                BinaryPrimitives.ReadInt64LittleEndian(span),
                BinaryPrimitives.ReadInt64LittleEndian(span[8..]),
                new Guid(span[16..32]),
                new Guid(span[32..48]),
                (ChangeType)span[48]);
        }

        /// <summary>All records currently in the ring, oldest first.</summary>
        private List<ChangeRecord> ReadChangeLog()
        {
            var records = new List<ChangeRecord>((int)Math.Min(_header.ChangeLogCount, int.MaxValue));
            for (long seq = OldestChangeSeq; seq <= _header.ChangeSeq; seq++)
                records.Add(ReadChangeRecord(SlotOfSeq(seq)));
            return records;
        }

        /// <summary>
        /// Writes <paramref name="records"/> into slots 0..n-1 and points the head after them.
        /// Used after a relayout changed the ring capacity, which invalidates every slot index.
        /// </summary>
        private void WritePackedChangeLog(List<ChangeRecord> records)
        {
            long capacity = _header.ChangeLogCapacity;
            int skip = (int)Math.Max(0, records.Count - capacity);
            for (int i = skip; i < records.Count; i++)
                WriteChangeRecord(i - skip, records[i]);

            _header.ChangeLogCount = records.Count - skip;
            _header.ChangeLogHead = _header.ChangeLogCount % capacity;
        }

        /// <summary>Rebuilds <see cref="_deletedVersions"/> from the ring. Requires <see cref="_guidIndex"/> and tombstones to be loaded.</summary>
        private void LoadDeletedVersions()
        {
            _deletedVersions.Clear();
            if (!_tracking) return;

            foreach (var record in ReadChangeLog())
            {
                if (record.Type == ChangeType.Delete)
                    _deletedVersions[record.DocumentId] = new EntryVersion(record.Hlc, record.Origin);
                else
                    _deletedVersions.Remove(record.DocumentId);
            }

            // A document that is live cannot also carry a tombstone; the row's version rules.
            foreach (var id in _deletedVersions.Keys.Where(_guidIndex.ContainsKey).ToList())
                _deletedVersions.Remove(id);
        }

        /// <summary>Maximum number of entries the database can hold.</summary>
        public int MaxCount => _header.MaxCount;

        /// <summary>Number of soft-deleted entries.</summary>
        public int DeletedCount => _header.DeletedCount;

        /// <summary>Number of live (non-deleted) entries.</summary>
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
        /// True when there is no live entry to search among. Used to
        /// avoid seeding the HNSW navigation result with a nonexistent node 0.
        /// </summary>
        private bool IsEffectivelyEmpty => _header.CurrentCount == 0 || LiveCount <= 0;

        /// <summary>
        /// Returns a valid start node for HNSW navigation, or -1 if none exists.
        /// Protects against EntryPoint pointing to a deleted node.
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

        // --- CORE: SIMD MATH ---
        //
        // One implementation per metric, on managed refs, and every public/internal overload
        // forwards to it. Four independent vector accumulators (16 floats per iteration on
        // 4-lane NEON, 32 on AVX2) keep the FMA pipeline busy instead of serialising on a
        // single accumulator chain, and the horizontal reduction happens once at the end.
        // Accumulating in vectors rather than `Vector.Dot` per iteration changes the order in
        // which partial sums are added, so results can differ from a scalar loop in the last
        // bits; the same kernel serves build and search, so the graph and the queries agree.

        public static float DotProduct(float[] left, float[] right)
        {
            return DotProduct(left, right, left.Length);
        }

        internal static float DotProduct(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            return DotProductCore(
                ref MemoryMarshal.GetReference(left),
                ref MemoryMarshal.GetReference(right),
                Math.Min(left.Length, right.Length));
        }

        public static float DotProduct(float[] left, float[] right, int count)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)count, (uint)Math.Min(left.Length, right.Length));
            return DotProductCore(
                ref MemoryMarshal.GetArrayDataReference(left),
                ref MemoryMarshal.GetArrayDataReference(right),
                count);
        }

        private static float DotProductCore(ref float left, ref float right, int count)
        {
            int i = 0;
            int lanes = Vector<float>.Count;

            var acc0 = Vector<float>.Zero;
            var acc1 = Vector<float>.Zero;
            var acc2 = Vector<float>.Zero;
            var acc3 = Vector<float>.Zero;

            for (; i <= count - 4 * lanes; i += 4 * lanes)
            {
                acc0 = Vector.FusedMultiplyAdd(Vector.LoadUnsafe(ref left, (nuint)i), Vector.LoadUnsafe(ref right, (nuint)i), acc0);
                acc1 = Vector.FusedMultiplyAdd(Vector.LoadUnsafe(ref left, (nuint)(i + lanes)), Vector.LoadUnsafe(ref right, (nuint)(i + lanes)), acc1);
                acc2 = Vector.FusedMultiplyAdd(Vector.LoadUnsafe(ref left, (nuint)(i + 2 * lanes)), Vector.LoadUnsafe(ref right, (nuint)(i + 2 * lanes)), acc2);
                acc3 = Vector.FusedMultiplyAdd(Vector.LoadUnsafe(ref left, (nuint)(i + 3 * lanes)), Vector.LoadUnsafe(ref right, (nuint)(i + 3 * lanes)), acc3);
            }

            for (; i <= count - lanes; i += lanes)
            {
                acc0 = Vector.FusedMultiplyAdd(Vector.LoadUnsafe(ref left, (nuint)i), Vector.LoadUnsafe(ref right, (nuint)i), acc0);
            }

            float dot = Vector.Sum((acc0 + acc1) + (acc2 + acc3));
            for (; i < count; i++)
                dot += Unsafe.Add(ref left, i) * Unsafe.Add(ref right, i);
            return dot;
        }

        /// <summary>
        /// Negated squared Euclidean distance, which is the score used by
        /// <see cref="DistanceFunction.Euclidean"/>.
        ///
        /// Negated because every heap, sort and pruning comparison in the HNSW code treats a
        /// higher score as better; flipping the sign here means none of that code has to know
        /// which metric is in use. Squared because the square root is a monotone function of
        /// the sum, so it cannot change any ordering — it would only cost time and precision.
        /// </summary>
        public static float NegativeSquaredDistance(float[] left, float[] right, int count)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)count, (uint)Math.Min(left.Length, right.Length));
            return NegativeSquaredDistanceCore(
                ref MemoryMarshal.GetArrayDataReference(left),
                ref MemoryMarshal.GetArrayDataReference(right),
                count);
        }

        internal static float NegativeSquaredDistance(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            return NegativeSquaredDistanceCore(
                ref MemoryMarshal.GetReference(left),
                ref MemoryMarshal.GetReference(right),
                Math.Min(left.Length, right.Length));
        }

        private static float NegativeSquaredDistanceCore(ref float left, ref float right, int count)
        {
            int i = 0;
            int lanes = Vector<float>.Count;

            var acc0 = Vector<float>.Zero;
            var acc1 = Vector<float>.Zero;
            var acc2 = Vector<float>.Zero;
            var acc3 = Vector<float>.Zero;

            for (; i <= count - 4 * lanes; i += 4 * lanes)
            {
                var d0 = Vector.LoadUnsafe(ref left, (nuint)i) - Vector.LoadUnsafe(ref right, (nuint)i);
                var d1 = Vector.LoadUnsafe(ref left, (nuint)(i + lanes)) - Vector.LoadUnsafe(ref right, (nuint)(i + lanes));
                var d2 = Vector.LoadUnsafe(ref left, (nuint)(i + 2 * lanes)) - Vector.LoadUnsafe(ref right, (nuint)(i + 2 * lanes));
                var d3 = Vector.LoadUnsafe(ref left, (nuint)(i + 3 * lanes)) - Vector.LoadUnsafe(ref right, (nuint)(i + 3 * lanes));
                acc0 = Vector.FusedMultiplyAdd(d0, d0, acc0);
                acc1 = Vector.FusedMultiplyAdd(d1, d1, acc1);
                acc2 = Vector.FusedMultiplyAdd(d2, d2, acc2);
                acc3 = Vector.FusedMultiplyAdd(d3, d3, acc3);
            }

            for (; i <= count - lanes; i += lanes)
            {
                var d = Vector.LoadUnsafe(ref left, (nuint)i) - Vector.LoadUnsafe(ref right, (nuint)i);
                acc0 = Vector.FusedMultiplyAdd(d, d, acc0);
            }

            float sum = Vector.Sum((acc0 + acc1) + (acc2 + acc3));
            for (; i < count; i++)
            {
                float d = Unsafe.Add(ref left, i) - Unsafe.Add(ref right, i);
                sum += d * d;
            }

            return -sum;
        }

        /// <summary>
        /// The single place that decides what "closer" means. Every scoring call site goes
        /// through here or through its pointer sibling, so adding a metric is a change
        /// in one switch rather than in a dozen scattered loops.
        /// Higher is always better, whichever metric is configured.
        /// </summary>
        private float Similarity(ReadOnlySpan<float> query, ReadOnlySpan<float> stored) =>
            _header.DistanceFunction == DistanceFunction.Euclidean
                ? NegativeSquaredDistance(query, stored)
                : DotProduct(query, stored);

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
        /// Normalizes a vector to unit length (L2 norm = 1).
        /// When vectors are normalized, dot product == cosine similarity.
        /// </summary>
        public static void NormalizeVector(float[] vector)
        {
            float norm = MathF.Sqrt(SquaredNorm(vector));
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

        /// <summary>
        /// Squared L2 norm with the summation order the 2.0 kernels used (one horizontal
        /// reduction per vector step, then a scalar tail). Normalised vectors are persisted, so
        /// this must keep producing bit-identical values when the scoring kernels change their
        /// accumulation order; it runs once per vector and is not on a hot path.
        /// </summary>
        private static float SquaredNorm(float[] vector)
        {
            int i = 0;
            float sum = 0;
            int lanes = Vector<float>.Count;
            ref float v = ref MemoryMarshal.GetArrayDataReference(vector);

            for (; i <= vector.Length - lanes; i += lanes)
            {
                var step = Vector.LoadUnsafe(ref v, (nuint)i);
                sum += Vector.Dot(step, step);
            }
            for (; i < vector.Length; i++) sum += vector[i] * vector[i];
            return sum;
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
                var live = new List<(int OldIndex, Guid Id, float[] Vector, string Metadata, EntryVersion Version)>();
                for (int i = 0; i < _header.CurrentCount; i++)
                {
                    if (_deletedIndices.Contains(i)) continue;
                    var exact = new float[_header.VectorDimension];
                    ReadVectorInto(i, exact, 0);
                    var version = _tracking ? ReadVersionFromDisk(i) : default;
                    live.Add((i, ReadGuidFromDisk(i), exact, GetMetadata(i), version));
                }

                string rebuiltPath = _path + ".vacuum";
                if (File.Exists(rebuiltPath)) File.Delete(rebuiltPath);

                ChangeTrackingOptions? tracking = _tracking
                    ? new ChangeTrackingOptions { ReplicaId = _header.ReplicaId, LogCapacity = _header.ChangeLogCapacity }
                    : null;

                try
                {
                    using (var rebuilt = new QvecDatabase(
                        rebuiltPath,
                        _header.VectorDimension,
                        _header.MaxCount,
                        _header.MaxNeighbors,
                        _header.MaxLayers,
                        _header.DistanceFunction,
                        quantization: Quantization,
                        changeTracking: tracking))
                    {
                        foreach (var entry in live)
                        {
                            rebuilt.AddEntry(entry.Vector, entry.Metadata, entry.Id);
                        }

                        if (_tracking)
                        {
                            // Rows are assigned sequentially, so live[n] sits at row n. Versions
                            // must survive a vacuum unchanged or peers would re-pull everything.
                            for (int n = 0; n < live.Count; n++)
                                rebuilt.WriteVersionToDisk(n, live[n].Version.Hlc, live[n].Version.Origin);

                            // The rebuild's own AddEntry calls logged upserts with fresh versions;
                            // replace that with the source ring so peers' cursors stay valid.
                            rebuilt.WritePackedChangeLog(ReadChangeLog());
                            rebuilt._header.ChangeSeq = _header.ChangeSeq;
                            rebuilt._header.LastHlc = Math.Max(rebuilt._header.LastHlc, _header.LastHlc);
                            rebuilt._header.TrackingEnabledUnixSeconds = _header.TrackingEnabledUnixSeconds;
                            rebuilt.CommitHeader();
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
            _quantized = _header.QuantizationMode != 0;
            _rescore = QvecFormatLayout.HasRescoringFloats(_header);
            if (_quantized)
            {
                _codesSectionOffset = _header.GetRequiredSection(V4SectionIds.QuantizedVectors).Offset;
                _quantParamsSectionOffset = _header.GetRequiredSection(V4SectionIds.QuantizationVectorParameters).Offset;
                _floatVectorSectionOffset = _rescore ? _header.GetRequiredSection(V4SectionIds.Vectors).Offset : 0;
            }
            else
            {
                _floatVectorSectionOffset = _header.GetRequiredSection(V4SectionIds.Vectors).Offset;
                _codesSectionOffset = 0;
                _quantParamsSectionOffset = 0;
            }
            _graphSectionOffset = _header.GetRequiredSection(V4SectionIds.Graph).Offset;
            _metadataSectionOffset = _header.GetRequiredSection(V4SectionIds.MetadataDescriptors).Offset;
            _metadataHeapOffset = _header.GetRequiredSection(V4SectionIds.MetadataHeap).Offset;
            _guidSectionOffset = _header.GetRequiredSection(V4SectionIds.Guids).Offset;
            _tombstoneSectionOffset = _header.GetRequiredSection(V4SectionIds.Tombstones).Offset;
            _freeListSectionOffset = _header.GetRequiredSection(V4SectionIds.FreeList).Offset;

            _tracking = _header.HasChangeTracking;
            _entryVersionsSectionOffset = _tracking ? _header.GetRequiredSection(V4SectionIds.EntryVersions).Offset : 0;
            _changeLogSectionOffset = _tracking ? _header.GetRequiredSection(V4SectionIds.ChangeLog).Offset : 0;
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
            ApplyRelayout(grown);

            // The caller is in the middle of a write, so the header must go straight back to
            // "write in progress" -- now against the new geometry.
            BeginWrite();
        }

        /// <summary>
        /// Rewrites the file to the geometry described by <paramref name="target"/>, which must
        /// have been derived from the current header (<see cref="QvecFormatLayout.CreateGrown"/>
        /// or <see cref="QvecFormatLayout.CreateTracked"/>): every present section is copied to
        /// its new offset, the mapping is re-established and a clean header committed. Sections new
        /// in the target land in zero-filled space beyond the old file end. Callers hold the write lock.
        /// </summary>
        private void ApplyRelayout(V4Header target)
        {
            // Publish "a write is in progress" against the OLD geometry before anything moves,
            // so a crash during the move is detectable.
            BeginWrite();
            _dataAccessor.Flush();
            _headerAccessor.Flush();

            var moves = PlanSectionMoves(_header, target);

            // Ring slots are addressed modulo capacity, so a capacity change invalidates every
            // slot index. Lift the records out now and re-pack them against the new geometry.
            List<ChangeRecord>? ring = _tracking && _header.ChangeLogCapacity != target.ChangeLogCapacity
                ? ReadChangeLog()
                : null;

            ReleaseMapping();

            try
            {
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    stream.SetLength(target.FileLength);
                    MoveSections(stream, moves);
                    stream.Flush(flushToDisk: true);
                }

                _previousMaxCount = _header.MaxCountRaw;
                _header = target;
                RemapAfterGrow();
                InitialiseNewCapacity();
                if (ring is not null) WritePackedChangeLog(ring);
                CommitHeader();
                _headerAccessor.Flush();
            }
            catch
            {
                // The file is now in an unknown state and the mapping is gone. Reopening is the
                // only safe recovery, and it will fail loudly if the relayout corrupted the file.
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
            CaptureSectionOffsets();

            long totalSize = _header.FileLength;
            _mmf = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, null, totalSize);
            _headerAccessor = _mmf.CreateViewAccessor(0, HeaderSize);
            _dataAccessor = _mmf.CreateViewAccessor(HeaderSize, totalSize - HeaderSize);

            _guidIndex.Clear();
            _deletedIndices.Clear();
            RebuildGuidIndex();
            LoadTombstones();
            LoadDeletedVersions();
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
                RecordLocalUpsert(index, docId);
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
                    if (!ConnectNewNode(index, vector, level, _insertScratch) || level > _header.EntryPointLevel)
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

        /// <summary>
        /// Inserts a batch of entries, building the graph links for several entries at once.
        /// Returns one id per input entry, in input order. Entries whose
        /// <see cref="QvecInsert.ExternalId"/> already exists, in the database or earlier in the
        /// batch, are skipped and the existing id is returned for them, as
        /// <see cref="AddEntry"/> does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The database's write lock is held for the whole call, so searches wait until the batch
        /// is complete. Within the batch, rows are allocated and written serially, then wired
        /// into the graph by up to <paramref name="maxDegreeOfParallelism"/> threads, each with
        /// its own search scratch space. Neighbour lists are updated under per-node striped
        /// locks; the entry point changes under a separate lock and is rare.
        /// </para>
        /// <para>
        /// With more than one thread the order in which nodes are linked depends on scheduling,
        /// so two builds of the same data are not byte-identical even with an index seed. The
        /// graph is still a valid HNSW graph and recall is measured to be within noise of a
        /// serial build. A degree of parallelism of one reproduces the serial build exactly.
        /// </para>
        /// </remarks>
        /// <param name="entries">Vectors, metadata and optional ids to insert.</param>
        /// <param name="maxDegreeOfParallelism">
        /// Threads used to link nodes into the graph. -1 (the default) uses
        /// <see cref="Environment.ProcessorCount"/>; 1 gives the same graph as calling
        /// <see cref="AddEntry"/> in a loop.
        /// </param>
        public IReadOnlyList<Guid> AddEntries(IReadOnlyList<QvecInsert> entries, int maxDegreeOfParallelism = -1)
        {
            ArgumentNullException.ThrowIfNull(entries);
            if (maxDegreeOfParallelism == 0 || maxDegreeOfParallelism < -1)
                throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), maxDegreeOfParallelism, "Use -1 for all processors or a positive thread count.");

            for (int i = 0; i < entries.Count; i++)
            {
                ValidateVector(entries[i].Vector, nameof(entries));
                ArgumentNullException.ThrowIfNull(entries[i].Metadata, nameof(entries));
            }

            var ids = new Guid[entries.Count];
            if (entries.Count == 0) return ids;

            int threads = maxDegreeOfParallelism == -1 ? Environment.ProcessorCount : maxDegreeOfParallelism;

            _lock.EnterWriteLock();
            try
            {
                // Bounds how many prepared vectors are held in memory at a time and how much
                // work sits behind a single header commit.
                const int chunkSize = 8192;
                var pending = new List<PendingInsert>(Math.Min(chunkSize, entries.Count));

                for (int start = 0; start < entries.Count; start += chunkSize)
                {
                    int end = Math.Min(start + chunkSize, entries.Count);
                    pending.Clear();

                    // Phase A, serial: rows, ids, levels. Growth may remap the file here, which
                    // is why no other thread touches the mapping yet.
                    for (int i = start; i < end; i++)
                    {
                        var entry = entries[i];
                        Guid docId = entry.ExternalId ?? Guid.NewGuid();
                        ids[i] = docId;
                        if (_guidIndex.ContainsKey(docId)) continue;

                        int index = AllocateSlot();
                        int level = RandomLayer();
                        float[] vector = PrepareVector(entry.Vector);

                        WriteVectorToDisk(index, vector);
                        WriteMetadataToDisk(index, entry.Metadata);
                        WriteGuidToDisk(index, docId);
                        RecordLocalUpsert(index, docId);
                        InitNeighborsOnDisk(index);
                        _guidIndex[docId] = index;

                        pending.Add(new PendingInsert(index, level, vector));
                    }

                    if (pending.Count == 0) continue;

                    // Phase B, parallel: link the rows into the graph.
                    LinkPending(pending, threads);
                    CommitHeader();
                }

                return ids;
            }
            finally { _lock.ExitWriteLock(); }
        }

        private readonly record struct PendingInsert(int Index, int Level, float[] Vector);

        /// <summary>Guards the entry point while <see cref="AddEntries"/> links nodes concurrently.</summary>
        private readonly ReaderWriterLockSlim _topologyLock = new(LockRecursionPolicy.NoRecursion);

        private void LinkPending(List<PendingInsert> pending, int threads)
        {
            int first = 0;
            if (!HasLiveEntryPoint())
            {
                // Nothing to attach to yet: the first row becomes the entry point serially so the
                // rest of the batch has a graph to descend from.
                var seed = pending[0];
                ConnectNewNode(seed.Index, seed.Vector, seed.Level, _insertScratch);
                _header.EntryPoint = seed.Index;
                _header.EntryPointLevel = seed.Level;
                first = 1;
            }

            if (threads == 1 || pending.Count - first < 2)
            {
                for (int i = first; i < pending.Count; i++)
                    LinkOne(pending[i], _insertScratch);
                return;
            }

            using var scratch = new ThreadLocal<SearchScratch>(() => new SearchScratch());
            var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
            Parallel.For(first, pending.Count, options, i => LinkOne(pending[i], scratch.Value!));
        }

        private bool HasLiveEntryPoint()
        {
            int entryPoint = _header.EntryPoint;
            return entryPoint >= 0 && entryPoint < _header.CurrentCount && !_deletedIndices.Contains(entryPoint);
        }

        private void LinkOne(PendingInsert node, SearchScratch scratch)
        {
            _topologyLock.EnterReadLock();
            try
            {
                if (node.Level <= _header.EntryPointLevel && ConnectNewNode(node.Index, node.Vector, node.Level, scratch))
                    return;
            }
            finally { _topologyLock.ExitReadLock(); }

            // The node reaches above the current entry point, or found nothing to attach to.
            // Either way it becomes the new entry point, which must not happen while other
            // threads are descending from the old one.
            _topologyLock.EnterWriteLock();
            try
            {
                if (!ConnectNewNode(node.Index, node.Vector, node.Level, scratch) || node.Level > _header.EntryPointLevel)
                {
                    _header.EntryPoint = node.Index;
                    _header.EntryPointLevel = node.Level;
                }
            }
            finally { _topologyLock.ExitWriteLock(); }
        }

        private unsafe void WriteVectorToDisk(int index, float[] vector)
        {
            BeginWrite();
            int dim = _header.VectorDimension;

            if (_quantized)
            {
                var parameters = Int8Quantizer.Quantize(vector.AsSpan(0, dim), new Span<byte>(CodesPointer(index), dim));
                parameters.WriteTo(new Span<byte>(ParamsPointer(index), Int8VectorParameters.Size));
                if (!_rescore) return;
            }

            long bytes = (long)dim * sizeof(float);
            fixed (float* source = vector)
            {
                Buffer.MemoryCopy(source, VectorPointer(index), bytes, bytes);
            }
        }

        /// <summary>Overwrites a row's int8 codes and parameters verbatim. Only valid for pure int8 files.</summary>
        private unsafe void WriteCodesToDisk(int index, byte[] codes, Int8VectorParameters parameters)
        {
            BeginWrite();
            codes.AsSpan(0, _header.VectorDimension).CopyTo(new Span<byte>(CodesPointer(index), _header.VectorDimension));
            parameters.WriteTo(new Span<byte>(ParamsPointer(index), Int8VectorParameters.Size));
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

        private unsafe void InitNeighborsOnDisk(int index)
        {
            BeginWrite();
            long offset = _graphSectionOffset + (long)index * _cachedEmptyNeighbors.Length * sizeof(int);
            long bytes = (long)_cachedEmptyNeighbors.Length * sizeof(int);
            fixed (int* source = _cachedEmptyNeighbors)
            {
                Buffer.MemoryCopy(source, DataBasePointer + (offset - HeaderSize), bytes, bytes);
            }
        }
        // --- INVERTED INDEX ---

        /// <summary>
        /// Adds an entry to the inverted index for a specific field and value.
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
        /// Indexes an entry by its document Guid. This is the safe alternative to
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
        /// Removes an entry from the inverted index.
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
        /// O(1) search via the inverted index. Requires the field to have been indexed at insert.
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
        /// O(1) search via the inverted index with multiple fields (AND / intersection).
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
        /// Rebuilds the inverted index from disk. Called at startup if an extractor exists.
        /// </summary>
        public void RebuildFieldIndex(Func<string, IEnumerable<(string Field, string Value)>> extractor)
        {
            ArgumentNullException.ThrowIfNull(extractor);
            _lock.EnterWriteLock();
            try
            {
                FieldIndexExtractor = extractor;
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
        /// Returns candidate indexes from the inverted index for one or more fields (AND).
        /// Returns null if any field has no match.
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
        /// Vector search only over pre-filtered candidate indexes.
        /// Computes similarity only for entries in candidates — no HNSW, no full scan.
        /// </summary>
        public List<(Guid Id, float Score, string Metadata)> SearchWithCandidates(float[] query, HashSet<int> candidates, int topK)
        {
            ValidateVector(query, nameof(query));
            ArgumentNullException.ThrowIfNull(candidates);
            ValidateTopK(topK);

            _lock.EnterReadLock();
            try
            {
                var prepared = Prepare(query);

                var scored = new List<(int Index, float Score)>(candidates.Count);
                foreach (int i in candidates)
                {
                    if (_deletedIndices.Contains(i)) continue;
                    scored.Add((i, FinalScore(prepared, i)));
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
        /// Filters entries only on metadata without vector search.
        /// Uses a parallel scan for large datasets, sequential for small ones.
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
        /// SimpleSearch is a basic linear search that does not use the HNSW graph
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

                var prepared = Prepare(query);

                var candidates = new List<(int Index, float Score)>();
                for (int i = 0; i < _header.CurrentCount; i++)
                {
                    if (_deletedIndices.Contains(i)) continue;
                    string meta = GetMetadata(i);
                    if (filter != null && !filter(meta)) continue;

                    candidates.Add((i, FinalScore(prepared, i)));
                }

                return candidates.OrderByDescending(c => c.Score)
                                 .Take(topK)
                                 .Select(c => (ReadGuidFromDisk(c.Index), c.Score, GetMetadata(c.Index)))
                                 .ToList();
            }
            finally { _lock.ExitReadLock(); }
        }
        /// <summary>
        /// SearchSimpleParallel is an optimized version of SearchSimple that uses all CPU cores.
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

            var prepared = Prepare(query);

            int count = _header.CurrentCount;

            var partialResults = new (int Index, float Score)[count];

            // Force the lazily acquired base pointer before fanning out: the first access
            // initialises a shared field and is not safe to race.
            _ = DataBasePointer;

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

                partialResults[i] = (i, FinalScore(prepared, i));
            });

            return partialResults
                .Where(r => r.Score > float.MinValue)
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .Select(r => (ReadGuidFromDisk(r.Index), r.Score, GetMetadata(r.Index)))
                .ToList();
        }
        /// <summary>
        /// HybridHNSW is a search method that combines fast HNSW navigation with the ability to filter on metadata during the search process.
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
            var scratch = RentSearchScratch();
            try
            {
                if (IsEffectivelyEmpty) return new List<(Guid, float, string)>();

                var prepared = Prepare(query, scratch);

                int entryPoint = ResolveEntryPoint();
                if (entryPoint < 0) return new List<(Guid, float, string)>();

                for (int level = _header.EntryPointLevel; level >= 1; level--)
                {
                    entryPoint = GreedyClosest(prepared, entryPoint, level);
                }

                int liveCount = LiveCount;
                int ef = Math.Max(topK, efSearch);

                // Filtering happens inside the graph walk (see SearchLayerFiltered), so a
                // selective filter no longer starves the result set the way post-filtering did.
                // The budget keeps a pathologically selective filter from touring the whole graph
                // more expensively than a linear scan would cost.
                int visitBudget = Math.Min(liveCount, Math.Max(ef * 16, 1024));

                var nearest = SearchLayerFiltered(
                    prepared, entryPoint, 0, ef, filter, visitBudget, out _, scratch);
                RescoreCandidates(prepared, nearest);

                // The walk returns its results best-first; rescoring can reorder them. The
                // sort is stable, like the OrderByDescending it replaces, so ties keep walk order.
                if (_rescore) InsertionSortDescending(nearest);

                int count = Math.Min(topK, nearest.Length);
                var matches = new List<(Guid Id, float Score, string Metadata)>(count);
                for (int i = 0; i < count; i++)
                    matches.Add((ReadGuidFromDisk(nearest[i].Id), nearest[i].Score, nearest[i].Meta));

                if (matches.Count >= topK) return matches;

                // Short of topK. Either the walk hit its budget, or matching entries are genuinely
                // unreachable from the entry point: a distant cluster can end up with outgoing
                // edges only, so no walk will ever reach it. Neither case is distinguishable
                // cheaply, and under-returning silently is the worse failure, so fall back to an
                // exact scan. This only costs O(N) when the filter really does match fewer than
                // topK reachable entries.
                return ExhaustiveFilteredSearch(prepared, filter, topK);
            }
            finally
            {
                ReturnSearchScratch(scratch);
                _lock.ExitReadLock();
            }
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
            PreparedQuery query, Func<string, bool> filter, int topK)
        {
            Interlocked.Increment(ref _filteredFallbackCount);

            var matches = new List<(int Index, float Score, string Meta)>();

            for (int i = 0; i < _header.CurrentCount; i++)
            {
                if (_deletedIndices.Contains(i)) continue;

                string meta = GetMetadata(i);
                if (!filter(meta)) continue;

                matches.Add((i, FinalScore(query, i), meta));
            }

            return matches.OrderByDescending(m => m.Score)
                          .Take(topK)
                          .Select(m => (ReadGuidFromDisk(m.Index), m.Score, m.Meta))
                          .ToList();
        }

        /// <summary>
        /// HNSW search that navigates through the layers and returns a list of the best matches, including metadata.
        /// </summary>
        /// <param name="query"></param>
        /// <param name="topK"></param>
        /// <param name="efSearch">Search width in the bottom layer. Higher = better recall but slower. Default: 200.</param>
        /// <returns></returns>
        public List<(Guid Id, float Score, string Metadata)> Search(float[] query, int topK = 5, int efSearch = DefaultEfSearch)
        {
            ValidateVector(query, nameof(query));
            ValidateTopK(topK);
            ValidateEfSearch(efSearch);

            _lock.EnterReadLock();
            var scratch = RentSearchScratch();
            try
            {
                if (IsEffectivelyEmpty) return new List<(Guid, float, string)>();

                var prepared = Prepare(query, scratch);

                int entryPoint = ResolveEntryPoint();
                if (entryPoint < 0) return new List<(Guid, float, string)>();

                for (int level = _header.EntryPointLevel; level >= 1; level--)
                {
                    entryPoint = GreedyClosest(prepared, entryPoint, level);
                }

                int ef = Math.Max(topK, efSearch);
                var nearest = SearchLayerNearest(prepared, entryPoint, 0, ef, scratch);
                // Rescored files: the walk ranked ef candidates on int8 codes; the exact floats
                // decide the final order and the reported scores.
                RescoreCandidates(prepared, nearest);

                // The walk returns its results best-first; rescoring can reorder them. The
                // sort is stable, like the OrderByDescending it replaces, so ties keep walk order.
                if (_rescore) InsertionSortDescending(nearest);

                int count = Math.Min(topK, nearest.Length);
                var matches = new List<(Guid Id, float Score, string Metadata)>(count);
                for (int i = 0; i < count; i++)
                    matches.Add((ReadGuidFromDisk(nearest[i].Id), nearest[i].Score, GetMetadata(nearest[i].Id)));
                return matches;
            }
            finally
            {
                ReturnSearchScratch(scratch);
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Stable descending sort by score for the small (ef-sized) candidate arrays that come
        /// out of a layer walk. Insertion sort is stable and allocation-free, which
        /// <see cref="Array.Sort{T}(T[], IComparer{T})"/> is not.
        /// </summary>
        private static void InsertionSortDescending((int Id, float Score)[] items)
        {
            for (int i = 1; i < items.Length; i++)
            {
                var item = items[i];
                int j = i - 1;
                while (j >= 0 && items[j].Score < item.Score)
                {
                    items[j + 1] = items[j];
                    j--;
                }
                items[j + 1] = item;
            }
        }

        private static void InsertionSortDescending((int Id, float Score, string Meta)[] items)
        {
            for (int i = 1; i < items.Length; i++)
            {
                var item = items[i];
                int j = i - 1;
                while (j >= 0 && items[j].Score < item.Score)
                {
                    items[j + 1] = items[j];
                    j--;
                }
                items[j + 1] = item;
            }
        }

        /// <summary>
        /// Wires a freshly written node into the graph. Returns false when there is no live
        /// node to attach to, in which case the caller must promote the new node to entry point.
        /// </summary>
        /// <remarks>
        /// Safe to run for several new nodes at once from <see cref="AddEntries"/>: every
        /// read-modify-write of a neighbour list happens under that node's stripe lock, and the
        /// new node's own list is merged with any back-links other threads have already written
        /// into it rather than overwritten. Searches read neighbour lists without locking, which
        /// is fine because slots are written as whole int32s and hold either a valid row or -1.
        /// </remarks>
        private bool ConnectNewNode(int newIndex, float[] newVector, int newLevel, SearchScratch scratch)
        {
            int currentElement = ResolveEntryPoint();
            if (currentElement < 0 || currentElement == newIndex) return false;
            var query = StoredQuery(newIndex, newVector);
            for (int level = _header.EntryPointLevel; level > newLevel; level--)
            {
                currentElement = GreedyClosest(query, currentElement, level);
            }

            for (int level = Math.Min(newLevel, _header.MaxLayers - 1); level >= 0; level--)
            {
                var candidates = SearchLayerNearest(query, currentElement, level, EfConstruction, scratch);
                var nearest = SelectNeighborsHeuristic(candidates, NeighborsAtLevel(level));

                lock (NodeLock(newIndex))
                {
                    MergeNeighborsAtLevel(newIndex, level, nearest);
                }

                foreach (var (neighborId, _) in nearest)
                {
                    if (neighborId < 0) break;
                    lock (NodeLock(neighborId))
                    {
                        AddNeighborConnection(neighborId, level, newIndex);
                    }
                }

                if (candidates.Length > 0 && candidates[0].Id >= 0)
                {
                    currentElement = candidates[0].Id;
                }
            }

            return true;
        }

        private const int NodeLockCount = 1024;
        private readonly object[] _nodeLocks = CreateNodeLocks();

        private static object[] CreateNodeLocks()
        {
            var locks = new object[NodeLockCount];
            for (int i = 0; i < locks.Length; i++) locks[i] = new object();
            return locks;
        }

        /// <summary>
        /// Striped lock for one node's neighbour lists. Never taken nested, so two stripes can
        /// not deadlock; the stripe count only bounds how often unrelated nodes collide.
        /// </summary>
        private object NodeLock(int nodeIndex) => _nodeLocks[nodeIndex & (NodeLockCount - 1)];

        /// <summary>
        /// Writes a new node's selected neighbours, keeping any back-links that concurrent
        /// inserts have already placed in the list. On the serial path the list is still all -1
        /// from <see cref="InitNeighborsOnDisk"/>, so this is a plain write there.
        /// </summary>
        private unsafe void MergeNeighborsAtLevel(int nodeIndex, int level, (int Id, float Score)[] nearest)
        {
            int slots = NeighborsAtLevel(level);
            int* target = NeighborPointer(nodeIndex, level);

            int existing = 0;
            while (existing < slots && target[existing] != -1) existing++;

            if (existing == 0)
            {
                WriteNeighborsAtLevel(nodeIndex, level, nearest);
                return;
            }

            int[] merged = ArrayPool<int>.Shared.Rent(slots);
            try
            {
                int count = 0;
                for (int i = 0; i < nearest.Length && count < slots; i++)
                {
                    if (nearest[i].Id < 0) break;
                    merged[count++] = nearest[i].Id;
                }

                for (int i = 0; i < existing && count < slots; i++)
                {
                    int id = target[i];
                    bool duplicate = false;
                    for (int j = 0; j < count; j++)
                    {
                        if (merged[j] == id) { duplicate = true; break; }
                    }
                    if (!duplicate) merged[count++] = id;
                }

                for (int i = count; i < slots; i++) merged[i] = -1;
                WriteNeighborsAtLevel(nodeIndex, level, merged.AsSpan(0, slots));
            }
            finally
            {
                ArrayPool<int>.Shared.Return(merged);
            }
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

            int live = 0;
            for (int i = 0; i < candidates.Length; i++)
                if (candidates[i].Id >= 0) live++;

            var ordered = new (int Id, float Score)[live];
            for (int i = 0, j = 0; i < candidates.Length; i++)
                if (candidates[i].Id >= 0) ordered[j++] = candidates[i];

            // Stable, like the OrderByDescending it replaces, so equal scores keep candidate
            // order and the resulting graph is unchanged.
            StableSortDescending(ordered);

            if (ordered.Length <= m) return ordered;

            return PruneNeighbors(ordered, m);
        }

        /// <summary>
        /// Stable descending sort by score. Equal scores keep their input order, exactly as the
        /// LINQ OrderByDescending this replaces, so the resulting graph is unchanged.
        /// </summary>
        private static void StableSortDescending((int Id, float Score)[] items)
        {
            var order = new int[items.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;

            Array.Sort(order, (a, b) =>
            {
                int byScore = items[b].Score.CompareTo(items[a].Score);
                return byScore != 0 ? byScore : a.CompareTo(b);
            });

            var sorted = new (int Id, float Score)[items.Length];
            for (int i = 0; i < order.Length; i++) sorted[i] = items[order[i]];
            sorted.CopyTo(items, 0);
        }

        /// <summary>
        /// Neighbour selection heuristic from Malkov &amp; Yashunin, algorithm 4.
        /// Keeps a candidate only when it is closer to the owning node than to any candidate
        /// already selected, which preserves long-range links instead of collapsing the
        /// neighbourhood into a single tight cluster.
        /// </summary>
        /// <remarks>
        /// Candidate vectors are read in place from the mapped file. An earlier version copied
        /// them into a rented contiguous buffer first; with M0 = 64 that meant renting and
        /// filling a 33 KiB buffer for every one of the up to 64 back-links an insert creates,
        /// and the pool traffic outweighed the O(m²) comparisons it was feeding.
        /// </remarks>
        /// <param name="ordered">Candidates sorted by descending similarity to the owner. Every id must be a stored row.</param>
        private (int Id, float Score)[] PruneNeighbors((int Id, float Score)[] ordered, int m)
        {
            var selected = new List<(int Id, float Score)>(m);
            var discarded = new List<(int Id, float Score)>();

            for (int i = 0; i < ordered.Length && selected.Count < m; i++)
            {
                var candidate = ordered[i];

                bool closerToOwnerThanToNeighbours = true;
                foreach (var (selectedId, _) in selected)
                {
                    // Score is a similarity: higher means closer.
                    if (StoredSimilarity(candidate.Id, selectedId) > candidate.Score)
                    {
                        closerToOwnerThanToNeighbours = false;
                        break;
                    }
                }

                if (closerToOwnerThanToNeighbours)
                    selected.Add(candidate);
                else
                    discarded.Add(candidate);
            }

            // keepPrunedConnections: top up with the best discarded candidates rather than
            // returning an under-filled neighbour list.
            for (int i = 0; i < discarded.Count && selected.Count < m; i++)
                selected.Add(discarded[i]);

            return selected.ToArray();
        }
        private unsafe int GreedyClosest(PreparedQuery query, int entryPoint, int level)
        {
            int current = entryPoint;
            float currentScore = CalculateScore(query, current);
            int slots = NeighborsAtLevel(level);
            bool changed = true;
            while (changed)
            {
                changed = false;
                // Read in place: the lock held by every caller keeps the mapping from moving,
                // and slots are whole int32s, so this sees the same values a copy would. The
                // pointer stays on the node the scan started from even after current moves.
                int* neighbors = NeighborPointer(current, level);
                for (int j = 0; j < slots; j++)
                {
                    int neighbor = neighbors[j];
                    if (neighbor == -1) break;
                    if (_deletedIndices.Contains(neighbor)) continue;
                    float score = CalculateScore(query, neighbor);
                    if (score > currentScore)
                    {
                        currentScore = score;
                        current = neighbor;
                        changed = true;
                    }
                }
            }
            return current;
        }
        /// <summary>
        /// Reusable per-walk scratch state: the epoch-stamped visited set, the two heaps, a
        /// neighbour buffer and the prepared query's buffers. The insert path owns one instance
        /// under the write lock (plus one per thread in <see cref="AddEntries"/>); the read path
        /// rents from <see cref="_searchScratchPool"/> so that concurrent searches allocate
        /// nothing per query beyond the results they return. Before the pool a single
        /// <c>Search</c> allocated 30–68 KB (design-performance.md §2.1).
        /// </summary>
        private sealed class SearchScratch
        {
            public int[] VisitedEpoch = Array.Empty<int>();
            public int Epoch;
            public readonly PriorityQueue<int, float> Candidates = new();
            public readonly PriorityQueue<int, float> Results = new();
            public readonly PreparedQuery Query = new();
            public float[]? PreparedFloats;
            public byte[]? QueryCodes;

            public void Begin(int capacity)
            {
                if (VisitedEpoch.Length < capacity)
                    Array.Resize(ref VisitedEpoch, Math.Max(capacity, VisitedEpoch.Length * 2));

                // Wrap-around resets the whole array once every int.MaxValue searches.
                if (++Epoch == int.MaxValue)
                {
                    Array.Clear(VisitedEpoch);
                    Epoch = 1;
                }

                Candidates.Clear();
                Results.Clear();
            }

            public bool Visit(int id)
            {
                if (VisitedEpoch[id] == Epoch) return false;
                VisitedEpoch[id] = Epoch;
                return true;
            }
        }

        private readonly SearchScratch _insertScratch = new();

        /// <summary>
        /// Scratch objects for the concurrent read path. Bounded in practice by the number of
        /// threads that have ever searched at once; each holds a visited array of
        /// <c>MaxCount</c> ints. Released with the database.
        /// </summary>
        private readonly ConcurrentBag<SearchScratch> _searchScratchPool = new();

        private SearchScratch RentSearchScratch()
            => _searchScratchPool.TryTake(out var scratch) ? scratch : new SearchScratch();

        private void ReturnSearchScratch(SearchScratch scratch) => _searchScratchPool.Add(scratch);

        private unsafe (int Id, float Score)[] SearchLayerNearest(PreparedQuery query, int entryPoint, int level, int ef, SearchScratch scratch)
        {
            scratch.Begin(_header.MaxCount);
            scratch.Visit(entryPoint);
            var candidates = scratch.Candidates;
            var results = scratch.Results;

            float entryScore = CalculateScore(query, entryPoint);
            candidates.Enqueue(entryPoint, -entryScore);
            results.Enqueue(entryPoint, entryScore);
            float worstScore = entryScore;

            int slots = NeighborsAtLevel(level);
            while (candidates.TryDequeue(out int candidateId, out float negScore))
            {
                float candidateScore = -negScore;
                if (candidateScore < worstScore && results.Count >= ef)
                    break;

                // Neighbour lists are read in place (see GreedyClosest).
                int* neighbors = NeighborPointer(candidateId, level);
                for (int j = 0; j < slots; j++)
                {
                    int neighbor = neighbors[j];
                    if (neighbor < 0) break;
                    if (!scratch.Visit(neighbor)) continue;
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
        private unsafe (int Id, float Score, string Meta)[] SearchLayerFiltered(
            PreparedQuery query, int entryPoint, int level, int ef,
            Func<string, bool> filter, int visitBudget, out bool budgetExhausted, SearchScratch scratch)
        {
            budgetExhausted = false;

            scratch.Begin(_header.MaxCount);
            scratch.Visit(entryPoint);
            int visitedCount = 1;

            // Metadata is kept only for nodes that entered the result set; it is what the
            // caller gets back. Non-matching nodes are decoded for the filter and dropped.
            var metadata = new Dictionary<int, string>();

            float entryScore = CalculateScore(query, entryPoint);

            var candidates = scratch.Candidates;
            candidates.Enqueue(entryPoint, -entryScore);

            // Only matching nodes enter the result set; the entry point is not special-cased.
            var results = scratch.Results;
            float worstScore = float.MinValue;

            void TryAdmit(int node, float score)
            {
                string meta = GetMetadata(node);
                if (!filter(meta)) return;

                if (results.Count < ef || score > worstScore)
                {
                    metadata[node] = meta;
                    results.Enqueue(node, score);
                    if (results.Count > ef)
                    {
                        metadata.Remove(results.Dequeue());
                    }
                    results.TryPeek(out _, out worstScore);
                }
            }

            if (!_deletedIndices.Contains(entryPoint))
                TryAdmit(entryPoint, entryScore);

            int slots = NeighborsAtLevel(level);
            while (candidates.TryDequeue(out int candidateId, out float negScore))
            {
                // Stop only once the result set is full, since until then a worse-scoring
                // candidate may still be the only route to a matching node.
                if (results.Count >= ef && -negScore < worstScore)
                    break;

                if (visitedCount > visitBudget)
                {
                    budgetExhausted = true;
                    break;
                }

                int* neighbors = NeighborPointer(candidateId, level);
                for (int j = 0; j < slots; j++)
                {
                    int neighbor = neighbors[j];
                    if (neighbor < 0) break;
                    if (!scratch.Visit(neighbor)) continue;
                    visitedCount++;
                    if (_deletedIndices.Contains(neighbor)) continue;

                    float score = CalculateScore(query, neighbor);

                    // The frontier is deliberately unfiltered: a non-matching node is still a
                    // valid stepping stone towards matching ones.
                    candidates.Enqueue(neighbor, -score);
                    TryAdmit(neighbor, score);
                }
            }

            var resultArray = new (int Id, float Score, string Meta)[results.Count];
            int idx = results.Count - 1;
            while (results.TryDequeue(out int id, out float score))
            {
                resultArray[idx--] = (id, score, metadata[id]);
            }
            return resultArray;
        }

        private unsafe void WriteNeighborsAtLevel(int nodeIndex, int level, (int Id, float Score)[] neighbors)
        {
            BeginWrite();
            int slots = NeighborsAtLevel(level);
            int count = Math.Min(neighbors.Length, slots);

            int* target = NeighborPointer(nodeIndex, level);
            for (int i = 0; i < count; i++) target[i] = neighbors[i].Id;
            for (int i = count; i < slots; i++) target[i] = -1;
        }

        private unsafe void WriteNeighborsAtLevel(int nodeIndex, int level, ReadOnlySpan<int> neighborIds)
        {
            BeginWrite();
            int slots = NeighborsAtLevel(level);
            int count = Math.Min(neighborIds.Length, slots);

            int* target = NeighborPointer(nodeIndex, level);
            for (int i = 0; i < count; i++) target[i] = neighborIds[i];
            for (int i = count; i < slots; i++) target[i] = -1;
        }

        /// <summary>
        /// Adds a back-reference from an existing node to a newly inserted one.
        /// When the neighbour list is full the replacement decision goes through the HNSW
        /// pruning heuristic rather than a plain "evict the worst" rule. Evicting purely on
        /// score meant a tightly clustered region never accepted links from a distant cluster,
        /// leaving whole groups of entries with outgoing edges only — unreachable from the
        /// entry point, and therefore invisible to search.
        /// </summary>
        /// <remarks>
        /// The heuristic is applied incrementally, the way Lucene's HNSW graph builder does it
        /// (<c>findWorstNonDiverse</c>), instead of re-running the full O(M0²) selection over
        /// all M0 + 1 candidates. Only the new node is unchecked: an existing neighbour can only
        /// have become redundant because of the new node, and the new node has to be checked
        /// against the neighbours closer than itself. That is O(M0) distance computations per
        /// full back-link rather than O(M0²), and on 768-dimensional vectors, where every
        /// distance is a 3 KiB memory read, the old path was 66 % of insert time.
        /// </remarks>
        private void AddNeighborConnection(int existingNode, int level, int newNode)
        {
            int slots = NeighborsAtLevel(level);
            int[] neighbors = ArrayPool<int>.Shared.Rent(slots);
            try
            {
                GetNeighborsAtLevel(existingNode, level, neighbors);

                for (int i = 0; i < slots; i++)
                {
                    if (neighbors[i] == -1)
                    {
                        neighbors[i] = newNode;
                        WriteNeighborsAtLevel(existingNode, level, neighbors);
                        return;
                    }

                    if (neighbors[i] == newNode) return;
                }

                // The list is full. Scores are recomputed because the graph section stores ids
                // only; the new node's vector is already on disk, so it needs no special casing.
                int candidateCount = slots + 1;
                var candidates = new (int Id, float Score)[candidateCount];

                for (int i = 0; i < slots; i++)
                    candidates[i] = (neighbors[i], StoredSimilarity(existingNode, neighbors[i]));

                candidates[slots] = (newNode, StoredSimilarity(existingNode, newNode));

                Array.Sort(candidates, DescendingScore.Instance);

                int newPosition = 0;
                while (candidates[newPosition].Id != newNode) newPosition++;
                int evict = FindWorstNonDiverse(candidates, newPosition);

                if (evict < 0)
                {
                    // Nothing is redundant because of the new node. The list may still hold
                    // neighbours that were topped up past the diversity check when it was
                    // built (keepPrunedConnections), and those are the ones to give up before
                    // a diverse long-range link. Only the full heuristic can tell them apart.
                    WriteNeighborsAtLevel(existingNode, level, PruneNeighbors(candidates, slots));
                    return;
                }

                // Rejecting the new node leaves the stored list exactly as it was.
                if (evict == newPosition) return;

                for (int i = 0, j = 0; i < candidateCount; i++)
                    if (i != evict) neighbors[j++] = candidates[i].Id;

                WriteNeighborsAtLevel(existingNode, level, neighbors);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighbors);
            }
        }

        /// <summary>
        /// Walks the candidates from farthest to nearest and returns the index of the first one
        /// that is closer to the new node than it is to the owner — the candidate the selection
        /// heuristic would discard because of the insertion. For the new node itself every
        /// nearer candidate is checked. Returns -1 when the new node makes nothing redundant.
        /// </summary>
        /// <param name="candidates">Sorted by descending similarity to the owner.</param>
        /// <param name="newPosition">Index of the newly inserted node; the only candidate the existing neighbours were never checked against.</param>
        private int FindWorstNonDiverse((int Id, float Score)[] candidates, int newPosition)
        {
            for (int i = candidates.Length - 1; i > 0; i--)
            {
                var candidate = candidates[i];

                if (i == newPosition)
                {
                    for (int j = 0; j < i; j++)
                    {
                        if (StoredSimilarity(candidate.Id, candidates[j].Id) > candidate.Score)
                            return i;
                    }
                }
                else if (newPosition < i)
                {
                    if (StoredSimilarity(candidate.Id, candidates[newPosition].Id) > candidate.Score)
                        return i;
                }
            }

            return -1;
        }

        private sealed class DescendingScore : IComparer<(int Id, float Score)>
        {
            public static readonly DescendingScore Instance = new();
            public int Compare((int Id, float Score) a, (int Id, float Score) b) => b.Score.CompareTo(a.Score);
        }

        /// <summary>
        /// Slots available on a given layer. Malkov &amp; Yashunin recommend twice the fan-out on
        /// the base layer (M0 = 2 * M): every node lives on layer 0 and it carries the final,
        /// decisive refinement of a search, so a base layer as narrow as the sparse upper layers
        /// caps achievable recall.
        /// </summary>
        private int NeighborsAtLevel(int level) => level == 0 ? _header.MaxNeighbors * 2 : _header.MaxNeighbors;

        /// <summary>
        /// Widest neighbour list any layer can hold. Scratch buffers are sized by this so one
        /// buffer can serve a loop that walks several layers.
        /// </summary>
        private int MaxNeighborsAnyLevel => _header.MaxNeighbors * 2;

        /// <summary>
        /// Neighbour slots per node: 2 * M for layer 0 plus M for each layer above it.
        /// </summary>
        private int GraphNodeStride => (_header.MaxLayers + 1) * _header.MaxNeighbors;

        /// <summary>
        /// Byte position of a node's neighbour list on one layer, relative to the data accessor.
        /// Layer 0 sits first in the node and is twice as wide, so every layer above it starts
        /// one extra M into the node.
        /// </summary>
        private long NeighborPosition(int nodeIndex, int level)
            => (_graphSectionOffset - HeaderSize)
               + ((long)nodeIndex * GraphNodeStride
                  + (level == 0 ? 0L : (long)(level + 1) * _header.MaxNeighbors)) * sizeof(int);

        private unsafe void GetNeighborsAtLevel(int nodeIndex, int level, int[] buffer)
        {
            long bytes = (long)NeighborsAtLevel(level) * sizeof(int);
            fixed (int* dest = buffer)
            {
                Buffer.MemoryCopy(NeighborPointer(nodeIndex, level), dest, bytes, bytes);
            }
        }

        private int RandomLayer()
        {
            double r = (_layerRng ?? Random.Shared).NextDouble();
            if (r <= 0) r = 0.0001;

            int level = (int)(-Math.Log(r) * _header.LayerProbability);
            return Math.Min(level, _header.MaxLayers - 1);
        }
        /// <summary>
        /// A query vector in whatever form the stored rows can be scored against directly:
        /// prepared floats for a float database, byte codes plus parameters for an int8 one.
        /// Built once per search or insert so the per-candidate scoring loop never re-quantises.
        /// </summary>
        private sealed class PreparedQuery
        {
            public float[] Floats = Array.Empty<float>();
            public byte[]? Codes;
            public Int8VectorParameters Parameters;
        }

        private PreparedQuery Prepare(float[] query)
        {
            var prepared = new PreparedQuery { Floats = PrepareVector(query) };
            if (_quantized)
            {
                prepared.Codes = new byte[_header.VectorDimension];
                prepared.Parameters = Int8Quantizer.Quantize(prepared.Floats.AsSpan(0, _header.VectorDimension), prepared.Codes);
            }
            return prepared;
        }

        /// <summary>
        /// Same as <see cref="Prepare(float[])"/> but into the scratch object's buffers: the
        /// cosine copy and the int8 codes are reused across queries instead of allocated. The
        /// caller's array is referenced directly when no normalisation is needed.
        /// </summary>
        private PreparedQuery Prepare(float[] query, SearchScratch scratch)
        {
            int dim = _header.VectorDimension;
            var prepared = scratch.Query;

            if (_header.DistanceFunction == DistanceFunction.Cosine)
            {
                scratch.PreparedFloats ??= new float[dim];
                Array.Copy(query, scratch.PreparedFloats, dim);
                NormalizeVector(scratch.PreparedFloats);
                prepared.Floats = scratch.PreparedFloats;
            }
            else
            {
                prepared.Floats = query;
            }

            if (_quantized)
            {
                scratch.QueryCodes ??= new byte[dim];
                prepared.Codes = scratch.QueryCodes;
                prepared.Parameters = Int8Quantizer.Quantize(prepared.Floats.AsSpan(0, dim), prepared.Codes);
            }
            else
            {
                prepared.Codes = null;
            }

            return prepared;
        }

        /// <summary>
        /// The query for a row that is already on disk, used when wiring a new node into the
        /// graph. In int8 mode the stored codes are read back rather than re-quantised so the
        /// node is scored exactly as its neighbours will later score it.
        /// </summary>
        private unsafe PreparedQuery StoredQuery(int index, float[] preparedFloats)
        {
            var prepared = new PreparedQuery { Floats = preparedFloats };
            if (_quantized)
            {
                int dim = _header.VectorDimension;
                prepared.Codes = new byte[dim];
                new ReadOnlySpan<byte>(CodesPointer(index), dim).CopyTo(prepared.Codes);
                prepared.Parameters = ParamsAt(index);
            }
            return prepared;
        }

        private unsafe float CalculateScore(PreparedQuery query, int targetIndex)
        {
            int dim = _header.VectorDimension;
            if (_quantized)
            {
                fixed (byte* codes = query.Codes)
                {
                    return Int8Quantizer.Similarity(
                        _header.DistanceFunction, codes, query.Parameters,
                        CodesPointer(targetIndex), ParamsAt(targetIndex), dim);
                }
            }

            return SimilarityUnsafe(query.Floats, VectorPointer(targetIndex), dim);
        }

        /// <summary>
        /// The score a search result is reported with. In rescored int8 mode this is the exact
        /// float similarity read from the optional float section; otherwise it is the same score
        /// the graph walk used. Callers must hold the read lock.
        /// </summary>
        private unsafe float FinalScore(PreparedQuery query, int targetIndex)
        {
            if (_rescore)
            {
                return SimilarityUnsafe(query.Floats, VectorPointer(targetIndex), _header.VectorDimension);
            }

            return CalculateScore(query, targetIndex);
        }

        /// <summary>
        /// Re-ranks the candidates a graph walk produced against the exact floats. A no-op unless
        /// the file is rescored, so float and plain int8 searches pay nothing for it.
        /// </summary>
        private void RescoreCandidates(PreparedQuery query, (int Id, float Score)[] candidates)
        {
            if (!_rescore) return;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Id < 0) continue;
                candidates[i].Score = FinalScore(query, candidates[i].Id);
            }
        }

        private void RescoreCandidates(PreparedQuery query, (int Id, float Score, string Meta)[] candidates)
        {
            if (!_rescore) return;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Id < 0) continue;
                candidates[i].Score = FinalScore(query, candidates[i].Id);
            }
        }

        /// <summary>Score between two stored rows, read in place.</summary>
        private unsafe float StoredSimilarity(int left, int right)
        {
            if (_quantized)
            {
                return Int8Quantizer.Similarity(
                    _header.DistanceFunction,
                    CodesPointer(left), ParamsAt(left),
                    CodesPointer(right), ParamsAt(right),
                    _header.VectorDimension);
            }

            return Similarity(StoredVector(left), StoredVector(right));
        }

        /// <summary>Address of a row's byte codes in section 8. Only valid when <see cref="_quantized"/>.</summary>
        private unsafe byte* CodesPointer(int index)
            => DataBasePointer + (_codesSectionOffset - HeaderSize) + (long)index * _header.VectorDimension;

        /// <summary>Address of a row's 16-byte parameter block in section 10. Only valid when <see cref="_quantized"/>.</summary>
        private unsafe byte* ParamsPointer(int index)
            => DataBasePointer + (_quantParamsSectionOffset - HeaderSize) + (long)index * Int8VectorParameters.Size;

        private unsafe Int8VectorParameters ParamsAt(int index)
            => Int8VectorParameters.ReadFrom(ParamsPointer(index));

        /// <summary>
        /// Address of a stored float vector inside the mapped view. Valid in float mode and in
        /// rescored int8 mode (section 1 in both); never in plain int8 mode. Scoring straight
        /// against this instead of copying the row out first is what took index construction
        /// from being dominated by <c>ReadArray</c> marshalling and pool traffic to being
        /// dominated by the arithmetic it is supposed to be doing.
        /// </summary>
        private unsafe float* VectorPointer(int index)
            => (float*)(DataBasePointer + (_floatVectorSectionOffset - HeaderSize)
                        + (long)index * _header.VectorDimension * sizeof(float));

        /// <summary>
        /// A stored vector viewed in place. Valid only while the write lock is held or the view
        /// is otherwise known not to be remapped, since growth replaces the mapping.
        /// </summary>
        private unsafe ReadOnlySpan<float> StoredVector(int index)
            => new(VectorPointer(index), _header.VectorDimension);

        private unsafe int* NeighborPointer(int nodeIndex, int level)
            => (int*)(DataBasePointer + NeighborPosition(nodeIndex, level));
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

                        if (!ConnectNewNode(index, vector, level, _insertScratch) || level > _header.EntryPointLevel)
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
            int[] neighbors = ArrayPool<int>.Shared.Rent(MaxNeighborsAnyLevel);
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
                // Return the index of the current entry point from the header
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

            if (_quantized && !_rescore)
            {
                Int8Quantizer.Dequantize(
                    new ReadOnlySpan<byte>(CodesPointer(index), dim),
                    ParamsAt(index),
                    destination.AsSpan(destinationOffset, dim));
                return;
            }

            long bytes = (long)dim * sizeof(float);
            fixed (float* destPtr = &destination[destinationOffset])
            {
                Buffer.MemoryCopy(VectorPointer(index), destPtr, bytes, bytes);
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

        /// <summary>
        /// Metric dispatch for the memory-mapped fast path, which scores straight against a raw
        /// pointer instead of copying the stored vector out first.
        /// </summary>
        private unsafe float SimilarityUnsafe(float[] query, float* stored, int dim) =>
            _header.DistanceFunction == DistanceFunction.Euclidean
                ? NegativeSquaredDistanceUnsafe(query, stored, dim)
                : DotProductUnsafe(query, stored, dim);

        internal static unsafe float NegativeSquaredDistanceUnsafe(float[] left, float* right, int dim)
        {
            return NegativeSquaredDistanceCore(
                ref MemoryMarshal.GetArrayDataReference(left),
                ref Unsafe.AsRef<float>(right),
                dim);
        }

        /// <summary>Dot product of a query array against a raw pointer into the mapping.</summary>
        internal static unsafe float DotProductUnsafe(float[] left, float* right, int dim)
        {
            return DotProductCore(
                ref MemoryMarshal.GetArrayDataReference(left),
                ref Unsafe.AsRef<float>(right),
                dim);
        }
        // Reads through the raw mapping pointer rather than the accessor: every accessor call
        // takes an interlocked ref on the shared SafeBuffer, and a top-100 result set makes a
        // few hundred of them per query, which serialised concurrent searches on that one
        // cache line (Cohere 1M, k = 100, 12 threads scaled 2× instead of 6×).
        private unsafe string GetMetadata(int index)
        {
            long descriptorPos = (_metadataSectionOffset - HeaderSize) + (long)index * MetadataDescriptorSize;
            byte* descriptor = DataBasePointer + descriptorPos;
            long heapOffset = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<long>(descriptor);
            int length = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<int>(descriptor + 8);

            if (length <= 0) return string.Empty;

            if (heapOffset < 0 || heapOffset + length > _header.MetadataHeapCapacity)
            {
                throw new QvecFormatException(
                    $"Row {index} has a corrupt metadata descriptor: offset={heapOffset}, length={length}, " +
                    $"heap capacity={_header.MetadataHeapCapacity}.");
            }

            return Encoding.UTF8.GetString(DataBasePointer + (_metadataHeapOffset - HeaderSize) + heapOffset, length);
        }

        private void WriteGuidToDisk(int index, Guid guid)
        {
            BeginWrite();
            long offset = (_guidSectionOffset - HeaderSize) + (long)index * GuidSize;
            byte[] bytes = guid.ToByteArray();
            _dataAccessor.WriteArray(offset, bytes, 0, GuidSize);
        }

        private unsafe Guid ReadGuidFromDisk(int index)
        {
            long offset = (_guidSectionOffset - HeaderSize) + (long)index * GuidSize;
            return new Guid(new ReadOnlySpan<byte>(DataBasePointer + offset, GuidSize));
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

        [Obsolete("SyncFrom copies rows without versions and cannot converge. Enable change tracking and use GetChanges/ApplyChanges (or Qvec.Sync). Removed in 3.0.")]
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
                RecordLocalDelete(id);
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
        /// made live entries unreachable from the entry point. Its surviving neighbours are
        /// therefore linked to each other before the node is unlinked.
        /// </summary>
        private void DisconnectNode(int deletedIndex)
        {
            int[] neighbors = ArrayPool<int>.Shared.Rent(MaxNeighborsAnyLevel);
            try
            {
                for (int level = 0; level < _header.MaxLayers; level++)
                {
                    GetNeighborsAtLevel(deletedIndex, level, neighbors);

                    int count = 0;
                    while (count < NeighborsAtLevel(level) && neighbors[count] != -1) count++;
                    if (count == 0)
                    {
                        InitNeighborsAtLevel(deletedIndex, level);
                        continue;
                    }

                    int[] live = new int[count];
                    Array.Copy(neighbors, live, count);

                    for (int j = 0; j < count; j++)
                        RemoveNeighborReference(live[j], level, deletedIndex);

                    BridgeSurvivors(live, level);
                    InitNeighborsAtLevel(deletedIndex, level);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighbors);
            }
        }

        /// <summary>
        /// Re-links the survivors of a deleted node so the neighbourhood cannot fall apart.
        /// They are joined into a cycle rather than cross-linked pairwise: a cycle already
        /// guarantees the survivor set stays connected, and it costs O(n) link attempts instead
        /// of O(n²). The quadratic version was affordable only while layer 0 held at most M
        /// neighbours; with M0 = 2 * M it made deleting a node roughly an order of magnitude
        /// more expensive, because each of the O(n²) attempts itself prunes over M0 candidates.
        /// Neighbour lists are stored in descending similarity order, so consecutive survivors
        /// are the most alike and the cycle links the pairs most worth linking.
        /// </summary>
        private void BridgeSurvivors(int[] live, int level)
        {
            var survivors = new List<int>(live.Length);
            foreach (int candidate in live)
            {
                if (!IsDeleted(candidate)) survivors.Add(candidate);
            }

            if (survivors.Count < 2) return;

            for (int j = 0; j < survivors.Count; j++)
            {
                int next = survivors[(j + 1) % survivors.Count];
                // Both directions, since a neighbour list is not symmetric.
                AddNeighborConnection(next, level, survivors[j]);
                AddNeighborConnection(survivors[j], level, next);
            }
        }

        private void RemoveNeighborReference(int nodeIndex, int level, int targetToRemove)
        {
            int slots = NeighborsAtLevel(level);
            int[] neighbors = ArrayPool<int>.Shared.Rent(slots);
            try
            {
                GetNeighborsAtLevel(nodeIndex, level, neighbors);

                // Compact out *every* occurrence. Returning after the first one left duplicate
                // references to a deleted node behind, which reintroduced tombstones into search.
                int write = 0;
                bool changed = false;
                for (int i = 0; i < slots; i++)
                {
                    int n = neighbors[i];
                    if (n == targetToRemove) { changed = true; continue; }
                    neighbors[write++] = n;
                }

                if (!changed) return;

                for (int i = write; i < slots; i++)
                    neighbors[i] = -1;

                WriteNeighborsAtLevel(nodeIndex, level, neighbors);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighbors);
            }
        }

        private unsafe void InitNeighborsAtLevel(int nodeIndex, int level)
        {
            BeginWrite();
            int slots = NeighborsAtLevel(level);
            new Span<int>(NeighborPointer(nodeIndex, level), slots).Fill(-1);
        }

        /// <summary>
        /// Derives a node's top layer from the graph. The level is not stored per node, so it
        /// is recovered as the highest layer on which the node has at least one neighbour.
        /// </summary>
        private int GetNodeLevel(int nodeIndex)
        {
            int[] buffer = ArrayPool<int>.Shared.Rent(MaxNeighborsAnyLevel);
            try
            {
                for (int level = _header.MaxLayers - 1; level > 0; level--)
                {
                    GetNeighborsAtLevel(nodeIndex, level, buffer);
                    for (int i = 0; i < NeighborsAtLevel(level); i++)
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
                RecordLocalUpsert(index, id);
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
                    RecordLocalUpsert(oldIndex, id);
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

        /// <param name="remoteVersion">
        /// When set, the row is stamped with this version (received from a peer) instead of a
        /// fresh local one.
        /// </param>
        private Guid AddEntryInternal(float[] vector, string metadata, Guid docId, EntryVersion? remoteVersion = null)
        {
            if (_guidIndex.ContainsKey(docId))
                return docId;

            int index = AllocateSlot();
            int level = RandomLayer();

            vector = PrepareVector(vector);

            WriteVectorToDisk(index, vector);
            WriteMetadataToDisk(index, metadata);
            WriteGuidToDisk(index, docId);
            if (remoteVersion is { } remote) RecordRemoteUpsert(index, docId, remote);
            else RecordLocalUpsert(index, docId);
            InitNeighborsOnDisk(index);

            _guidIndex[docId] = index;

            if (LiveCount == 1)
            {
                _header.EntryPoint = index;
                _header.EntryPointLevel = level;
            }
            else
            {
                if (!ConnectNewNode(index, vector, level, _insertScratch) || level > _header.EntryPointLevel)
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
            _topologyLock.Dispose();
        }
    }
}
