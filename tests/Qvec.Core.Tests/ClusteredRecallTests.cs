using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// Recall gates measured on clustered vectors rather than uniformly random ones.
///
/// Every recall number this project published before was measured on uniform random vectors,
/// and that flattered nobody: in high dimensions uniform vectors are all roughly equidistant,
/// so the nearest neighbour is barely distinguishable from the hundredth and the index looks
/// far worse than it is. The real measurement against SIFT-1M lives in
/// <c>benchmarks/Qvec.Benchmarks</c> — it needs a 500 MB download and a long index build, so it
/// cannot be a CI gate. These tests are the offline, deterministic stand-in: clustered data
/// that behaves like real embeddings, with floors that would catch a genuine regression.
///
/// The floors are set below the measured values with room to spare, because the point of a gate
/// is to fail on real regressions and never on noise.
/// </summary>
public class ClusteredRecallTests
{
    [Theory]
    [InlineData(DistanceFunction.Euclidean)]
    [InlineData(DistanceFunction.Cosine)]
    [InlineData(DistanceFunction.DotProduct)]
    public void Search_OnClusteredVectors_StaysNearExhaustiveRecall(DistanceFunction distance)
    {
        var clusters = new Vec.ClusteredVectors(dim: 64, clusterCount: 50, seed: 1234);

        var result = RecallMeasurement.Measure(
            n: 2000,
            dim: 64,
            maxNeighbors: 32,
            efSearch: 200,
            distance: distance,
            seed: 555,
            queryCount: 100,
            vectorSource: clusters.Next);

        // Measured at the library defaults: recall@1 is 100% for all three metrics and
        // recall@10 is 99.9% or better. The floors leave a wide margin.
        Assert.True(
            result.RecallAt1 >= 0.95,
            $"recall@1 on clustered data was {result.RecallAt1:P1} under {distance}, expected at least 95%.");
        Assert.True(
            result.RecallAt10 >= 0.93,
            $"recall@10 on clustered data was {result.RecallAt10:P1} under {distance}, expected at least 93%.");
    }

    /// <summary>
    /// The same measurement with a narrow beam. This is the sensitive one: at efSearch = 16 the
    /// index has very little room to recover from a bad graph, so a fan-out or pruning
    /// regression shows up here long before it shows up at the defaults.
    /// </summary>
    [Fact]
    public void Search_OnClusteredVectorsWithANarrowBeam_StillFindsMostNeighbours()
    {
        var clusters = new Vec.ClusteredVectors(dim: 64, clusterCount: 50, seed: 4321);

        var result = RecallMeasurement.Measure(
            n: 2000,
            dim: 64,
            maxNeighbors: 16,
            efSearch: 16,
            distance: DistanceFunction.Euclidean,
            seed: 777,
            queryCount: 100,
            vectorSource: clusters.Next);

        // Measured: recall@1 99.0%, recall@10 93.6%.
        Assert.True(
            result.RecallAt1 >= 0.90,
            $"recall@1 with a narrow beam was {result.RecallAt1:P1}, expected at least 90%.");
        Assert.True(
            result.RecallAt10 >= 0.80,
            $"recall@10 with a narrow beam was {result.RecallAt10:P1}, expected at least 80%.");
    }

    /// <summary>
    /// Documents the gap between the two data distributions rather than asserting a bare number,
    /// so nobody reads an old uniform-data figure as a statement about Qvec on real data.
    /// </summary>
    [Fact]
    public void ClusteredVectorsAreSubstantiallyEasierThanUniformOnes()
    {
        const int Dim = 64;
        const int N = 2000;
        const int Ef = 16;
        const int M = 16;

        var clusters = new Vec.ClusteredVectors(dim: Dim, clusterCount: 50, seed: 2024);

        var clustered = RecallMeasurement.Measure(
            n: N, dim: Dim, maxNeighbors: M, efSearch: Ef,
            distance: DistanceFunction.Euclidean, seed: 90210, queryCount: 100,
            vectorSource: clusters.Next);

        var uniform = RecallMeasurement.Measure(
            n: N, dim: Dim, maxNeighbors: M, efSearch: Ef,
            distance: DistanceFunction.Euclidean, seed: 90210, queryCount: 100);

        Assert.True(
            clustered.RecallAt10 > uniform.RecallAt10,
            $"clustered recall@10 was {clustered.RecallAt10:P1} and uniform was {uniform.RecallAt10:P1}. " +
            "If uniform data is no longer the harder case, the published methodology note is wrong.");
    }
}
