using Qvec.Core;

namespace Qvec.Core.Tests;

public class CrudTests
{
    public static IEnumerable<object[]> DistanceFunctions()
    {
        yield return new object[] { DistanceFunction.DotProduct };
        yield return new object[] { DistanceFunction.Cosine };
    }

    [Theory]
    [MemberData(nameof(DistanceFunctions))]
    public void AddEntry_ThenGetByGuid_ReturnsStoredVectorAndMetadata(DistanceFunction distance)
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 10, maxNeighbors: 8, maxLayers: 1, distance: distance);
        var original = new[] { 3f, 0f, 4f, 0f };

        var id = db.AddEntry((float[])original.Clone(), """{"name":"alpha"}""");
        var entry = db.GetByGuid(id);

        Assert.NotNull(entry);
        Assert.Equal("""{"name":"alpha"}""", entry.Value.Metadata);
        if (distance == DistanceFunction.Cosine)
        {
            // Cosine storage normalizes vectors, so assert same direction and unit length instead of bit equality.
            AssertVectorClose(Normalized(original), entry.Value.Vector);
            Assert.Equal(1f, Norm(entry.Value.Vector), precision: 6);
        }
        else
        {
            Assert.Equal(original, entry.Value.Vector);
        }
    }

    [Fact]
    public void AddEntry_WithDuplicateExternalId_IsIdempotentAndDoesNotOverwriteExistingRow()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 3, maxNeighbors: 2, maxLayers: 1);
        var externalId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var first = db.AddEntry(Vec.Basis(4, 0), "category=one", externalId);
        var second = db.AddEntry(Vec.Basis(4, 1), "category=two", externalId);

        Assert.Equal(externalId, first);
        Assert.Equal(externalId, second);
        Assert.Equal(1, db.LiveCount);
        var entry = db.GetByGuid(externalId);
        Assert.NotNull(entry);
        Assert.Equal(Vec.Basis(4, 0), entry.Value.Vector);
        Assert.Equal("category=one", entry.Value.Metadata);
    }

    [Fact]
    public void Delete_RemovesEntryFromReadsSearchAndCounts()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 5, maxNeighbors: 4, maxLayers: 1);
        var deleteId = db.AddEntry(Vec.Basis(4, 0), "delete-me");
        var keepId = db.AddEntry(Vec.Basis(4, 1), "keep-me");

        Assert.True(db.Delete(deleteId));
        Assert.False(db.Delete(deleteId));
        Assert.False(db.Delete(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")));
        Assert.Null(db.GetByGuid(deleteId));
        Assert.Equal(1, db.LiveCount);
        Assert.Equal(1, db.DeletedCount);

        var results = db.Search(Vec.Basis(4, 0), topK: 5, efSearch: 10);
        Assert.DoesNotContain(results, r => r.Id == deleteId);
        Assert.Contains(results, r => r.Id == keepId);
    }

    [Fact]
    public void UpdateMetadata_ChangesMetadataOnlyAndKeepsSearchPosition()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 5, maxNeighbors: 4, maxLayers: 1);
        var vector = Vec.Basis(4, 2);
        var id = db.AddEntry(vector, "old");
        var deleted = db.AddEntry(Vec.Basis(4, 3), "deleted");
        Assert.True(db.Delete(deleted));

        Assert.True(db.UpdateMetadata(id, "new"));
        var entry = db.GetByGuid(id);
        Assert.NotNull(entry);
        Assert.Equal(id, db.Search(vector, topK: 1, efSearch: 10)[0].Id);
        Assert.Equal(vector, entry.Value.Vector);
        Assert.Equal("new", entry.Value.Metadata);
        Assert.False(db.UpdateMetadata(deleted, "resurrected"));
        Assert.False(db.UpdateMetadata(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "missing"));
        Assert.Null(db.GetByGuid(deleted));
    }

    [Fact]
    public void UpdateVector_ChangesVectorPreservesGuidMetadataAndMovesSearchResult()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 10, maxNeighbors: 8, maxLayers: 1);
        var id = db.AddEntry(Vec.Basis(4, 0), "moving");
        var anchorA = db.AddEntry(new[] { 0.9f, 0.1f, 0f, 0f }, "anchor-a");
        var anchorB = db.AddEntry(new[] { 0f, 0.9f, 0.1f, 0f }, "anchor-b");

        Assert.True(db.UpdateVector(id, Vec.Basis(4, 1)));

        var entry = db.GetByGuid(id);
        Assert.NotNull(entry);
        Assert.Equal(Vec.Basis(4, 1), entry.Value.Vector);
        Assert.Equal("moving", entry.Value.Metadata);
        Assert.Equal(id, db.Search(Vec.Basis(4, 1), topK: 1, efSearch: 10)[0].Id);
        Assert.NotEqual(id, db.Search(Vec.Basis(4, 0), topK: 1, efSearch: 10)[0].Id);
        Assert.Contains(db.Search(Vec.Basis(4, 0), topK: 2, efSearch: 10), r => r.Id == anchorA);
        Assert.Contains(db.Search(Vec.Basis(4, 1), topK: 2, efSearch: 10), r => r.Id == anchorB);
    }

    [Fact]
    public void Update_CanChangeVectorMetadataOrBothWhileKeepingEntryFindable()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 10, maxNeighbors: 8, maxLayers: 1);
        var id = db.AddEntry(Vec.Basis(4, 0), "original");

        Assert.True(db.Update(id, Vec.Basis(4, 1), null));
        var vectorOnly = db.GetByGuid(id);
        Assert.NotNull(vectorOnly);
        Assert.Equal(Vec.Basis(4, 1), vectorOnly.Value.Vector);
        Assert.Equal("original", vectorOnly.Value.Metadata);
        Assert.Equal(id, db.Search(Vec.Basis(4, 1), topK: 1, efSearch: 10)[0].Id);

        Assert.True(db.Update(id, null, "metadata-only"));
        var metadataOnly = db.GetByGuid(id);
        Assert.NotNull(metadataOnly);
        Assert.Equal(Vec.Basis(4, 1), metadataOnly.Value.Vector);
        Assert.Equal("metadata-only", metadataOnly.Value.Metadata);
        Assert.Equal(id, db.Search(Vec.Basis(4, 1), topK: 1, efSearch: 10)[0].Id);

        Assert.True(db.Update(id, Vec.Basis(4, 2), "both"));
        var both = db.GetByGuid(id);
        Assert.NotNull(both);
        Assert.Equal(Vec.Basis(4, 2), both.Value.Vector);
        Assert.Equal("both", both.Value.Metadata);
        Assert.Equal(id, db.Search(Vec.Basis(4, 2), topK: 1, efSearch: 10)[0].Id);
    }

    /// <summary>
    /// Deleted slots should be reusable. Currently additions after deleting every row
    /// still throw QvecFullException because capacity is checked against CurrentCount.
    /// </summary>
    [Fact]
    public void AddEntry_AfterDeletingAllEntries_ReusesTombstonedCapacity()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 3, maxNeighbors: 2, maxLayers: 1);
        var ids = Enumerable.Range(0, 3)
            .Select(i => db.AddEntry(Vec.Basis(4, i), $"old-{i}"))
            .ToArray();
        foreach (var id in ids)
            Assert.True(db.Delete(id));

        Assert.Equal(0, db.LiveCount);
        var newIds = Enumerable.Range(0, 3)
            .Select(i => db.AddEntry(Vec.Basis(4, i), $"new-{i}"))
            .ToArray();

        Assert.Equal(3, db.LiveCount);
        Assert.All(newIds, id => Assert.NotNull(db.GetByGuid(id)));
    }

    [Fact]
    public void Search_ReturnsOrderedUniqueLiveResultsWithoutPadding()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 8, maxNeighbors: 8, maxLayers: 1);
        var best = db.AddEntry(Vec.Basis(4, 0), "best");
        var deleted = db.AddEntry(Vec.Basis(4, 0), "deleted");
        var second = db.AddEntry(new[] { 0.8f, 0.2f, 0f, 0f }, "second");
        var third = db.AddEntry(new[] { 0.6f, 0.4f, 0f, 0f }, "third");
        Assert.True(db.Delete(deleted));

        var results = db.Search(Vec.Basis(4, 0), topK: 10, efSearch: 10);

        Assert.Equal(3, results.Count);
        Assert.Equal(results.Count, results.Select(r => r.Id).Distinct().Count());
        Assert.DoesNotContain(results, r => r.Id == deleted);
        Assert.Equal(new[] { best, second, third }, results.Select(r => r.Id).ToArray());
        Assert.True(results.Zip(results.Skip(1), (left, right) => left.Score >= right.Score).All(BooleanIdentity));
    }

    private static bool BooleanIdentity(bool value) => value;

    private static float[] Normalized(float[] vector)
    {
        var copy = (float[])vector.Clone();
        var norm = Norm(copy);
        for (var i = 0; i < copy.Length; i++)
            copy[i] /= norm;
        return copy;
    }

    private static float Norm(float[] vector) => MathF.Sqrt(vector.Sum(v => v * v));

    private static void AssertVectorClose(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i], precision: 6);
    }
}
