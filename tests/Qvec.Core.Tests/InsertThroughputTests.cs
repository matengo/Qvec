using System.Diagnostics;
using Qvec.Core;
using Xunit.Abstractions;

namespace Qvec.Core.Tests;

/// <summary>
/// Insert throughput gate.
///
/// The SIFT-1M benchmark measured index construction at ~242 inserts/s -- 69 minutes for a
/// million vectors -- and a CPU profile showed that the distance arithmetic accounted for well
/// under one percent of that. The rest was overhead per insert: element-by-element marshalling
/// through <c>MemoryMappedViewAccessor.ReadArray</c>/<c>WriteArray</c>, shared-pool rentals of
/// large scratch buffers, and allocation-heavy search state that kept the GC busy.
///
/// This test exists so that overhead cannot quietly come back. The floor is deliberately far
/// below what a developer machine achieves, because CI runners are slower and noisier and a
/// throughput gate that fails on a busy runner teaches people to ignore it.
/// </summary>
public class InsertThroughputTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", TestCategories.Slow)]
    public void AddEntry_OnClusteredVectors_SustainsMinimumThroughput()
    {
        const int N = 5_000;
        const int Dim = 128;

        var clusters = new Vec.ClusteredVectors(Dim, clusterCount: 100, seed: 8080);
        var rng = new Random(8080);
        var vectors = new float[N][];
        for (int i = 0; i < N; i++) vectors[i] = clusters.Next(Dim, rng);

        using var temp = new TempDb();
        using var db = temp.Open(dim: Dim, max: N, maxNeighbors: 32, maxLayers: 5,
            distance: DistanceFunction.Euclidean, indexSeed: 8080);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) db.AddEntry(vectors[i], "{}");
        sw.Stop();

        double perSecond = N / sw.Elapsed.TotalSeconds;
        output.WriteLine($"{N} inserts in {sw.Elapsed.TotalSeconds:F2}s = {perSecond:F0} inserts/s");
        AppendToStepSummary(perSecond);

        // Before the insert-path work this configuration ran at roughly 450 inserts/s on a
        // 12-core developer machine; after it, 1,300-1,450/s on the same machine, with the
        // remaining time spent in the O(M0²) distance arithmetic of the neighbour heuristic
        // rather than in overhead. GitHub-hosted ubuntu runners have been measured as low as
        // 711/s and 786/s for the same code under load (an 800/s floor produced false
        // failures on two master pushes in September 2026). The floor is set so that
        // regressing back to the old path -- roughly 225/s on such a runner -- is a clear
        // failure while a slow, busy runner still passes.
        //
        // This is a smoke floor, not a measurement. The real before/after comparison is the
        // perf workflow (.github/workflows/perf.yml, benchmarks/compare.ps1), which runs base
        // and head interleaved on the same runner; see docs/design-performance.md section 3.
        Assert.True(perSecond >= 500,
            $"Insert throughput was {perSecond:F0}/s, below the 500/s floor. " +
            "Profile AddEntry before adjusting this number.");
    }

    /// <summary>
    /// A passing xunit test leaves no trace of its output in CI, so the measured value is also
    /// written to the GitHub Actions step summary when one is available. That gives a series of
    /// runner values over time for free, which is what the floor above is calibrated against.
    /// </summary>
    private static void AppendToStepSummary(double insertsPerSecond)
    {
        string? summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(summaryPath)) return;

        try
        {
            File.AppendAllText(summaryPath,
                $"**InsertThroughputTests**: {insertsPerSecond:F0} inserts/s " +
                $"(5,000 × 128-d clustered, M = 32, single thread; smoke floor 500/s){Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // The summary is a convenience, never a reason to fail the test.
        }
    }
}
