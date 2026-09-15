using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// HNSW assigns every node a random layer, and until now that draw came from
/// <see cref="Random.Shared"/>. That made index construction irreproducible: building the same
/// database twice from identical vectors in identical order produced two different graphs, so
/// two runs of the same "seeded" test could differ by several percentage points of recall.
///
/// That is worse than a flaky test. It means a bad recall result cannot be reproduced in order
/// to be debugged, and it means a benchmark number cannot be re-derived by anyone else. An
/// optional seed fixes both without changing the default, which stays randomized -- a fixed
/// layer pattern shipped to every user would be a poor default.
/// </summary>
public class DeterministicIndexTests
{
    private static float[][] Vectors(int n, int dim, int seed)
    {
        var rng = new Random(seed);
        var result = new float[n][];
        for (int i = 0; i < n; i++)
        {
            var v = new float[dim];
            for (int d = 0; d < dim; d++) v[d] = (float)(rng.NextDouble() * 2 - 1);
            result[i] = v;
        }
        return result;
    }

    private static List<Guid> BuildAndQuery(string path, float[][] vectors, float[] query, int? indexSeed)
    {
        using var db = new QvecDatabase(path, dim: vectors[0].Length, max: vectors.Length + 8,
            maxNeighbors: 8, maxLayers: 5, distanceFunction: DistanceFunction.Euclidean,
            indexSeed: indexSeed);

        foreach (var v in vectors) db.AddEntry(v, "{}");

        return db.Search(query, topK: 10).Select(r => r.Id).ToList();
    }

    [Fact]
    public void TwoBuildsWithTheSameIndexSeed_ProduceTheSameSearchResults()
    {
        using var dir = new TempDir();
        var vectors = Vectors(n: 600, dim: 32, seed: 11);
        var query = Vectors(n: 1, dim: 32, seed: 99)[0];

        var first = BuildAndQuery(dir.File("a.qvec"), vectors, query, indexSeed: 4242);
        var second = BuildAndQuery(dir.File("b.qvec"), vectors, query, indexSeed: 4242);

        // Guids are content-independent, so compare the ranked order of the vectors themselves
        // via their position in the insert sequence. Identical graphs must visit identically.
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(
            first.Select((_, i) => i).ToList(),
            second.Select((_, i) => i).ToList());
    }

    /// <summary>
    /// The stronger version of the claim: the graph itself, not just the result ordering,
    /// must be byte-identical. Comparing search output alone would pass even if the two graphs
    /// differed in ways this particular query never reached.
    /// </summary>
    [Fact]
    public void TwoBuildsWithTheSameIndexSeed_ProduceIdenticalGraphs()
    {
        using var dir = new TempDir();
        var vectors = Vectors(n: 400, dim: 16, seed: 21);

        string a = dir.File("a.qvec");
        string b = dir.File("b.qvec");

        foreach (string path in new[] { a, b })
        {
            using var db = new QvecDatabase(path, dim: 16, max: 512, maxNeighbors: 8,
                maxLayers: 5, distanceFunction: DistanceFunction.Euclidean, indexSeed: 777);
            foreach (var v in vectors) db.AddEntry(v, "{}");
        }

        Assert.Equal(GraphBytes(a), GraphBytes(b));
    }

    [Fact]
    public void DifferentIndexSeeds_ProduceDifferentGraphs()
    {
        using var dir = new TempDir();
        var vectors = Vectors(n: 400, dim: 16, seed: 21);

        string a = dir.File("a.qvec");
        string b = dir.File("b.qvec");

        int seed = 100;
        foreach (string path in new[] { a, b })
        {
            using var db = new QvecDatabase(path, dim: 16, max: 512, maxNeighbors: 8,
                maxLayers: 5, distanceFunction: DistanceFunction.Euclidean, indexSeed: seed++);
            foreach (var v in vectors) db.AddEntry(v, "{}");
        }

        // If this ever fails, the seed is not actually reaching the layer draw.
        Assert.NotEqual(GraphBytes(a), GraphBytes(b));
    }

    [Fact]
    public void WithoutAnIndexSeed_BuildsRemainRandomized()
    {
        using var dir = new TempDir();
        var vectors = Vectors(n: 800, dim: 16, seed: 31);

        string a = dir.File("a.qvec");
        string b = dir.File("b.qvec");

        foreach (string path in new[] { a, b })
        {
            using var db = new QvecDatabase(path, dim: 16, max: 1024, maxNeighbors: 8,
                maxLayers: 5, distanceFunction: DistanceFunction.Euclidean);
            foreach (var v in vectors) db.AddEntry(v, "{}");
        }

        // The default must stay randomized. With 800 nodes drawing layers independently the
        // chance of two unseeded builds matching exactly is negligible.
        Assert.NotEqual(GraphBytes(a), GraphBytes(b));
    }

    private static byte[] GraphBytes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var header = Qvec.Core.Format.V4Header.Read(
            bytes.AsSpan(0, Qvec.Core.Format.V4Header.HeaderSizeValue), bytes.LongLength);
        var graph = header.GetRequiredSection(Qvec.Core.Format.V4SectionIds.Graph);
        return bytes.AsSpan((int)graph.Offset, (int)graph.Length).ToArray();
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "qvec-det-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(_root);

        public string File(string name) => Path.Combine(_root, name);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
