using Qvec.Core;
using System.Diagnostics;

namespace Qvec.Console.Test
{
    public static class BenchmarkRunner
    {
        public static void RunCompare(QvecDatabase db, float[] queryVector, int iterations = 1000)
        {
            System.Console.WriteLine($"--- STARTING BENCHMARK ({iterations} searches) ---");

            // 1. WARM UP (load the file into the OS cache)
            db.Search(queryVector, topK: 1);
            db.SearchSimpleParallel(queryVector, topK: 1);

            //var parallelMs = RunParallelSearchTest(db, queryVector, iterations);
            var hnswMs = RunHNSWSearchTest(db, queryVector, iterations);


            // 4. RESULTS
            //double speedup = parallelMs / hnswMs;
            //System.Console.WriteLine("------------------------------------------------");
            //System.Console.WriteLine($"HNSW is {speedup:F1}x faster than linear search!");
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
            // 3. TEST SEARCH HNSW (graph navigation)
            for (int i = 0; i < iterations; i++)
            {
                var _ = db.Search(queryVector, topK: 5);
            }
            sw.Stop();
            double hnswMs = sw.Elapsed.TotalMilliseconds / iterations;
            double totalSeconds = sw.Elapsed.TotalSeconds;
            double qps = iterations / totalSeconds;

            System.Console.WriteLine("--------------------------------------");
            System.Console.WriteLine($"Total time: {sw.ElapsedMilliseconds} ms");
            System.Console.WriteLine($"Average time per search: {sw.Elapsed.TotalMilliseconds / iterations:F4} ms");
            System.Console.WriteLine($"PERFORMANCE: {qps:F0} QPS (Queries Per Second)");
            System.Console.WriteLine("--------------------------------------");
            System.Console.WriteLine($"SearchHNSW (Graph):      {hnswMs:F4} ms/search");
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
            System.Console.WriteLine($"Total time: {sw.ElapsedMilliseconds} ms");
            System.Console.WriteLine($"Average time per search: {sw.Elapsed.TotalMilliseconds / iterations:F4} ms");
            System.Console.WriteLine($"PERFORMANCE: {qps:F0} QPS (Queries Per Second)");
            System.Console.WriteLine("--------------------------------------");
            System.Console.WriteLine($"SearchParallel (Linear): {parallelMs:F4} ms/search");
            return parallelMs;
        }
        public static void RunRecallTest(QvecDatabase db, int testRounds = 100)
        {
            System.Console.WriteLine($"--- STARTING RECALL TEST ({testRounds} rounds) ---");
            int hits = 0;
            var rand = new Random();
            int dim = 128; // Same as your DB

            for (int i = 0; i < testRounds; i++)
            {
                // 1. Create a random search vector
                float[] query = Enumerable.Range(0, dim).Select(_ => (float)rand.NextDouble()).ToArray();

                // 2. Get the ground truth (linear search ALWAYS finds the absolute nearest)
                var truth = db.SearchSimpleParallel(query, topK: 1).First();

                // 3. Get HNSW results
                var approx = db.Search(query, topK: 1).FirstOrDefault();

                // 4. Check whether they found the same document
                if (approx.Id == truth.Id)
                {
                    hits++;
                }
            }

            double recall = (double)hits / testRounds;
            System.Console.WriteLine("------------------------------------------------");
            System.Console.WriteLine($"RECALL: {recall:P1} ({hits} of {testRounds} correct)");
            System.Console.WriteLine("------------------------------------------------");

            if (recall < 0.9)
            {
                System.Console.WriteLine("Tip: If recall is low, increase 'MaxNeighbors' or implement 'efSearch'.");
            }
        }
    }
}
