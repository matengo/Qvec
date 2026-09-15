using Qvec.Core;
using Xunit.Abstractions;

namespace Qvec.Core.Tests;

// These recall tests intentionally use deterministic random uniform vectors.
// Uniform high-dimensional vectors are harder for HNSW than real embeddings,
// which usually have lower intrinsic dimensionality and clearer cluster
// structure. Treat the absolute numbers as pessimistic and dataset-dependent;
// the robust signal is the relative trend across construction/search parameters.
[Collection(TestCollections.TimingSensitive)]
public sealed class RecallTests
{
    private const int DefaultMaxNeighbors = 32;

    /// <summary>
    /// Deliberately below the library default, so the fast regression tests measure a
    /// pessimistic lower bound rather than the configuration users actually get.
    /// </summary>
    private const int NarrowEfSearch = 50;

    /// <summary>The library's own default search width, used by the default-quality tests.</summary>
    private const int LibraryDefaultEfSearch = QvecDatabase.DefaultEfSearch;

    private readonly ITestOutputHelper _output;

    public RecallTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<DistanceFunction, int, double> FastRecallFloorCases => new()
    {
        // Measured before fixing floors (n=1000, dim=64, efSearch=50).
        // DotProduct seeds 101/202/303: 0.950 / 1.000 / 1.000 recall@1.
        { DistanceFunction.DotProduct, 101, 0.85 },
        { DistanceFunction.DotProduct, 202, 0.85 },
        { DistanceFunction.DotProduct, 303, 0.85 },

        // Measured before fixing floors (n=1000, dim=64, efSearch=50).
        // Cosine seeds 101/202/303: 1.000 / 1.000 / 1.000 recall@1.
        { DistanceFunction.Cosine, 101, 0.85 },
        { DistanceFunction.Cosine, 202, 0.85 },
        { DistanceFunction.Cosine, 303, 0.85 },
    };

    public static TheoryData<DistanceFunction> DistanceFunctions => new()
    {
        DistanceFunction.DotProduct,
        DistanceFunction.Cosine,
    };

    [Theory]
    [MemberData(nameof(FastRecallFloorCases))]
    public void Search_RecallAtOne_StaysAboveFastRegressionFloor(
        DistanceFunction distance,
        int seed,
        double floor)
    {
        var recall = RecallMeasurement.Measure(
            n: 1_000,
            dim: 64,
            maxNeighbors: DefaultMaxNeighbors,
            efSearch: NarrowEfSearch,
            distance: distance,
            seed: seed,
            queryCount: 40);

        _output.WriteLine($"{distance} seed {seed}: recall@1={recall.RecallAt1:P1}, recall@10={recall.RecallAt10:P1}");

        Assert.True(
            recall.RecallAt1 >= floor,
            $"{distance} seed {seed} recall@1 {recall.RecallAt1:P1} fell below floor {floor:P1}; recall@10 was {recall.RecallAt10:P1}.");
    }

    [Fact]
    public void Search_AfterDeletingHalf_StaysAboveFastRegressionFloor()
    {
        // Measured before fixing floor (n=1000, dim=64, default params, seed=404,
        // exact ground truth over surviving rows after deleting every other row):
        // 1.000 recall@1.
        const double floor = 0.85;

        var recall = RecallMeasurement.Measure(
            n: 1_000,
            dim: 64,
            maxNeighbors: DefaultMaxNeighbors,
            efSearch: NarrowEfSearch,
            distance: DistanceFunction.DotProduct,
            seed: 404,
            queryCount: 40,
            deleteEveryOtherEntry: true);

        _output.WriteLine($"After deleting half: recall@1={recall.RecallAt1:P1}, recall@10={recall.RecallAt10:P1}");

        Assert.True(
            recall.RecallAt1 >= floor,
            $"Recall@1 after deleting half was {recall.RecallAt1:P1}, below floor {floor:P1}; recall@10 was {recall.RecallAt10:P1}.");
    }

    /// <summary>
    /// Default-quality bar, measured with the parameters a user actually gets.
    /// The library-review baseline on 5,000 random uniform 128-dimensional vectors at
    /// maxNeighbors=32 and efSearch=50 measured only 81.0% recall@1. This test previously
    /// hardcoded that same narrow efSearch=50 despite being named "default parameters";
    /// it now uses the library's own default search width.
    /// </summary>
    [Theory]
    [MemberData(nameof(DistanceFunctions))]
    [Trait("Category", TestCategories.Slow)]
    public void Search_DefaultParameters_ShouldReachAspirationalRecall(DistanceFunction distance)
    {
        var recall = RecallMeasurement.Measure(
            n: 5_000,
            dim: 128,
            maxNeighbors: DefaultMaxNeighbors,
            efSearch: LibraryDefaultEfSearch,
            distance: distance,
            seed: 9001,
            queryCount: 200);

        _output.WriteLine($"{distance} default parameters: recall@1={recall.RecallAt1:P1}, recall@10={recall.RecallAt10:P1}");

        Assert.True(
            recall.RecallAt1 >= 0.95,
            $"{distance} default recall@1 was {recall.RecallAt1:P1}; recall@10 was {recall.RecallAt10:P1}.");
    }

    /// <summary>
    /// Deletion-quality bar at default search width. The library review measured recall@1
    /// around 94% after deleting 50% of entries because tombstoned nodes remained
    /// relevant to graph navigation.
    /// </summary>
    [Fact]
    [Trait("Category", TestCategories.Slow)]
    public void Search_AfterDeletingHalf_ShouldReachAspirationalRecall()
    {
        var recall = RecallMeasurement.Measure(
            n: 5_000,
            dim: 128,
            maxNeighbors: DefaultMaxNeighbors,
            efSearch: LibraryDefaultEfSearch,
            distance: DistanceFunction.DotProduct,
            seed: 9010,
            queryCount: 200,
            deleteEveryOtherEntry: true);

        _output.WriteLine($"After deleting half, default width: recall@1={recall.RecallAt1:P1}, recall@10={recall.RecallAt10:P1}");

        Assert.True(
            recall.RecallAt1 >= 0.95,
            $"Recall@1 after deleting half was {recall.RecallAt1:P1}; recall@10 was {recall.RecallAt10:P1}.");
    }

    [Fact]
    [Trait("Category", TestCategories.Slow)]
    public void Search_RecallParameterSweep_WritesDiagnosticTable()
    {
        int[] maxNeighborsValues = [16, 32, 64];
        int[] efSearchValues = [50, 200, 800];

        _output.WriteLine("| maxNeighbors | efSearch=50 r@1/r@10 | efSearch=200 r@1/r@10 | efSearch=800 r@1/r@10 |");
        _output.WriteLine("|---:|---:|---:|---:|");

        foreach (int maxNeighbors in maxNeighborsValues)
        {
            var cells = new List<string>();
            foreach (int efSearch in efSearchValues)
            {
                var recall = RecallMeasurement.Measure(
                    n: 2_000,
                    dim: 128,
                    maxNeighbors: maxNeighbors,
                    efSearch: efSearch,
                    distance: DistanceFunction.DotProduct,
                    seed: 50_000 + maxNeighbors + efSearch,
                    queryCount: 80);

                cells.Add($"{recall.RecallAt1:P1} / {recall.RecallAt10:P1}");
            }

            _output.WriteLine($"| {maxNeighbors} | {string.Join(" | ", cells)} |");
        }
    }
}

internal static class RecallMeasurement
{
    public static RecallResult Measure(
        int n,
        int dim,
        int maxNeighbors,
        int efSearch,
        DistanceFunction distance,
        int seed,
        int queryCount,
        bool deleteEveryOtherEntry = false)
    {
        if (queryCount <= 0) throw new ArgumentOutOfRangeException(nameof(queryCount));
        if (queryCount > n) throw new ArgumentOutOfRangeException(nameof(queryCount));

        using var temp = new TempDb();
        using var db = temp.Open(
            dim: dim,
            max: n,
            maxNeighbors: maxNeighbors,
            maxLayers: 5,
            distance: distance);

        var rng = new Random(seed);
        var ids = new Guid[n];

        for (int i = 0; i < n; i++)
        {
            var vector = Vec.Random(dim, rng);
            ids[i] = DeterministicId(i);
            db.AddEntry(vector, $$"""{"i":{{i}}}""", ids[i]);
        }

        if (deleteEveryOtherEntry)
        {
            for (int i = 0; i < n; i += 2)
            {
                Assert.True(db.Delete(ids[i]));
            }

            Assert.Equal(n / 2, db.DeletedCount);
            Assert.Equal(n - n / 2, db.LiveCount);
        }

        double recallAt1Sum = 0;
        double recallAt10Sum = 0;

        for (int measuredQueries = 0; measuredQueries < queryCount; measuredQueries++)
        {
            var query = Vec.Random(dim, rng);

            // SearchSimple was verified in QvecDatabase.cs to scan every current
            // non-deleted row, skip tombstones, sort by score, and use the same
            // score path as HNSW. It therefore matches dot-product semantics and
            // cosine semantics, where both stored vectors and query copies are
            // normalized by QvecDatabase before scoring.
            var exactTop10 = db.SearchSimple(query.ToArray(), topK: 10).Select(r => r.Id).ToArray();
            var approximateTop10 = db.Search(query.ToArray(), topK: 10, efSearch: efSearch).Select(r => r.Id).ToArray();

            Assert.NotEmpty(exactTop10);
            Assert.NotEmpty(approximateTop10);

            if (approximateTop10[0] == exactTop10[0])
            {
                recallAt1Sum++;
            }

            recallAt10Sum += approximateTop10.Intersect(exactTop10).Count() / 10.0;
        }

        return new RecallResult(recallAt1Sum / queryCount, recallAt10Sum / queryCount);
    }

    private static Guid DeterministicId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        return new Guid(bytes);
    }
}

internal sealed record RecallResult(double RecallAt1, double RecallAt10);
