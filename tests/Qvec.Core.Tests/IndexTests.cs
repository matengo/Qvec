using Qvec.Core;

namespace Qvec.Core.Tests;

public class IndexTests
{
    /// <summary>
    /// Exact-copy queries should still find every surviving row after scattered
    /// deletes. Uses cosine so that the identical vector is genuinely the nearest
    /// neighbour; under raw dot product the largest-magnitude vector wins every
    /// query regardless of the graph, which says nothing about deletion repair.
    /// </summary>
    [Fact]
    public void Search_AfterScatteredDeletion_FindsEverySurvivingEntryByExactVector()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 320, maxNeighbors: 64, maxLayers: 1, distance: DistanceFunction.Cosine);
        var rng = new Random(1001);
        var entries = AddRandomEntries(db, count: 240, dim: 8, rng);

        foreach (var entry in entries.Where((_, i) => i % 7 == 0 || i % 11 == 0))
            Assert.True(db.Delete(entry.Id));

        foreach (var entry in entries.Where((_, i) => i % 7 != 0 && i % 11 != 0))
        {
            var result = db.Search(entry.Vector, topK: 1, efSearch: 128);
            Assert.NotEmpty(result);
            Assert.Equal(entry.Id, result[0].Id);
        }
    }

    /// <summary>
    /// Deleting the original entry point must not make the remaining rows unreachable.
    /// Cosine is used so an exact-copy query has a unique correct answer.
    /// </summary>
    [Fact]
    public void Search_AfterDeletingOriginalEntryPoint_StillFindsRemainingEntries()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 6, max: 80, maxNeighbors: 32, maxLayers: 1, distance: DistanceFunction.Cosine);
        var entries = AddRandomEntries(db, count: 40, dim: 6, new Random(2002));

        Assert.True(db.Delete(entries[0].Id));

        foreach (var entry in entries.Skip(1))
        {
            var result = db.Search(entry.Vector, topK: 1, efSearch: 64);
            Assert.NotEmpty(result);
            Assert.Equal(entry.Id, result[0].Id);
        }
    }

    /// <summary>
    /// Deleting all entries should leave the database able to accept new entries and
    /// establish a fresh entry point. It currently remains full because tombstones are
    /// not reused.
    /// </summary>
    [Fact]
    public void AddEntry_AfterDeletingEverything_ReestablishesWorkingIndex()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 4, maxNeighbors: 3, maxLayers: 1);
        var oldIds = Enumerable.Range(0, 4)
            .Select(i => db.AddEntry(Vec.Basis(4, i), $"old-{i}"))
            .ToArray();
        foreach (var id in oldIds)
            Assert.True(db.Delete(id));

        var first = db.AddEntry(Vec.Basis(4, 0), "new-0");
        var second = db.AddEntry(Vec.Basis(4, 1), "new-1");

        Assert.Equal(2, db.LiveCount);
        Assert.Equal(first, db.Search(Vec.Basis(4, 0), topK: 1, efSearch: 10)[0].Id);
        Assert.Equal(second, db.Search(Vec.Basis(4, 1), topK: 1, efSearch: 10)[0].Id);
    }

    /// <summary>
    /// RebuildIndex should preserve result sets. It currently passes pooled arrays
    /// from GetVector directly into SearchSimpleParallel, so rented arrays larger
    /// than VectorDimension throw QvecDimensionException during rebuild.
    /// </summary>
    [Fact]
    public void RebuildIndex_PreservesSearchResultsAndLiveCount()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 80, maxNeighbors: 32, maxLayers: 1);
        var entries = AddRandomEntries(db, count: 50, dim: 8, new Random(3003));
        var queries = entries.Where((_, i) => i % 9 == 0).Select(e => e.Vector).ToArray();
        var before = queries.Select(q => db.Search(q, topK: 5, efSearch: 64).Select(r => r.Id).ToArray()).ToArray();

        db.RebuildIndex();

        Assert.Equal(50, db.LiveCount);
        for (var i = 0; i < queries.Length; i++)
            Assert.Equal(before[i], db.Search(queries[i], topK: 5, efSearch: 64).Select(r => r.Id).ToArray());
    }

    /// <summary>
    /// RebuildIndex should skip tombstones and preserve live rows. It currently
    /// throws QvecDimensionException because it searches with oversized pooled
    /// buffers returned by GetVector.
    /// </summary>
    [Fact]
    public void RebuildIndex_WithTombstones_PreservesLiveResultsAndCount()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 90, maxNeighbors: 32, maxLayers: 1);
        var entries = AddRandomEntries(db, count: 60, dim: 8, new Random(4004));
        foreach (var entry in entries.Where((_, i) => i % 5 == 0))
            Assert.True(db.Delete(entry.Id));
        var queries = entries.Where((_, i) => i % 5 != 0 && i % 13 == 0).Select(e => e.Vector).ToArray();
        var before = queries.Select(q => db.Search(q, topK: 5, efSearch: 64).Select(r => r.Id).ToArray()).ToArray();

        db.RebuildIndex();

        Assert.Equal(48, db.LiveCount);
        for (var i = 0; i < queries.Length; i++)
            Assert.Equal(before[i], db.Search(queries[i], topK: 5, efSearch: 64).Select(r => r.Id).ToArray());
    }

    [Fact]
    public void RebuildIndex_OnEmptyDatabase_KeepsItSearchableAsEmpty()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 5, maxNeighbors: 3, maxLayers: 1);

        db.RebuildIndex();

        Assert.Equal(0, db.LiveCount);
        Assert.Empty(db.Search(Vec.Basis(4, 0), topK: 3));
    }

    [Fact]
    public void FilteredSearch_MatchingNothingOrEverythingBehavesLikeExpected()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 8, maxNeighbors: 8, maxLayers: 1);
        for (var i = 0; i < 6; i++)
            db.AddEntry(new[] { 1f - i * 0.1f, i * 0.1f, 0f, 0f }, i % 2 == 0 ? "group=even" : "group=odd");

        var unfiltered = db.Search(Vec.Basis(4, 0), topK: 4, efSearch: 10);
        var all = db.Search(Vec.Basis(4, 0), _ => true, topK: 4, efSearch: 10);
        var none = db.Search(Vec.Basis(4, 0), _ => false, topK: 4, efSearch: 10);

        Assert.Equal(unfiltered.Select(r => r.Id), all.Select(r => r.Id));
        Assert.Empty(none);
        Assert.All(db.Search(Vec.Basis(4, 0), m => m == "group=even", topK: 3, efSearch: 10),
            r => Assert.Equal("group=even", r.Metadata));
    }

    /// <summary>
    /// Filtered search must return topK matching entries when enough matches exist, even when
    /// every one of the best unfiltered candidates fails the filter. Post-filtering an unfiltered
    /// top-ef retrieval cannot do this; filtering inside the graph walk can.
    /// </summary>
    [Fact]
    public void FilteredSearch_ReturnsTopKMatchesEvenWhenBestUnfilteredCandidatesDoNotMatch()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 90, maxNeighbors: 32, maxLayers: 1);
        for (var i = 0; i < 40; i++)
            db.AddEntry(new[] { 1f, i / 1000f, 0f, 0f }, "group=skip");
        for (var i = 0; i < 10; i++)
            db.AddEntry(new[] { 0.5f, 0.5f + i / 1000f, 0f, 0f }, "group=keep");

        var filtered = db.Search(Vec.Basis(4, 0), m => m == "group=keep", topK: 5, efSearch: 5);

        Assert.Equal(5, filtered.Count);
        Assert.All(filtered, r => Assert.Equal("group=keep", r.Metadata));
    }

    [Fact]
    public void Search_ReturnsSameTop1AsSearchSimpleOnSmallDataset()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 6, max: 40, maxNeighbors: 32, maxLayers: 1);
        var entries = AddRandomEntries(db, count: 24, dim: 6, new Random(5005));

        foreach (var query in entries.Select(e => e.Vector))
        {
            var simple = db.SearchSimple(query, topK: 1);
            var hnsw = db.Search(query, topK: 1, efSearch: 32);
            Assert.Equal(simple[0].Id, hnsw[0].Id);
        }
    }

    [Fact]
    public void SearchSimpleParallel_ReturnsIdenticalResultsToSearchSimple()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 7, max: 70, maxNeighbors: 32, maxLayers: 1);
        var entries = AddRandomEntries(db, count: 50, dim: 7, new Random(6006));

        foreach (var query in entries.Where((_, i) => i % 8 == 0).Select(e => e.Vector))
        {
            var sequential = db.SearchSimple(query, topK: 7);
            var parallel = db.SearchSimpleParallel(query, topK: 7);
            Assert.Equal(sequential.Select(r => r.Id), parallel.Select(r => r.Id));
            Assert.Equal(sequential.Select(r => r.Score), parallel.Select(r => r.Score));
            Assert.Equal(sequential.Select(r => r.Metadata), parallel.Select(r => r.Metadata));
        }
    }

    /// <summary>
    /// The contract for filtered search: the result must be exactly the brute-force top-k over
    /// the entries that pass the filter, in descending score order — not merely "some matches".
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void FilteredSearch_AgreesExactlyWithBruteForceOverMatchingEntries(int keepEveryNth)
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 16, max: 600, maxNeighbors: 16, maxLayers: 4,
            distance: DistanceFunction.Cosine);

        var rng = new Random(424242);
        var stored = new List<(Guid Id, float[] Vector, string Meta)>();
        for (var i = 0; i < 500; i++)
        {
            var vector = Vec.Random(16, rng);
            var meta = i % keepEveryNth == 0 ? "group=keep" : "group=skip";
            stored.Add((db.AddEntry((float[])vector.Clone(), meta), vector, meta));
        }

        var query = Vec.Random(16, rng);

        var expected = stored
            .Where(e => e.Meta == "group=keep")
            .Select(e => (e.Id, Score: Cosine(query, e.Vector)))
            .OrderByDescending(e => e.Score)
            .Take(10)
            .Select(e => e.Id)
            .ToList();

        var actual = db.Search((float[])query.Clone(), m => m == "group=keep", topK: 10);

        Assert.All(actual, r => Assert.Equal("group=keep", r.Metadata));
        Assert.Equal(expected, actual.Select(r => r.Id));
        Assert.Equal(actual.Select(r => r.Score).OrderByDescending(s => s), actual.Select(r => r.Score));
    }

    /// <summary>
    /// A filter matching fewer entries than topK must return every match and nothing else,
    /// rather than padding the result with non-matching entries or returning an empty list.
    /// </summary>
    [Fact]
    public void FilteredSearch_WithFewerMatchesThanTopK_ReturnsExactlyTheMatches()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 300, maxNeighbors: 16, maxLayers: 3,
            distance: DistanceFunction.Cosine);

        var rng = new Random(77001);
        var expected = new List<Guid>();
        for (var i = 0; i < 200; i++)
        {
            bool keep = i is 3 or 97 or 188;
            var id = db.AddEntry(Vec.Random(8, rng), keep ? "group=keep" : "group=skip");
            if (keep) expected.Add(id);
        }

        var actual = db.Search(Vec.Random(8, rng), m => m == "group=keep", topK: 10);

        Assert.Equal(3, actual.Count);
        Assert.Equal(expected.OrderBy(x => x), actual.Select(r => r.Id).OrderBy(x => x));
    }

    /// <summary>
    /// Proves the filtering really happens inside the graph walk. With a filter that keeps a
    /// quarter of the entries there are far more matches than topK, so a filter-aware walk must
    /// find them by navigation alone and never touch the exhaustive O(N) backstop.
    /// </summary>
    [Fact]
    public void FilteredSearch_WithAmpleMatches_NeverFallsBackToAnExhaustiveScan()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 16, max: 600, maxNeighbors: 16, maxLayers: 4,
            distance: DistanceFunction.Cosine);

        var rng = new Random(913_001);
        for (var i = 0; i < 500; i++)
            db.AddEntry(Vec.Random(16, rng), i % 4 == 0 ? "group=keep" : "group=skip");

        Assert.Equal(0, db.FilteredSearchFallbackCount);

        for (var q = 0; q < 25; q++)
        {
            var results = db.Search(Vec.Random(16, rng), m => m == "group=keep", topK: 10);
            Assert.Equal(10, results.Count);
            Assert.All(results, r => Assert.Equal("group=keep", r.Metadata));
        }

        Assert.Equal(0, db.FilteredSearchFallbackCount);
    }

    private static float Cosine(float[] left, float[] right)
    {
        float dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        var denominator = MathF.Sqrt(leftNorm) * MathF.Sqrt(rightNorm);
        return denominator == 0 ? 0 : dot / denominator;
    }

    private static List<(Guid Id, float[] Vector)> AddRandomEntries(QvecDatabase db, int count, int dim, Random rng)    {
        var entries = new List<(Guid Id, float[] Vector)>(count);
        for (var i = 0; i < count; i++)
        {
            var vector = Vec.Random(dim, rng);
            var stored = (float[])vector.Clone();
            var id = db.AddEntry(stored, $"item-{i:D3}");
            entries.Add((id, stored));
        }
        return entries;
    }
}
