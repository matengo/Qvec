using Qvec.Console.Test;
using Qvec.Core;
using System.Diagnostics;

const int Dim = 128;        // Dimensions (e.g. for a smaller model)
const int Count = 1000000;    // Number of vectors in the database
const int SearchRounds = 1000; // How many searches to measure
string dbPath = "benchmark.qvec";

if (File.Exists(dbPath)) File.Delete(dbPath);

using var db = new QvecDatabase(dbPath, dim: Dim, max: Count);
var rand = new Random();

// --- 1. POPULATION (Bulk Import) ---
Console.WriteLine($"Populating {Count} vectors...");
var timer = Stopwatch.StartNew();

for (int i = 0; i < Count; i++)
{
    float[] v = Enumerable.Range(0, Dim).Select(_ => (float)rand.NextDouble()).ToArray();
    string meta = $"{{\"id\":{i}, \"tag\":\"test\"}}";
    db.AddEntry(v, meta);
}
timer.Stop();
Console.WriteLine($"Population completed in: {timer.ElapsedMilliseconds} ms");

// --- 2. BENCHMARK (Search) ---
float[] queryVector = Enumerable.Range(0, Dim).Select(_ => (float)rand.NextDouble()).ToArray();

Console.WriteLine($"Starting benchmark: {SearchRounds} searches...");
timer.Restart();

BenchmarkRunner.RunCompare(db, queryVector, SearchRounds);
BenchmarkRunner.RunRecallTest(db);

//for (int i = 0; i < SearchRounds; i++)
//{
//    // Search for the top 5 nearest
//    var results = db.SearchHNSW(queryVector, topK: 5);
//}

timer.Stop();

// Hybrid search with metadata filter
//var results = db.SearchHybridHNSW(myQuery, meta => {
//    // Example: Metadata is JSON
//    return meta.Contains("\"InStock\":true") && meta.Contains("\"Price\":<500");
//}, topK: 10);




// --- 3. RESULTS ---
//double totalSeconds = timer.Elapsed.TotalSeconds;
//double qps = SearchRounds / totalSeconds;

//Console.WriteLine("--------------------------------------");
//Console.WriteLine($"Total time: {timer.ElapsedMilliseconds} ms");
//Console.WriteLine($"Average time per search: {timer.Elapsed.TotalMilliseconds / SearchRounds:F4} ms");
//Console.WriteLine($"PERFORMANCE: {qps:F0} QPS (Queries Per Second)");
//Console.WriteLine("--------------------------------------");

// Verify that AOT works
Console.WriteLine("Press any key to exit...");
Console.ReadKey();