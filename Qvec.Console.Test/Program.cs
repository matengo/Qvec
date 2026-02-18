using Qvec.Console.Test;
using QvecSharp;
using System.Diagnostics;

const int Dim = 128;        // Dimensioner (t.ex. för en mindre modell)
const int Count = 10000;    // Antal vektorer i databasen
const int SearchRounds = 1000; // Hur många sökningar vi ska mäta
string dbPath = "benchmark.qvec";

if (File.Exists(dbPath)) File.Delete(dbPath);

using var db = new QvecDatabase(dbPath, dim: Dim, max: Count);
var rand = new Random();

// --- 1. POPULERING (Bulk Import) ---
Console.WriteLine($"Populerar {Count} vektorer...");
var timer = Stopwatch.StartNew();

for (int i = 0; i < Count; i++)
{
    float[] v = Enumerable.Range(0, Dim).Select(_ => (float)rand.NextDouble()).ToArray();
    string meta = $"{{\"id\":{i}, \"tag\":\"test\"}}";
    db.AddEntry(v, meta);
}
timer.Stop();
Console.WriteLine($"Populering klar på: {timer.ElapsedMilliseconds} ms");

// --- 2. BENCHMARK (Sökning) ---
float[] queryVector = Enumerable.Range(0, Dim).Select(_ => (float)rand.NextDouble()).ToArray();

Console.WriteLine($"Startar benchmark: {SearchRounds} sökningar...");
timer.Restart();

BenchmarkRunner.RunCompare(db, queryVector, SearchRounds);
BenchmarkRunner.RunRecallTest(db);

//for (int i = 0; i < SearchRounds; i++)
//{
//    // Vi söker efter topp 5 närmaste
//    var results = db.SearchHNSW(queryVector, topK: 5);
//}

timer.Stop();

//Hybrid search med metadata-filter
//var results = db.SearchHybridHNSW(myQuery, meta => {
//    // Exempel: Metadata är JSON
//    return meta.Contains("\"InStock\":true") && meta.Contains("\"Price\":<500");
//}, topK: 10);




// --- 3. RESULTAT ---
//double totalSeconds = timer.Elapsed.TotalSeconds;
//double qps = SearchRounds / totalSeconds;

//Console.WriteLine("--------------------------------------");
//Console.WriteLine($"Total tid: {timer.ElapsedMilliseconds} ms");
//Console.WriteLine($"Genomsnittlig tid per sökning: {timer.Elapsed.TotalMilliseconds / SearchRounds:F4} ms");
//Console.WriteLine($"PRESTANDA: {qps:F0} QPS (Queries Per Second)");
//Console.WriteLine("--------------------------------------");

// Verifiera att AOT fungerar
Console.WriteLine("Tryck på valfri tangent för att avsluta...");
Console.ReadKey();