using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// The layer-0 neighbour score cache (<see cref="QvecDatabase.NeighborScoreCacheBytes"/>) only
/// avoids recomputing similarities the insert path would otherwise re-read; it must never
/// change a single link. These tests build the same data with the cache on and off through
/// every path that writes a neighbour list — serial inserts, growth, deletes and the batch
/// path — and compare the raw graphs.
/// </summary>
public sealed class NeighborScoreCacheTests
{
    private static QvecInsert[] Batch(int n, int dim, int seed)
    {
        var clusters = new Vec.ClusteredVectors(dim, clusterCount: 10, seed: seed);
        var rng = new Random(seed);
        var batch = new QvecInsert[n];
        for (int i = 0; i < n; i++)
            batch[i] = new QvecInsert(clusters.Next(dim, rng), $$"""{"i":{{i}}}""", Guid.NewGuid());
        return batch;
    }

    private static void AssertSameGraph(string left, string right, int nodes, int layers)
    {
        var a = RawGraph.Read(left);
        var b = RawGraph.Read(right);
        for (int node = 0; node < nodes; node++)
        {
            for (int level = 0; level < layers; level++)
                Assert.Equal(a.Slots(node, level), b.Slots(node, level));
        }
    }

    [Theory]
    [InlineData(VectorQuantization.None)]
    [InlineData(VectorQuantization.Int8)]
    public void CacheOnAndOff_ProduceTheSameGraph_ThroughGrowthAndDeletes(VectorQuantization quantization)
    {
        const int n = 1200, dim = 16, layers = 4;
        var batch = Batch(n, dim, seed: 31);

        string Build(long cacheBytes)
        {
            var temp = new TempDb();
            // max is a quarter of n so the file grows several times while linking.
            using (var db = temp.Open(dim: dim, max: n / 4, maxNeighbors: 6, maxLayers: layers,
                                      distance: DistanceFunction.Euclidean, indexSeed: 31, quantization: quantization))
            {
                db.NeighborScoreCacheBytes = cacheBytes;
                for (int i = 0; i < n / 2; i++) db.AddEntry(batch[i].Vector, batch[i].Metadata, batch[i].ExternalId);
                // Deletes rewrite the survivors' lists; later inserts must see the dropped entries.
                for (int i = 0; i < n / 2; i += 9) db.Delete(batch[i].ExternalId!.Value);
                for (int i = n / 2; i < n; i++) db.AddEntry(batch[i].Vector, batch[i].Metadata, batch[i].ExternalId);
            }
            return temp.Path;
        }

        AssertSameGraph(Build(cacheBytes: 0), Build(cacheBytes: 64L << 20), n, layers);
    }

    [Fact]
    public void TinyBudget_StopsCachingWithoutChangingTheGraph()
    {
        const int n = 800, dim = 16, layers = 4;
        var batch = Batch(n, dim, seed: 47);

        string Build(long cacheBytes)
        {
            var temp = new TempDb();
            using (var db = temp.Open(dim: dim, max: n, maxNeighbors: 6, maxLayers: layers,
                                      distance: DistanceFunction.Cosine, indexSeed: 47))
            {
                db.NeighborScoreCacheBytes = cacheBytes;
                db.AddEntries(batch, maxDegreeOfParallelism: 1);
            }
            return temp.Path;
        }

        // 2 KiB holds only a few dozen entries, so most nodes run with a full budget miss.
        AssertSameGraph(Build(cacheBytes: 0), Build(cacheBytes: 2048), n, layers);
    }
}
