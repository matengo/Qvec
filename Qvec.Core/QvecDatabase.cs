using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Qvec.Core
{
    public enum DistanceFunction : int
    {
        DotProduct = 0,
        Cosine = 1
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DbHeader
    {
        public int MaxLayers; // Ofta 4-5 lager räcker för miljoner vektorer
        public double LayerProbability; // Bestämmer hur noder fördelas mellan lager

        public int MagicNumber;      // 0x5A564543 ("ZVEC")
        public int Version;
        public int VectorDimension;
        public int CurrentCount;
        public int MaxCount;
        public int MaxNeighbors;
        public int EntryPoint;       // <--- Här sparar vi index för "topp-noden"
        public int EntryPointLevel;  // Lagret som EntryPoint tillhör
        public int DeletedCount;     // Antal soft-deleted dokument
        public DistanceFunction DistanceFunction;
    }

    public class QvecDatabase : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _headerAccessor;
        private readonly MemoryMappedViewAccessor _dataAccessor;
        private readonly ReaderWriterLockSlim _lock = new();

        private DbHeader _header;
        private const int HeaderSize = 1024;
        private const int MetadataSize = 512;
        private const int GuidSize = 16;
        private readonly long _vectorSectionOffset;
        private readonly long _graphSectionOffset;
        private readonly long _metadataSectionOffset;
        private readonly long _guidSectionOffset;
        private readonly long _tombstoneSectionOffset;
        private readonly Dictionary<Guid, int> _guidIndex = new();
        private readonly HashSet<int> _deletedIndices = new();
        private readonly int[] _cachedEmptyNeighbors;
        private readonly byte[] _cachedPadding;


        
        public QvecDatabase(string path, int dim = 1536, int max = 1000, int maxNeighbors = 32, int maxLayers = 5, DistanceFunction distanceFunction = DistanceFunction.DotProduct)
        {
            bool exists = File.Exists(path);
            _vectorSectionOffset = HeaderSize;
            long vectorSpace = (long)max * dim * sizeof(float);

            _graphSectionOffset = _vectorSectionOffset + vectorSpace;
            long graphSpace = (long)max * maxLayers * maxNeighbors * sizeof(int);

            _metadataSectionOffset = _graphSectionOffset + graphSpace;
            long metadataSpace = (long)max * MetadataSize;

            _guidSectionOffset = _metadataSectionOffset + metadataSpace;
            long guidSpace = (long)max * GuidSize;

            _tombstoneSectionOffset = _guidSectionOffset + guidSpace;
            long tombstoneSpace = (long)max;

            long totalSize = _tombstoneSectionOffset + tombstoneSpace;

            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, null, totalSize);
            _headerAccessor = _mmf.CreateViewAccessor(0, HeaderSize);

            if (exists)
            {
                _headerAccessor.Read(0, out _header);
            }
            else
            {
                _header = new DbHeader
                {
                    MagicNumber = 0x5A564543,
                    Version = 3,
                    VectorDimension = dim,
                    MaxCount = max,
                    CurrentCount = 0,
                    MaxNeighbors = maxNeighbors,
                    MaxLayers = maxLayers,
                    LayerProbability = 1.0 / Math.Log(maxNeighbors),
                    EntryPoint = 0,
                    EntryPointLevel = 0,
                    DeletedCount = 0,
                    DistanceFunction = distanceFunction
                };
                _headerAccessor.Write(0, ref _header);
            }

            _dataAccessor = _mmf.CreateViewAccessor(HeaderSize, totalSize - HeaderSize);

            int totalSlots = _header.MaxLayers * _header.MaxNeighbors;
            _cachedEmptyNeighbors = new int[totalSlots];
            Array.Fill(_cachedEmptyNeighbors, -1);
            _cachedPadding = new byte[MetadataSize];

            if (exists && _header.Version < 2)
            {
                MigrateToV2();
            }
            else if (exists)
            {
                RebuildGuidIndex();
            }
            if (exists)
            {
                LoadTombstones();
            }
        }

        // --- KÄRNA: SIMD MATEMATIK ---
        public static float DotProduct(float[] left, float[] right)
        {
            return DotProduct(left, right, left.Length);
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
        public Guid AddEntry(float[] vector, string metadata, Guid? externalId = null)
        {
            _lock.EnterWriteLock();
            try
            {
                if (_header.CurrentCount >= _header.MaxCount) throw new Exception("DB Full");

                Guid docId = externalId ?? Guid.NewGuid();

                if (_guidIndex.ContainsKey(docId))
                    return docId;

                int index = _header.CurrentCount;
                int level = RandomLayer();

                if (_header.DistanceFunction == DistanceFunction.Cosine)
                    NormalizeVector(vector);

                WriteVectorToDisk(index, vector);
                WriteMetadataToDisk(index, metadata);
                WriteGuidToDisk(index, docId);
                InitNeighborsOnDisk(index);

                _guidIndex[docId] = index;
                _header.CurrentCount++;

                if (_header.CurrentCount == 1)
                {
                    _header.EntryPoint = index;
                    _header.EntryPointLevel = level;
                }
                else
                {
                    ConnectNewNode(index, vector, level);
                    if (level > _header.EntryPointLevel)
                    {
                        _header.EntryPoint = index;
                        _header.EntryPointLevel = level;
                    }
                }

                _headerAccessor.Write(0, ref _header);
                return docId;
            }
            finally { _lock.ExitWriteLock(); }
        }
        private void WriteVectorToDisk(int index, float[] vector)
        {
            // Beräkna offset: Hoppa över Header, sedan alla tidigare vektorer
            long offset = _vectorSectionOffset + (long)index * _header.VectorDimension * sizeof(float);

            // Vi skriver arrayen direkt till den mappade vyn
            // offset - HeaderSize eftersom _dataAccessor startar efter headern
            _dataAccessor.WriteArray(offset - HeaderSize, vector, 0, _header.VectorDimension);
        }
        private void WriteMetadataToDisk(int index, string metadata)
        {
            long offset = _metadataSectionOffset + (long)index * MetadataSize;
            byte[] bytes = Encoding.UTF8.GetBytes(metadata);

            int length = Math.Min(bytes.Length, MetadataSize);
            _dataAccessor.WriteArray(offset - HeaderSize, bytes, 0, length);

            if (length < MetadataSize)
            {
                _dataAccessor.WriteArray(offset - HeaderSize + length, _cachedPadding, 0, MetadataSize - length);
            }
        }
        private void InitNeighborsOnDisk(int index)
        {
            long offset = _graphSectionOffset + (long)index * _cachedEmptyNeighbors.Length * sizeof(int);
            _dataAccessor.WriteArray(offset - HeaderSize, _cachedEmptyNeighbors, 0, _cachedEmptyNeighbors.Length);
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
        public List<(Guid Id, float Score, string Metadata)> SearchSimple(float[] query, int topK, Func<string, bool> filter = null)
        {
            _lock.EnterReadLock();
            try
            {
                if (_header.DistanceFunction == DistanceFunction.Cosine)
                    NormalizeVector(query);

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
        public unsafe List<(Guid Id, float Score, string Metadata)> SearchSimpleParallel(float[] query, int topK, Func<string, bool> filter = null)
        {
            if (_header.DistanceFunction == DistanceFunction.Cosine)
                NormalizeVector(query);

            int count = _header.CurrentCount;
            int dim = _header.VectorDimension;

            var partialResults = new (int Index, float Score)[count];

            byte* basePtr = null;
            _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

            try
            {
                basePtr += _dataAccessor.PointerOffset;
                float* vectorBasePtr = (float*)basePtr;

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
        public List<(Guid Id, float Score, string Metadata)> Search(float[] query, Func<string, bool> filter, int topK = 5, int efSearch = 50)
        {
            _lock.EnterReadLock();
            try
            {
                if (_header.DistanceFunction == DistanceFunction.Cosine)
                    NormalizeVector(query);

                int entryPoint = _header.EntryPoint;
                for (int level = _header.EntryPointLevel; level >= 1; level--)
                {
                    entryPoint = GreedyClosest(query, entryPoint, level);
                }

                int ef = Math.Max(topK, efSearch);
                var nearest = SearchLayerNearest(query, entryPoint, 0, ef);

            return nearest
                    .Select(r => (r.Id, r.Score, Meta: GetMetadata(r.Id)))
                    .Where(r => !_deletedIndices.Contains(r.Id) && filter(r.Meta))
                    .OrderByDescending(r => r.Score)
                    .Take(topK)
                    .Select(r => (ReadGuidFromDisk(r.Id), r.Score, r.Meta))
                    .ToList();
            }
            finally { _lock.ExitReadLock(); }
        }

        // Hjälpmetod för att hitta en startpunkt om EntryPoint inte matchar filtret
        private int FindFirstValidNode(Func<string, bool> filter)
        {
            for (int i = 0; i < _header.CurrentCount; i++)
            {
                if (_deletedIndices.Contains(i)) continue;
                if (filter(GetMetadata(i))) return i;
            }
            return -1;
        }
        /// <summary>
        /// HNSW search som navigerar genom lagren och returnerar en lista med de bästa matchningarna, inklusive metadata.
        /// </summary>
        /// <param name="query"></param>
        /// <param name="topK"></param>
        /// <param name="efSearch">Sökbredd i bottenlagret. Högre = bättre recall men långsammare. Standard: 200.</param>
        /// <returns></returns>
        public List<(Guid Id, float Score, string Metadata)> Search(float[] query, int topK = 5, int efSearch = 50)
        {
            _lock.EnterReadLock();
            try
            {
                if (_header.DistanceFunction == DistanceFunction.Cosine)
                    NormalizeVector(query);

                int entryPoint = _header.EntryPoint;
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

        // Privat hjälpmetod för själva navigeringen
        private int NavigateLayers(float[] query)
        {
            int currentElement = _header.EntryPoint;
            float currentScore = CalculateScore(query, currentElement);
            int[] neighbors = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                for (int level = _header.EntryPointLevel; level >= 0; level--)
                {
                    bool changed = true;
                    while (changed)
                    {
                        changed = false;
                        GetNeighborsAtLevel(currentElement, level, neighbors);
                        for (int j = 0; j < _header.MaxNeighbors; j++)
                        {
                            if (neighbors[j] == -1) break;
                            float score = CalculateScore(query, neighbors[j]);
                            if (score > currentScore)
                            {
                                currentScore = score;
                                currentElement = neighbors[j];
                                changed = true;
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighbors);
            }
            return currentElement;
        }
        private void ConnectNewNode(int newIndex, float[] newVector, int newLevel)
        {
            int currentElement = _header.EntryPoint;

            for (int level = _header.EntryPointLevel; level > newLevel; level--)
            {
                currentElement = GreedyClosest(newVector, currentElement, level);
            }

            for (int level = Math.Min(newLevel, _header.MaxLayers - 1); level >= 0; level--)
            {
                var nearest = SearchLayerNearest(newVector, currentElement, level, _header.MaxNeighbors);

                WriteNeighborsAtLevel(newIndex, level, nearest);

                foreach (var (neighborId, _) in nearest)
                {
                    if (neighborId < 0) break;
                    AddNeighborConnection(neighborId, level, newIndex, newVector);
                }

                if (nearest.Length > 0 && nearest[0].Id >= 0)
                {
                    currentElement = nearest[0].Id;
                }
            }
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

        private void WriteNeighborsAtLevel(int nodeIndex, int level, (int Id, float Score)[] neighbors)
        {
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
                }

            float[] existingVector = GetVector(existingNode);
                float newScore = DotProduct(existingVector, newVector, _header.VectorDimension);

                int worstIdx = 0;
                float worstScore = CalculateScore(existingVector, neighbors[0]);
                for (int i = 1; i < _header.MaxNeighbors; i++)
                {
                    float score = CalculateScore(existingVector, neighbors[i]);
                    if (score < worstScore)
                    {
                        worstScore = score;
                        worstIdx = i;
                    }
                }

                ArrayPool<float>.Shared.Return(existingVector);

                if (newScore > worstScore)
                {
                    neighbors[worstIdx] = newNode;
                    WriteNeighborsAtLevel(existingNode, level, neighbors);
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
        public void RebuildIndex()
        {
            Console.WriteLine("Optimerar HNSW-grafen...");

            // Vi kör detta parallellt för att utnyttja alla kärnor
            Parallel.For(0, _header.CurrentCount, i =>
            {
                // 1. Hämta vektorn för aktuell nod
                float[] v = GetVector(i);
                try
                {
                    // 2. Gör en djup sökning (efSearch) för att hitta bättre grannar
                    var bestNeighbors = SearchInternal(v, k: _header.MaxNeighbors);

                    // 3. Uppdatera lagren på disk
                    UpdateNeighbors(i, bestNeighbors.Select(n => n.Id).ToArray());
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(v);
                }
            });

            Console.WriteLine("Indexering klar.");
        }
        public int GetCount()
        {
            _lock.EnterReadLock();
            try
            {
                // Vi läser direkt från header-accessorn för att få det mest 
                // aktuella värdet om en annan process skulle ha skrivit till filen.
                _headerAccessor.Read(0, out DbHeader currentHeader);
                return currentHeader.CurrentCount;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        public bool IsHealthy()
        {
            try
            {
                _headerAccessor.Read(0, out DbHeader currentHeader);
                // Kontrollera Magic Number (0x5A564543) och att dimensionen är giltig
                return currentHeader.MagicNumber == 0x5A564543 && currentHeader.VectorDimension > 0;
            }
            catch
            {
                return false;
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
            long offset = _vectorSectionOffset + (long)index * _header.VectorDimension * sizeof(float);
            _dataAccessor.ReadArray(offset - HeaderSize, vector, 0, _header.VectorDimension);
            return vector;
        }

        private List<(int Id, float Score)> SearchInternal(float[] query, int k)
        {
            var results = SearchSimpleParallel(query, k);
            return results.Select(r =>
            {
                _guidIndex.TryGetValue(r.Id, out int index);
                return (index, r.Score);
            }).ToList();
        }

        private void UpdateNeighbors(int nodeIndex, int[] neighbors)
        {
            // Vi skriver till Layer 0 (bottenlagret) som standard vid rebuild
            long position = (_graphSectionOffset - HeaderSize) + (long)nodeIndex * _header.MaxLayers * _header.MaxNeighbors * sizeof(int);
            _dataAccessor.WriteArray(position, neighbors, 0, Math.Min(neighbors.Length, _header.MaxNeighbors));
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
            byte[] buffer = ArrayPool<byte>.Shared.Rent(MetadataSize);
            try
            {
                long pos = (_metadataSectionOffset - HeaderSize) + (long)index * MetadataSize;
                _dataAccessor.ReadArray(pos, buffer, 0, MetadataSize);
                return Encoding.UTF8.GetString(buffer, 0, MetadataSize).TrimEnd('\0');
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void WriteGuidToDisk(int index, Guid guid)
        {
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

        private void MigrateToV2()
        {
            for (int i = 0; i < _header.CurrentCount; i++)
            {
                Guid id = Guid.NewGuid();
                WriteGuidToDisk(i, id);
                _guidIndex[id] = i;
            }
            _header.Version = 2;
            _headerAccessor.Write(0, ref _header);
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
                _headerAccessor.Write(0, ref _header);
                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        private void SoftDelete(int index)
        {
            WriteTombstone(index, 1);
            _deletedIndices.Add(index);
            DisconnectNode(index);

            if (_header.EntryPoint == index)
            {
                FindNewEntryPoint();
            }
        }

        private void DisconnectNode(int deletedIndex)
        {
            int[] neighbors = ArrayPool<int>.Shared.Rent(_header.MaxNeighbors);
            try
            {
                for (int level = 0; level < _header.MaxLayers; level++)
                {
                    GetNeighborsAtLevel(deletedIndex, level, neighbors);

                    for (int j = 0; j < _header.MaxNeighbors; j++)
                    {
                        if (neighbors[j] == -1) break;
                        RemoveNeighborReference(neighbors[j], level, deletedIndex);
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

                for (int i = 0; i < _header.MaxNeighbors; i++)
                {
                    if (neighbors[i] == targetToRemove)
                    {
                        for (int k = i; k < _header.MaxNeighbors - 1; k++)
                            neighbors[k] = neighbors[k + 1];
                        neighbors[_header.MaxNeighbors - 1] = -1;
                        WriteNeighborsAtLevel(nodeIndex, level, neighbors);
                        return;
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(neighbors);
            }
        }

        private void InitNeighborsAtLevel(int nodeIndex, int level)
        {
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

        private void FindNewEntryPoint()
        {
            for (int i = 0; i < _header.CurrentCount; i++)
            {
                if (!_deletedIndices.Contains(i))
                {
                    _header.EntryPoint = i;
                    _header.EntryPointLevel = 0;
                    return;
                }
            }
            _header.EntryPoint = 0;
            _header.EntryPointLevel = 0;
        }

        private void WriteTombstone(int index, byte value)
        {
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
            _lock.EnterWriteLock();
            try
            {
                if (!_guidIndex.TryGetValue(id, out int index))
                    return false;

                WriteMetadataToDisk(index, newMetadata);
                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public bool UpdateVector(Guid id, float[] newVector)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_guidIndex.TryGetValue(id, out int oldIndex))
                    return false;

                string metadata = GetMetadata(oldIndex);
                _guidIndex.Remove(id);
                SoftDelete(oldIndex);
                _header.DeletedCount++;

                AddEntryInternal(newVector, metadata, id);
                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public bool Update(Guid id, float[] newVector, string newMetadata)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_guidIndex.TryGetValue(id, out int oldIndex))
                    return false;

                if (newVector == null)
                {
                    WriteMetadataToDisk(oldIndex, newMetadata);
                    return true;
                }

                string metadata = newMetadata ?? GetMetadata(oldIndex);
                _guidIndex.Remove(id);
                SoftDelete(oldIndex);
                _header.DeletedCount++;

                AddEntryInternal(newVector, metadata, id);
                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        private Guid AddEntryInternal(float[] vector, string metadata, Guid docId)
        {
            if (_header.CurrentCount >= _header.MaxCount) throw new Exception("DB Full");

            if (_guidIndex.ContainsKey(docId))
                return docId;

            int index = _header.CurrentCount;
            int level = RandomLayer();

            if (_header.DistanceFunction == DistanceFunction.Cosine)
                NormalizeVector(vector);

            WriteVectorToDisk(index, vector);
            WriteMetadataToDisk(index, metadata);
            WriteGuidToDisk(index, docId);
            InitNeighborsOnDisk(index);

            _guidIndex[docId] = index;
            _header.CurrentCount++;

            if (_header.CurrentCount - _header.DeletedCount == 1)
            {
                _header.EntryPoint = index;
                _header.EntryPointLevel = level;
            }
            else
            {
                ConnectNewNode(index, vector, level);
                if (level > _header.EntryPointLevel)
                {
                    _header.EntryPoint = index;
                    _header.EntryPointLevel = level;
                }
            }

            _headerAccessor.Write(0, ref _header);
            return docId;
        }

        public void Dispose()
        {
            _headerAccessor.Dispose();
            _dataAccessor.Dispose();
            _mmf.Dispose();
            _lock.Dispose();
        }
    }
}