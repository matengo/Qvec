using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// Proof for <see cref="DistanceFunction.Euclidean"/>.
///
/// Euclidean is not a cosmetic third option. Dot product and cosine both rank by direction:
/// dot product additionally rewards magnitude, and cosine ignores it entirely. Neither can
/// answer "which stored vector is physically closest to this one", which is what the standard
/// ANN corpora (SIFT, GIST) and most image and audio embeddings actually mean by a neighbour.
/// Without it, Qvec could not be measured against any published ground truth.
///
/// Internally the score stays "higher is better" so every heap, sort and pruning comparison in
/// the HNSW code keeps working unchanged. The score for Euclidean is therefore the *negated
/// squared* distance: negated to flip the ordering, squared to skip a square root that cannot
/// change the ordering anyway.
/// </summary>
public class EuclideanDistanceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(128)]
    [InlineData(129)]
    public void NegativeSquaredDistance_MatchesNaiveLoop(int dim)
    {
        // Dimensions that are not a multiple of the SIMD width exercise the scalar tail, which
        // is exactly where a hand-vectorised loop goes wrong.
        var rng = new Random(20250915 + dim);
        var left = Vec.Random(dim, rng);
        var right = Vec.Random(dim, rng);

        double expected = 0;
        for (int i = 0; i < dim; i++)
        {
            double d = left[i] - right[i];
            expected += d * d;
        }

        float actual = QvecDatabase.NegativeSquaredDistance(left, right, dim);

        Assert.Equal(-expected, actual, 3);
    }

    [Fact]
    public void NegativeSquaredDistance_OfAVectorWithItself_IsZero()
    {
        var rng = new Random(7);
        var v = Vec.Random(64, rng);

        Assert.Equal(0f, QvecDatabase.NegativeSquaredDistance(v, v, v.Length), 4);
    }

    /// <summary>
    /// The discriminating case. Under dot product the long vector wins every query because
    /// magnitude dominates; under Euclidean the physically nearest vector wins. If the metric
    /// were silently falling back to dot product, this test would fail.
    /// </summary>
    [Fact]
    public void Search_WithEuclidean_PrefersTheNearestVectorNotTheLongestOne()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 16, distance: DistanceFunction.Euclidean);

        // Points along the first axis. The query sits at 1.0.
        var near = new float[] { 1.1f, 0, 0, 0 };
        var far = new float[] { 50f, 0, 0, 0 };

        var nearId = db.AddEntry(near, """{"name":"near"}""");
        var farId = db.AddEntry(far, """{"name":"far"}""");

        var query = new float[] { 1.0f, 0, 0, 0 };

        var results = db.Search(query, topK: 2);

        Assert.Equal(nearId, results[0].Id);
        Assert.Equal(farId, results[1].Id);

        // Sanity check that the fixture really is adversarial: dot product ranks them the
        // other way round, so the assertion above is testing the metric and not the ordering.
        Assert.True(
            QvecDatabase.DotProduct(query, far, 4) > QvecDatabase.DotProduct(query, near, 4),
            "The fixture no longer distinguishes Euclidean from dot product.");
    }

    [Fact]
    public void Search_WithEuclidean_ReturnsAnExactlyStoredVectorFirst()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 16, max: 400, maxNeighbors: 16, distance: DistanceFunction.Euclidean);

        var rng = new Random(99);
        var ids = new Guid[200];
        var vectors = new float[200][];

        for (int i = 0; i < ids.Length; i++)
        {
            // Scale each vector differently so magnitude varies widely across the set.
            vectors[i] = Vec.Random(16, rng);
            float scale = 1f + (i % 20);
            for (int j = 0; j < vectors[i].Length; j++) vectors[i][j] *= scale;
            ids[i] = db.AddEntry(vectors[i], $$"""{"i":{{i}}}""");
        }

        for (int i = 0; i < ids.Length; i += 17)
        {
            var hits = db.Search(vectors[i].ToArray(), topK: 1, efSearch: 200);
            Assert.Equal(ids[i], hits[0].Id);
        }
    }

    [Fact]
    public void SearchSimple_WithEuclidean_OrdersEveryEntryByTrueDistance()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 12, max: 200, distance: DistanceFunction.Euclidean);

        var rng = new Random(4242);
        var vectors = new List<float[]>();
        var ids = new List<Guid>();

        for (int i = 0; i < 120; i++)
        {
            var v = Vec.Random(12, rng);
            vectors.Add(v);
            ids.Add(db.AddEntry(v, $$"""{"i":{{i}}}"""));
        }

        var query = Vec.Random(12, rng);

        var expected = Enumerable.Range(0, vectors.Count)
            .OrderBy(i => Distance(query, vectors[i]))
            .Take(10)
            .Select(i => ids[i])
            .ToArray();

        var actual = db.SearchSimple(query.ToArray(), topK: 10).Select(r => r.Id).ToArray();

        Assert.Equal(expected, actual);

        static double Distance(float[] a, float[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = a[i] - b[i];
                sum += d * d;
            }
            return sum;
        }
    }

    /// <summary>
    /// Cosine normalises stored vectors on the way in. Euclidean must not: scaling a vector
    /// changes its distance to everything else, so normalising would quietly answer a
    /// different question than the caller asked.
    /// </summary>
    [Fact]
    public void AddEntry_WithEuclidean_StoresTheVectorUnnormalised()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 8, distance: DistanceFunction.Euclidean);

        var original = new float[] { 3f, 4f, 0f, 0f };
        var id = db.AddEntry(original, "{}");

        var stored = db.GetByGuid(id);

        Assert.NotNull(stored);
        Assert.Equal(original, stored!.Value.Vector);
    }

    [Fact]
    public void Open_RoundTripsTheEuclideanDistanceFunction()
    {
        using var temp = new TempDb();
        Guid id;

        using (var db = temp.Open(dim: 4, max: 8, distance: DistanceFunction.Euclidean))
        {
            id = db.AddEntry(new float[] { 1f, 2f, 3f, 4f }, """{"a":1}""");
        }

        using (var reopened = QvecDatabase.Open(temp.Path))
        {
            Assert.Equal(DistanceFunction.Euclidean, reopened.DistanceFunction);

            var hits = reopened.Search(new float[] { 1f, 2f, 3f, 4f }, topK: 1);
            Assert.Equal(id, hits[0].Id);
        }
    }

    [Fact]
    public void Open_WithMismatchedDistanceFunction_Throws()
    {
        using var temp = new TempDb();

        using (var db = temp.Open(dim: 4, max: 8, distance: DistanceFunction.Euclidean))
        {
            db.AddEntry(new float[] { 1f, 0f, 0f, 0f }, "{}");
        }

        Assert.ThrowsAny<Exception>(() => temp.Open(dim: 4, max: 8, distance: DistanceFunction.Cosine));
    }

    /// <summary>
    /// The graph has to be navigable under the new metric, not merely correct on a linear scan.
    /// A broken score would still return *something*, so recall is the only assertion that
    /// distinguishes a working index from a random walk.
    /// </summary>
    [Fact]
    public void Search_WithEuclidean_ReachesUsefulRecall()
    {
        var result = RecallMeasurement.Measure(
            n: 2000,
            dim: 32,
            maxNeighbors: 16,
            efSearch: 100,
            distance: DistanceFunction.Euclidean,
            seed: 31337,
            queryCount: 100);

        Assert.True(
            result.RecallAt1 >= 0.90,
            $"recall@1 was {result.RecallAt1:P1}, expected at least 90%.");
        Assert.True(
            result.RecallAt10 >= 0.80,
            $"recall@10 was {result.RecallAt10:P1}, expected at least 80%.");
    }
}
