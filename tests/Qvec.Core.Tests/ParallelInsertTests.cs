using Qvec.Core;
using Xunit.Abstractions;

namespace Qvec.Core.Tests;

/// <summary>
/// <see cref="QvecDatabase.AddEntries"/> inserts a batch with several threads wiring nodes
/// into the graph at once. These tests pin what parallelism must not change: every entry is
/// stored and searchable, the graph stays well formed, duplicates and growth behave like the
/// single-entry path, and a degree of parallelism of one reproduces the serial build exactly.
/// </summary>
public sealed class ParallelInsertTests
{
    private readonly ITestOutputHelper _output;

    public ParallelInsertTests(ITestOutputHelper output) => _output = output;

    private static QvecInsert[] ClusteredBatch(int n, int dim, int seed)
    {
        var clusters = new Vec.ClusteredVectors(dim, clusterCount: 12, seed: seed);
        var rng = new Random(seed);
        var batch = new QvecInsert[n];
        for (int i = 0; i < n; i++)
            batch[i] = new QvecInsert(clusters.Next(dim, rng), $$"""{"i":{{i}}}""");
        return batch;
    }

    [Theory]
    [InlineData(VectorQuantization.None)]
    [InlineData(VectorQuantization.Int8)]
    public void AddEntries_StoresEveryEntryAndKeepsRecall(VectorQuantization quantization)
    {
        const int n = 3000, dim = 64;
        var batch = ClusteredBatch(n, dim, seed: 11);

        using var serialTemp = new TempDb();
        using var serial = serialTemp.Open(dim: dim, max: n, maxNeighbors: 16, distance: DistanceFunction.Cosine,
                                           indexSeed: 11, quantization: quantization);
        var serialIds = serial.AddEntries(batch, maxDegreeOfParallelism: 1);

        using var temp = new TempDb();
        using var db = temp.Open(dim: dim, max: n, maxNeighbors: 16, distance: DistanceFunction.Cosine,
                                 indexSeed: 11, quantization: quantization);

        var ids = db.AddEntries(batch, maxDegreeOfParallelism: 8);

        Assert.Equal(n, ids.Count);
        Assert.Equal(n, db.LiveCount);
        Assert.Equal(n, ids.Distinct().Count());

        for (int i = 0; i < n; i += 97)
        {
            var stored = db.GetByGuid(ids[i]);
            Assert.NotNull(stored);
            Assert.Equal(batch[i].Metadata, stored.Value.Metadata);
        }

        double recall = SelfRecall(db, batch, ids);
        double serialRecall = SelfRecall(serial, batch, serialIds);
        _output.WriteLine($"{quantization}: self-recall@1 parallel = {recall:P1}, serial = {serialRecall:P1}");
        Assert.True(recall >= 0.95, $"Self-recall@1 after parallel insert was {recall:P1}, expected at least 95 %.");
        Assert.True(recall >= serialRecall - 0.02, $"Parallel self-recall {recall:P1} is more than 2 pp below the serial build's {serialRecall:P1}.");
    }

    private static double SelfRecall(QvecDatabase db, QvecInsert[] batch, IReadOnlyList<Guid> ids)
    {
        const int queries = 300;
        int hits = 0;
        for (int i = 0; i < queries; i++)
        {
            int q = (i * 7919) % batch.Length;
            var result = db.Search(batch[q].Vector, topK: 1, efSearch: 200);
            if (result[0].Id == ids[q]) hits++;
        }
        return hits / (double)queries;
    }

    [Fact]
    public void AddEntries_WithDegreeOfParallelismOne_ReproducesTheSerialGraphExactly()
    {
        const int n = 1500, dim = 32;
        var batch = ClusteredBatch(n, dim, seed: 5);
        var ids = new Guid[n];
        for (int i = 0; i < n; i++) ids[i] = Guid.NewGuid();
        for (int i = 0; i < n; i++) batch[i] = batch[i] with { ExternalId = ids[i] };

        using var serial = new TempDb();
        using (var db = serial.Open(dim: dim, max: n, maxNeighbors: 8, distance: DistanceFunction.Cosine, indexSeed: 5))
        {
            foreach (var entry in batch) db.AddEntry(entry.Vector, entry.Metadata, entry.ExternalId);
        }

        using var parallel = new TempDb();
        using (var db = parallel.Open(dim: dim, max: n, maxNeighbors: 8, distance: DistanceFunction.Cosine, indexSeed: 5))
        {
            db.AddEntries(batch, maxDegreeOfParallelism: 1);
        }

        var a = RawGraph.Read(serial.Path);
        var b = RawGraph.Read(parallel.Path);
        for (int node = 0; node < n; node++)
        {
            for (int level = 0; level < 5; level++)
                Assert.Equal(a.Slots(node, level), b.Slots(node, level));
        }
    }

    [Fact]
    public void AddEntries_ProducesWellFormedNeighbourLists()
    {
        const int n = 4000, dim = 16, maxLayers = 4;
        var batch = ClusteredBatch(n, dim, seed: 23);

        int serialOrphans = CountLayerZeroOrphans(batch, n, dim, maxLayers, threads: 1);
        int parallelOrphans = CountLayerZeroOrphans(batch, n, dim, maxLayers, threads: Environment.ProcessorCount);

        _output.WriteLine($"nodes without any incoming layer-0 link: serial {serialOrphans}/{n}, parallel {parallelOrphans}/{n}");

        // A tiny fanout (M0 = 8) on clustered data leaves some nodes without incoming links even
        // serially. Concurrent siblings compete for the same full lists, so the parallel build is
        // allowed somewhat more — but not the kind of jump a lost back-link race would produce.
        Assert.True(parallelOrphans <= 2 * serialOrphans + 10,
            $"Parallel build left {parallelOrphans} nodes without incoming layer-0 links, serial left {serialOrphans}.");
    }

    private int CountLayerZeroOrphans(QvecInsert[] batch, int n, int dim, int maxLayers, int threads)
    {
        using var temp = new TempDb();
        using (var db = temp.Open(dim: dim, max: n, maxNeighbors: 4, maxLayers: maxLayers, distance: DistanceFunction.Euclidean, indexSeed: 23))
        {
            db.AddEntries(batch, maxDegreeOfParallelism: threads);
        }

        var graph = RawGraph.Read(temp.Path);
        var inDegree = new int[n];

        for (int node = 0; node < n; node++)
        {
            for (int level = 0; level < maxLayers; level++)
            {
                var slots = graph.Slots(node, level);
                bool seenEmpty = false;
                var seen = new HashSet<int>();
                foreach (int slot in slots)
                {
                    if (slot == -1) { seenEmpty = true; continue; }
                    Assert.False(seenEmpty, $"Node {node} level {level} has a neighbour after an empty slot.");
                    Assert.InRange(slot, 0, n - 1);
                    Assert.NotEqual(node, slot);
                    Assert.True(seen.Add(slot), $"Node {node} level {level} lists neighbour {slot} twice.");
                    if (level == 0) inDegree[slot]++;
                }
            }
        }

        return inDegree.Count(d => d == 0);
    }

    [Fact]
    public void AddEntries_SkipsDuplicateExternalIds_WithinTheBatchAndAcrossCalls()
    {
        const int dim = 8;
        var rng = new Random(3);
        var shared = Guid.NewGuid();

        using var temp = new TempDb();
        using var db = temp.Open(dim: dim, max: 64, maxNeighbors: 4, distance: DistanceFunction.Cosine);

        db.AddEntry(Vec.Random(dim, rng), "first", shared);

        var batch = new[]
        {
            new QvecInsert(Vec.Random(dim, rng), "dup-of-existing", shared),
            new QvecInsert(Vec.Random(dim, rng), "a", Guid.NewGuid()),
            new QvecInsert(Vec.Random(dim, rng), "b"),
        };
        var repeated = batch[1].ExternalId!.Value;
        batch = [.. batch, new QvecInsert(Vec.Random(dim, rng), "dup-in-batch", repeated)];

        var ids = db.AddEntries(batch, maxDegreeOfParallelism: 4);

        Assert.Equal(4, ids.Count);
        Assert.Equal(shared, ids[0]);
        Assert.Equal(repeated, ids[1]);
        Assert.Equal(repeated, ids[3]);
        Assert.Equal(3, db.LiveCount);
        Assert.Equal("first", db.GetByGuid(shared)!.Value.Metadata);
        Assert.Equal("a", db.GetByGuid(repeated)!.Value.Metadata);
    }

    [Fact]
    public void AddEntries_GrowsTheFileAndReusesDeletedSlots()
    {
        const int dim = 8;
        using var temp = new TempDb();
        using var db = temp.Open(dim: dim, max: 50, maxNeighbors: 4, distance: DistanceFunction.Cosine, indexSeed: 9);

        var first = db.AddEntries(ClusteredBatch(40, dim, seed: 1), maxDegreeOfParallelism: 4);
        for (int i = 0; i < 20; i++) db.Delete(first[i]);
        Assert.Equal(20, db.LiveCount);

        // 20 tombstoned slots plus 10 free rows, then growth for the remaining 970.
        var second = db.AddEntries(ClusteredBatch(1000, dim, seed: 2), maxDegreeOfParallelism: 4);

        Assert.Equal(1000, second.Count);
        Assert.Equal(1020, db.LiveCount);
        Assert.Equal(0, db.DeletedCount);
        Assert.True(db.MaxCount >= 1020);
        Assert.All(second, id => Assert.NotNull(db.GetByGuid(id)));
    }

    [Fact]
    public void AddEntries_WithEmptyBatch_IsANoOp()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 8);

        var ids = db.AddEntries([], maxDegreeOfParallelism: 4);

        Assert.Empty(ids);
        Assert.Equal(0, db.LiveCount);
        Assert.True(db.IsHealthy());
    }

    [Fact]
    public void AddEntries_RejectsWrongDimension()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 8);

        Assert.Throws<QvecDimensionException>(() => db.AddEntries([new QvecInsert(new float[3], "{}")]));
        Assert.Equal(0, db.LiveCount);
    }
}
