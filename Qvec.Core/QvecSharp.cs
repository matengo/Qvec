using System;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace QvecSharp
{
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
    }

    public class VectorDatabase : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _headerAccessor;
        private readonly MemoryMappedViewAccessor _dataAccessor;
        private readonly ReaderWriterLockSlim _lock = new();

        private DbHeader _header;
        private const int HeaderSize = 1024;
        private const int MetadataSize = 512;
        private readonly long _vectorSectionOffset;
        private readonly long _graphSectionOffset;
        private readonly long _metadataSectionOffset;


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
        public VectorDatabase(string path, int dim = 1536, int max = 1000, int maxNeighbors = 32)
        {
            bool exists = File.Exists(path);
            _vectorSectionOffset = HeaderSize;
            long vectorSpace = (long)max * dim * sizeof(float);

            _graphSectionOffset = _vectorSectionOffset + vectorSpace;
            long graphSpace = (long)max * maxNeighbors * sizeof(int);

            _metadataSectionOffset = _graphSectionOffset + graphSpace;
            long metadataSpace = (long)max * MetadataSize;

            long totalSize = _metadataSectionOffset + metadataSpace;

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
                    Version = 1,
                    VectorDimension = dim,
                    MaxCount = max,
                    CurrentCount = 0,
                    MaxNeighbors = maxNeighbors
                };
                _headerAccessor.Write(0, ref _header);
            }

            _dataAccessor = _mmf.CreateViewAccessor(HeaderSize, totalSize - HeaderSize);
        }

        // --- KÄRNA: SIMD MATEMATIK ---
        public static float DotProduct(float[] left, float[] right)
        {
            int i = 0;
            float dot = 0;
            int vectorSize = Vector<float>.Count;

            for (; i <= left.Length - vectorSize; i += vectorSize)
            {
                dot += Vector.Dot(new Vector<float>(left, i), new Vector<float>(right, i));
            }
            for (; i < left.Length; i++) dot += left[i] * right[i];
            return dot;
        }

        // --- SKRIVNING ---
        public void AddEntry(float[] vector, string metadata)
        {
            _lock.EnterWriteLock();
            try
            {
                if (_header.CurrentCount >= _header.MaxCount) throw new Exception("DB Full");

                int index = _header.CurrentCount;

                // 1. Bestäm vilket lager denna nod ska tillhöra
                int level = RandomLayer();

                // 2. Skriv Vektor & Metadata (som tidigare)
                WriteVectorToDisk(index, vector);
                WriteMetadataToDisk(index, metadata);

                // 3. Initiera Grannar för ALLA lager upp till MaxLayers
                InitNeighborsOnDisk(index);

                // 4. Uppdatera räknaren
                _header.CurrentCount++;

                // 5. VIKTIGT: Kolla om detta är vår nya högsta punkt
                UpdateEntryPointIfHigher(index, level);

                // Spara resten av headern
                _headerAccessor.Write(0, ref _header);
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

            // Säkerställ att vi inte skriver för mycket
            int length = Math.Min(bytes.Length, MetadataSize);

            // Skriv byten
            _dataAccessor.WriteArray(offset - HeaderSize, bytes, 0, length);

            // Om metadatan är kortare än Max, fyll resten med nollor (padding)
            if (length < MetadataSize)
            {
                byte[] padding = new byte[MetadataSize - length];
                _dataAccessor.WriteArray(offset - HeaderSize + length, padding, 0, padding.Length);
            }
        }
        private void InitNeighborsOnDisk(int index)
        {
            // Varje nod har plats för MaxNeighbors i varje lager
            int totalNeighborSlots = _header.MaxLayers * _header.MaxNeighbors;
            int[] emptySlots = new int[totalNeighborSlots];
            Array.Fill(emptySlots, -1); // -1 betyder "ingen granne här"

            long offset = _graphSectionOffset + (long)index * totalNeighborSlots * sizeof(int);

            _dataAccessor.WriteArray(offset - HeaderSize, emptySlots, 0, totalNeighborSlots);
        }
        /// <summary>
        /// SimpleSearch är en grundläggande linjär sökning som inte utnyttjar HNSW-graf
        /// </summary>
        /// <param name="query"></param>
        /// <param name="topK"></param>
        /// <param name="filter"></param>
        /// <returns></returns>
        public List<(int Id, float Score, string Metadata)> SearchSimple(float[] query, int topK, Func<string, bool> filter = null)
        {
            _lock.EnterReadLock();
            try
            {
                var candidates = new List<(int Id, float Score)>();
                // Enkel linjär sökning som fallback för demo, 
                // men du kan enkelt byta till den NSW-loop vi byggde.
                for (int i = 0; i < _header.CurrentCount; i++)
                {
                    string meta = GetMetadata(i);
                    if (filter != null && !filter(meta)) continue;

                    float[] v = new float[_header.VectorDimension];
                    _dataAccessor.ReadArray((long)i * _header.VectorDimension * sizeof(float), v, 0, _header.VectorDimension);

                    float score = DotProduct(query, v);
                    candidates.Add((i, score));
                }

                return candidates.OrderByDescending(c => c.Score)
                                 .Take(topK)
                                 .Select(c => (c.Id, c.Score, GetMetadata(c.Id)))
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
        public unsafe List<(int Id, float Score, string Metadata)> SearchSimpleParallel(float[] query, int topK, Func<string, bool> filter = null)
        {
            int count = _header.CurrentCount;
            int dim = _header.VectorDimension;

            // Vi skapar en trådsäker behållare för resultaten
            // ConcurrentBag är bra, men för topp-K är en lokal array per tråd snabbare
            var partialResults = new (int Id, float Score)[count];

            // Hämta en rå pekare till början av vektordatan
            byte* basePtr = null;
            _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

            try
            {
                float* vectorBasePtr = (float*)basePtr; // Vektorer börjar direkt efter headern i vår förenklade modell

                // PARALLELL LOOP: Utnyttjar alla CPU-kärnor
                Parallel.For(0, count, i =>
                {
                    string meta = GetMetadata(i);
                    if (filter != null && !filter(meta))
                    {
                        partialResults[i] = (i, float.MinValue);
                        return;
                    }

                    // Skapa en Span direkt från minnesadressen (ingen kopiering!)
                    float* currentVecPtr = vectorBasePtr + (i * dim);

                    // SIMD DotProduct direkt mot pekaren
                    float score = DotProductUnsafe(query, currentVecPtr, dim);
                    partialResults[i] = (i, score);
                });
            }
            finally
            {
                _dataAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            // Sortera och returnera topp-K
            return partialResults
                .Where(r => r.Score > float.MinValue)
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .Select(r => (r.Id, r.Score, GetMetadata(r.Id)))
                .ToList();
        }
        /// <summary>
        /// HybridHNSW är en sökmetod som kombinerar den snabba HNSW-navigeringen med möjligheten att filtrera på metadata under sökprocessen.
        /// </summary>
        /// <param name="query"></param>
        /// <param name="filter"></param>
        /// <param name="topK"></param>
        /// <returns></returns>
        public List<(int Id, float Score, string Metadata)> Search(float[] query, Func<string, bool> filter, int topK = 5)
        {
            _lock.EnterReadLock();
            try
            {
                // 1. Starta från EntryPoint
                int currentElement = _header.EntryPoint;

                // Kontrollera om EntryPoint matchar filtret, annars sök linjärt efter en giltig startnod
                if (!filter(GetMetadata(currentElement)))
                {
                    currentElement = FindFirstValidNode(filter);
                    if (currentElement == -1) return new List<(int, float, string)>(); // Inget matchar filtret i hela DB
                }

                float currentScore = CalculateScore(query, currentElement);

                // 2. Navigera i lagren med filter-krav
                for (int level = _header.MaxLayers - 1; level >= 0; level--)
                {
                    bool changed = true;
                    while (changed)
                    {
                        changed = false;
                        var neighbors = GetNeighborsAtLevel(currentElement, level);
                        foreach (int neighbor in neighbors)
                        {
                            if (neighbor == -1) break;

                            // HYBRID-STEGET: Kolla metadata innan vi ens bryr oss om score
                            string meta = GetMetadata(neighbor);
                            if (!filter(meta)) continue;

                            float score = CalculateScore(query, neighbor);
                            if (score > currentScore)
                            {
                                currentScore = score;
                                currentElement = neighbor;
                                changed = true;
                            }
                        }
                    }
                }

                // 3. Samla resultat (Top K)
                var finalResults = new List<(int Id, float Score)>();
                finalResults.Add((currentElement, currentScore));

                // Fyll på med grannar som också matchar filtret
                var baseNeighbors = GetNeighborsAtLevel(currentElement, 0);
                foreach (var n in baseNeighbors)
                {
                    if (n == -1) break;
                    string m = GetMetadata(n);
                    if (filter(m))
                        finalResults.Add((n, CalculateScore(query, n)));
                }

                return finalResults
                    .OrderByDescending(r => r.Score)
                    .Take(topK)
                    .Select(r => (r.Id, r.Score, GetMetadata(r.Id)))
                    .ToList();
            }
            finally { _lock.ExitReadLock(); }
        }

        // Hjälpmetod för att hitta en startpunkt om EntryPoint inte matchar filtret
        private int FindFirstValidNode(Func<string, bool> filter)
        {
            for (int i = 0; i < _header.CurrentCount; i++)
            {
                if (filter(GetMetadata(i))) return i;
            }
            return -1;
        }
        /// <summary>
        /// HNSW search som navigerar genom lagren och returnerar en lista med de bästa matchningarna, inklusive metadata.
        /// </summary>
        /// <param name="query"></param>
        /// <param name="topK"></param>
        /// <returns></returns>
        public List<(int Id, float Score, string Metadata)> Search(float[] query, int topK = 5)
        {
            _lock.EnterReadLock();
            try
            {
                // 1. Hitta den närmaste noden via lagren (HNSW-navigering)
                int bestIndex = NavigateLayers(query);

                // 2. För att returnera en lista (likt SearchParallel) hämtar vi 
                // grannarna till den bästa träffen i bottenlagret för att fylla TopK
                var neighbors = GetNeighborsAtLevel(bestIndex, 0).ToArray();

                var results = new List<(int Id, float Score)>();
                results.Add((bestIndex, CalculateScore(query, bestIndex)));

                foreach (var n in neighbors.Where(id => id != -1).Take(topK - 1))
                {
                    results.Add((n, CalculateScore(query, n)));
                }

                // 3. Formatera resultatet med metadata
                return results
                    .OrderByDescending(r => r.Score)
                    .Select(r => (r.Id, r.Score, GetMetadata(r.Id)))
                    .ToList();
            }
            finally { _lock.ExitReadLock(); }
        }

        // Privat hjälpmetod för själva navigeringen
        private int NavigateLayers(float[] query)
        {
            int currentElement = _header.EntryPoint;
            float currentScore = CalculateScore(query, currentElement);

            for (int level = _header.MaxLayers - 1; level >= 0; level--)
            {
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    var neighbors = GetNeighborsAtLevel(currentElement, level);
                    foreach (int neighbor in neighbors)
                    {
                        if (neighbor == -1) break;
                        float score = CalculateScore(query, neighbor);
                        if (score > currentScore)
                        {
                            currentScore = score;
                            currentElement = neighbor;
                            changed = true;
                        }
                    }
                }
            }
            return currentElement;
        }
        private void UpdateEntryPointIfHigher(int newIndex, int newLevel)
        {
            // Vi läser nuvarande EntryPoint nivå (om vi hade sparat nivåer, 
            // men för enkelhetens skull kollar vi om den nya noden är i ett 
            // högre lager än vad vi tror är MaxLayers/2 eller om det är första noden)

            // En enkel men effektiv regel: Om det är första noden, eller om 
            // nivån är högre än 0, utvärdera om den ska bli ny startpunkt.
            if (_header.CurrentCount == 1 || newLevel > 0)
            {
                // I en full HNSW-implementation skulle vi här jämföra newLevel 
                // mot nivån för den nuvarande EntryPoint.
                // För din version: Om newLevel är den högsta vi sett hittills, uppdatera.

                // Just nu sätter vi den helt enkelt som EntryPoint om det är den 
                // första noden, eller om den når ett respektabelt lager.
                if (_header.CurrentCount == 1 || newLevel >= (_header.MaxLayers / 2))
                {
                    _header.EntryPoint = newIndex;
                    // Vi skriver inte headern här, det görs i slutet av AddEntry
                }
            }
        }

        private unsafe Span<int> GetNeighborsAtLevel(int nodeIndex, int level)
        {
            // Layout på disk: [Node 0: L0, L1, L2...][Node 1: L0, L1, L2...]
            // Varje lager har 'MaxNeighbors' platser.
            long position = _graphSectionOffset +
                            (long)nodeIndex * _header.MaxLayers * _header.MaxNeighbors * sizeof(int) +
                            (long)level * _header.MaxNeighbors * sizeof(int);

            byte* ptr = null;
            _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            // Returnera en Span så vi kan läsa/skriva direkt i minnet utan kopiering
            return new Span<int>(ptr + position, _header.MaxNeighbors);
        }

        private static readonly ThreadLocal<Random> _random = new(() => new Random());

        private int RandomLayer()
        {
            double mL = 0.36;
            // .Value behövs eftersom det är en ThreadLocal
            double r = _random.Value.NextDouble();
            if (r <= 0) r = 0.0001;

            int level = (int)(-Math.Log(r) * mL);
            return Math.Min(level, _header.MaxLayers - 1);
        }
        private float CalculateScore(float[] query, int targetIndex)
        {
            // Beräkna offset i filen för målvektorn
            long offset = _vectorSectionOffset + (long)targetIndex * _header.VectorDimension * sizeof(float);

            // Vi läser in vektorn till en temporär buffert (stackalloc är snabbast för AOT)
            Span<float> targetVector = stackalloc float[_header.VectorDimension];

            // Läs direkt från MemoryMappedFile-accessorn
            _dataAccessor.ReadArray(offset - HeaderSize, targetVector.ToArray(), 0, _header.VectorDimension);

            // Anropa vår SIMD-optimerade DotProduct
            return DotProduct(query, targetVector.ToArray());
        }
        public void RebuildIndex()
        {
            Console.WriteLine("Optimerar HNSW-grafen...");

            // Vi kör detta parallellt för att utnyttja alla kärnor
            Parallel.For(0, _header.CurrentCount, i =>
            {
                // 1. Hämta vektorn för aktuell nod
                float[] v = GetVector(i);

                // 2. Gör en djup sökning (efSearch) för att hitta bättre grannar
                var bestNeighbors = SearchInternal(v, k: _header.MaxNeighbors);

                // 3. Uppdatera lagren på disk
                UpdateNeighbors(i, bestNeighbors.Select(n => n.Id).ToArray());
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
        public Dictionary<int, int> GetStats()
        {
            var stats = new Dictionary<int, int>();
            for (int l = 0; l < _header.MaxLayers; l++) stats[l] = 0;

            _lock.EnterReadLock();
            try
            {
                for (int i = 0; i < _header.CurrentCount; i++)
                {
                    // Vi kollar varje lager för varje nod
                    for (int level = 0; level < _header.MaxLayers; level++)
                    {
                        var neighbors = GetNeighborsAtLevel(i, level);
                        // Om första grannen inte är -1, existerar noden i detta lager
                        if (neighbors.Length > 0 && neighbors[0] != -1)
                        {
                            stats[level]++;
                        }
                        else if (level == 0 && _header.CurrentCount > 0)
                        {
                            // Bottenlagret räknar vi alltid om det finns data
                            stats[0]++;
                            break;
                        }
                    }
                }
            }
            finally { _lock.ExitReadLock(); }

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
            float[] vector = new float[_header.VectorDimension];
            long offset = _vectorSectionOffset + (long)index * _header.VectorDimension * sizeof(float);
            _dataAccessor.ReadArray(offset - HeaderSize, vector, 0, _header.VectorDimension);
            return vector;
        }

        // SearchInternal används vid indexering för att hitta de k-närmaste grannarna
        private List<(int Id, float Score)> SearchInternal(float[] query, int k)
        {
            // Här använder vi vår tidigare optimerade sökloop (NSW/HNSW)
            // men vi returnerar de K bästa träffarna istället för bara en.
            var results = SearchSimpleParallel(query, k);
            return results.Select(r => (r.Id, r.Score)).ToList();
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
            byte[] buffer = new byte[MetadataSize];
            long pos = (_metadataSectionOffset - HeaderSize) + (long)index * MetadataSize;
            _dataAccessor.ReadArray(pos, buffer, 0, MetadataSize);
            return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
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