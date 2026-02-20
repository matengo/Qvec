using Qvec.Core;
using System.Diagnostics;

namespace Qvec.Console.Test
{
    public static class BenchmarkRunner
    {
        public static void RunCompare(QvecDatabase db, float[] queryVector, int iterations = 1000)
        {
            System.Console.WriteLine($"--- STARTAR BENCHMARK ({iterations} sökningar) ---");

            // 1. VÄRM UPP (Ladda filen i OS-cachen)
            db.Search(queryVector, topK: 1);
            db.SearchSimpleParallel(queryVector, topK: 1);

            //var parallelMs = RunParallelSearchTest(db, queryVector, iterations);
            var hnswMs = RunHNSWSearchTest(db, queryVector, iterations);


            // 4. RESULTAT
            //double speedup = parallelMs / hnswMs;
            //System.Console.WriteLine("------------------------------------------------");
            //System.Console.WriteLine($"HNSW är {speedup:F1}x snabbare än linjär sökning!");
            //System.Console.WriteLine("------------------------------------------------");

            System.Console.WriteLine("------------------------------------------------");
            System.Console.WriteLine($"TotalVectors: {db.GetCount()}");
            System.Console.WriteLine($"EntrypointIndex: {db.GetEntryPoint()}");
            System.Console.WriteLine($"FileSizeMb: {new FileInfo("benchmark.qvec").Length / 1024 / 1024}");
            System.Console.WriteLine($"Layers:");
            var stats = db.GetStats();
            foreach (var kvp in stats)
            {
                System.Console.WriteLine($"Layer {kvp.Key}: {kvp.Value} vectors");
            }


            System.Console.WriteLine("------------------------------------------------");

        }
        private static double RunHNSWSearchTest(QvecDatabase db, float[] queryVector, int iterations = 1000)
        {
            var sw = Stopwatch.StartNew();
            // 3. TESTA SEARCH HNSW (Graf-navigering)
            for (int i = 0; i < iterations; i++)
            {
                var _ = db.Search(queryVector, topK: 5);
            }
            sw.Stop();
            double hnswMs = sw.Elapsed.TotalMilliseconds / iterations;
            double totalSeconds = sw.Elapsed.TotalSeconds;
            double qps = iterations / totalSeconds;

            System.Console.WriteLine("--------------------------------------");
            System.Console.WriteLine($"Total tid: {sw.ElapsedMilliseconds} ms");
            System.Console.WriteLine($"Genomsnittlig tid per sökning: {sw.Elapsed.TotalMilliseconds / iterations:F4} ms");
            System.Console.WriteLine($"PRESTANDA: {qps:F0} QPS (Queries Per Second)");
            System.Console.WriteLine("--------------------------------------");
            System.Console.WriteLine($"SearchHNSW (Graf):      {hnswMs:F4} ms/sökning");
            return hnswMs;
        }
        private static double RunParallelSearchTest(QvecDatabase db, float[] queryVector, int iterations = 1000)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var _ = db.SearchSimpleParallel(queryVector, topK: 5);
            }
            sw.Stop();
            double parallelMs = sw.Elapsed.TotalMilliseconds / iterations;
            double totalSeconds = sw.Elapsed.TotalSeconds;
            double qps = iterations / totalSeconds;
            System.Console.WriteLine("--------------------------------------");
            System.Console.WriteLine($"Total tid: {sw.ElapsedMilliseconds} ms");
            System.Console.WriteLine($"Genomsnittlig tid per sökning: {sw.Elapsed.TotalMilliseconds / iterations:F4} ms");
            System.Console.WriteLine($"PRESTANDA: {qps:F0} QPS (Queries Per Second)");
            System.Console.WriteLine("--------------------------------------");
            System.Console.WriteLine($"SearchParallel (Linjär): {parallelMs:F4} ms/sökning");
            return parallelMs;
        }
        public static void RunRecallTest(QvecDatabase db, int testRounds = 100)
        {
            System.Console.WriteLine($"--- STARTAR RECALL-TEST ({testRounds} runder) ---");
            int hits = 0;
            var rand = new Random();
            int dim = 128; // Samma som din DB

            for (int i = 0; i < testRounds; i++)
            {
                // 1. Skapa en slumpmässig sökvektor
                float[] query = Enumerable.Range(0, dim).Select(_ => (float)rand.NextDouble()).ToArray();

                // 2. Hämta FACIT (Linjär sökning hittar ALLTID den absolut närmaste)
                var truth = db.SearchSimpleParallel(query, topK: 1).First();

                // 3. Hämta HNSW-resultat
                var approx = db.Search(query, topK: 1).FirstOrDefault();

                // 4. Kolla om de hittade samma dokument
                if (approx.Id == truth.Id)
                {
                    hits++;
                }
            }

            double recall = (double)hits / testRounds;
            System.Console.WriteLine("------------------------------------------------");
            System.Console.WriteLine($"RECALL: {recall:P1} ({hits} av {testRounds} rätt)");
            System.Console.WriteLine("------------------------------------------------");

            if (recall < 0.9)
            {
                System.Console.WriteLine("Tips: Om recall är låg, öka 'MaxNeighbors' eller implementera 'efSearch'.");
            }
        }
    }
}
