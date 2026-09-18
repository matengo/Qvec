using BenchmarkDotNet.Attributes;
using Qvec.Core;

namespace Qvec.MicroBenchmarks;

/// <summary>
/// One graph walk through the public API on a 10,000-node index of clustered vectors. The
/// arithmetic is a small part of this; what it exposes is everything around it — the
/// visited set, the heaps, the neighbour-list reads, the result materialisation — and,
/// through the memory diagnoser, how much of that is allocated per query.
/// </summary>
[MemoryDiagnoser]
public class SearchBenchmarks
{
    private const int N = 10_000;
    private const int Seed = 8080;

    [Params(128, 768)]
    public int Dim;

    [Params(VectorQuantization.None, VectorQuantization.Int8, VectorQuantization.Int8Rescored)]
    public VectorQuantization Quantization;

    private string _dir = "";
    private QvecDatabase? _db;
    private float[][] _queries = [];
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qvec-micro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var rng = new Random(Seed);
        var centres = new float[100][];
        for (int c = 0; c < centres.Length; c++) centres[c] = RandomUnit(Dim, rng);

        _db = new QvecDatabase(Path.Combine(_dir, "bench.qvec"), Dim, N, maxNeighbors: 32, maxLayers: 5,
            DistanceFunction.Euclidean, indexSeed: Seed, quantization: Quantization);

        var inserts = new QvecInsert[N];
        for (int i = 0; i < N; i++)
            inserts[i] = new QvecInsert(Clustered(centres[rng.Next(centres.Length)], rng), "{}");
        _db.AddEntries(inserts, maxDegreeOfParallelism: -1);

        _queries = new float[1_000][];
        for (int i = 0; i < _queries.Length; i++)
            _queries[i] = Clustered(centres[rng.Next(centres.Length)], rng);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Benchmark]
    public int Search_Top10_Ef100()
    {
        var q = _queries[_next++ % _queries.Length];
        return _db!.Search(q, topK: 10, efSearch: 100).Count;
    }

    private static float[] RandomUnit(int dim, Random rng)
    {
        var v = new float[dim];
        double norm = 0;
        for (int i = 0; i < dim; i++) { v[i] = (float)(rng.NextDouble() * 2 - 1); norm += v[i] * v[i]; }
        float inv = (float)(1 / Math.Sqrt(norm));
        for (int i = 0; i < dim; i++) v[i] *= inv;
        return v;
    }

    private static float[] Clustered(float[] centre, Random rng)
    {
        var v = new float[centre.Length];
        for (int i = 0; i < v.Length; i++) v[i] = centre[i] + (float)((rng.NextDouble() * 2 - 1) * 0.08);
        return v;
    }
}
