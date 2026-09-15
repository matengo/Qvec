using Qvec.Core;
using Qvec.Core.Format;

namespace Qvec.Core.Tests;

/// <summary>
/// Finding #20: <c>max</c> was a hard ceiling fixed at creation, so a database that filled up
/// simply stopped accepting entries and the only remedy was to rebuild it at a larger size. The
/// file now grows geometrically instead, and <c>max</c> is a starting size.
/// </summary>
public class GrowableFileTests
{
    [Fact]
    public void AddEntry_PastTheInitialCapacity_GrowsInsteadOfThrowing()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 4);

        var ids = new List<Guid>();
        for (int i = 0; i < 50; i++)
            ids.Add(db.AddEntry(VectorFor(i), $"{{\"seq\":{i}}}"));

        Assert.Equal(50, db.GetCount());
        Assert.True(db.MaxCount >= 50);
        Assert.True(db.IsHealthy());

        // Everything written before a grow must survive it intact.
        for (int i = 0; i < 50; i++)
            Assert.Equal($"{{\"seq\":{i}}}", db.GetByGuid(ids[i])?.Metadata);
    }

    [Fact]
    public void Grow_PreservesVectorsExactly()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 2, distance: DistanceFunction.Cosine);

        var ids = new List<Guid>();
        for (int i = 0; i < 40; i++)
            ids.Add(db.AddEntry(VectorFor(i), $"{{\"seq\":{i}}}"));

        // A vector stored before the first grow must still be its own nearest neighbour, which
        // only holds if the vector section was moved byte-for-byte.
        for (int i = 0; i < 40; i += 7)
        {
            var hits = db.Search(VectorFor(i), topK: 1);
            Assert.Single(hits);
            Assert.Equal(ids[i], hits[0].Id);
        }
    }

    [Fact]
    public void Grow_PreservesTheGraphSoSearchStillFindsOldEntries()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 16, max: 2, distance: DistanceFunction.Cosine);

        var ids = new List<Guid>();
        for (int i = 0; i < 200; i++)
            ids.Add(db.AddEntry(VectorFor(i, 16), $"{{\"seq\":{i}}}"));

        int found = 0;
        for (int i = 0; i < 200; i += 5)
        {
            var hits = db.Search(VectorFor(i, 16), topK: 1);
            if (hits.Count == 1 && hits[0].Id == ids[i]) found++;
        }

        // If the graph section were moved incorrectly, recall would collapse rather than dip.
        Assert.True(found >= 36, $"Expected near-perfect recall after repeated grows but found {found}/40.");
    }

    [Fact]
    public void Grow_PreservesTombstones()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 4);

        var ids = new List<Guid>();
        for (int i = 0; i < 4; i++)
            ids.Add(db.AddEntry(VectorFor(i), $"{{\"seq\":{i}}}"));

        Assert.True(db.Delete(ids[1]));
        int deletedBefore = db.DeletedCount;

        // Force several grows.
        for (int i = 4; i < 60; i++)
            db.AddEntry(VectorFor(i), $"{{\"seq\":{i}}}");

        Assert.Null(db.GetByGuid(ids[1]));
        Assert.Equal(deletedBefore - 1, db.DeletedCount); // the free slot was reused, not lost
        Assert.NotNull(db.GetByGuid(ids[0]));
        Assert.NotNull(db.GetByGuid(ids[2]));
    }

    [Fact]
    public void Grow_SurvivesReopenAndReportsTheNewCapacity()
    {
        using var temp = new TempDb();
        int grownMax;
        List<Guid> ids = [];

        using (var db = temp.Open(dim: 8, max: 4))
        {
            for (int i = 0; i < 40; i++)
                ids.Add(db.AddEntry(VectorFor(i), $"{{\"seq\":{i}}}"));
            grownMax = db.MaxCount;
        }

        using var reopened = QvecDatabase.Open(temp.Path);
        Assert.True(reopened.IsHealthy());
        Assert.Equal(grownMax, reopened.MaxCount);
        Assert.Equal(40, reopened.GetCount());
        for (int i = 0; i < 40; i++)
            Assert.Equal($"{{\"seq\":{i}}}", reopened.GetByGuid(ids[i])?.Metadata);
    }

    [Fact]
    public void MetadataHeap_GrowsWhenItFillsUp()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 8);

        var id = db.AddEntry(Vec.Basis(4, 0), "{\"v\":0}");
        string padding = new('x', 1024);

        // The heap starts at 64 KiB, so this needs several grows.
        for (int i = 1; i <= 400; i++)
            Assert.True(db.UpdateMetadata(id, $"{{\"v\":{i},\"pad\":\"{padding}\"}}"));

        Assert.Equal($"{{\"v\":400,\"pad\":\"{padding}\"}}", db.GetByGuid(id)?.Metadata);
        Assert.True(db.IsHealthy());
    }

    [Fact]
    public void MetadataHeap_AcceptsABlobLargerThanTheEntireHeap()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 8);

        // Larger than the 64 KiB starting heap, so geometric growth alone would not be enough:
        // the required size has to act as a floor.
        string huge = new('y', 256 * 1024);
        var id = db.AddEntry(Vec.Basis(4, 0), huge);

        Assert.Equal(huge, db.GetByGuid(id)?.Metadata);
        Assert.True(db.IsHealthy());
    }

    [Fact]
    public void AutoGrowDisabled_StillThrowsWhenFull()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 2);
        db.AutoGrow = false;

        db.AddEntry(Vec.Basis(4, 0), "{}");
        db.AddEntry(Vec.Basis(4, 1), "{}");

        var ex = Assert.Throws<QvecFullException>(() => db.AddEntry(Vec.Basis(4, 2), "{}"));
        Assert.Equal(2, ex.MaxCount);
    }

    [Fact]
    public void Grow_KeepsTheFileSparse()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 256, max: 2);

        for (int i = 0; i < 300; i++)
            db.AddEntry(VectorFor(i, 256), "{}");

        Assert.True(db.IsHealthy());
        Assert.Equal(300, db.GetCount());
    }

    [Fact]
    public void GrownLayout_MatchesADatabaseCreatedAtTheLargerSize()
    {
        var initial = QvecFormatLayout.CreateInitial(
            vectorDimension: 8, maxCount: 10, maxNeighbors: 4, maxLayers: 3,
            metadataHeapCapacity: 64 * 1024);

        var grown = QvecFormatLayout.CreateGrown(initial, newMaxCount: 100, newMetadataHeapCapacity: 128 * 1024);

        var direct = QvecFormatLayout.CreateInitial(
            vectorDimension: 8, maxCount: 100, maxNeighbors: 4, maxLayers: 3,
            metadataHeapCapacity: 128 * 1024);

        Assert.Equal(direct.FileLength, grown.FileLength);
        foreach (var expected in direct.Sections.Where(s => (s.SectionFlags & SectionFlags.Present) != 0))
        {
            var actual = grown.GetRequiredSection(expected.SectionId);
            Assert.Equal(expected.Offset, actual.Offset);
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected.ElementSize, actual.ElementSize);
        }
    }

    [Fact]
    public void CreateGrown_RefusesToShrink()
    {
        var initial = QvecFormatLayout.CreateInitial(
            vectorDimension: 8, maxCount: 100, maxNeighbors: 4, maxLayers: 3,
            metadataHeapCapacity: 128 * 1024);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => QvecFormatLayout.CreateGrown(initial, newMaxCount: 10, newMetadataHeapCapacity: 128 * 1024));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => QvecFormatLayout.CreateGrown(initial, newMaxCount: 100, newMetadataHeapCapacity: 1024));
    }

    [Fact]
    public void CreateGrown_CarriesMutableStateAcross()
    {
        var initial = QvecFormatLayout.CreateInitial(
            vectorDimension: 8, maxCount: 10, maxNeighbors: 4, maxLayers: 3,
            metadataHeapCapacity: 64 * 1024, distanceFunction: DistanceFunction.Cosine);
        initial.CurrentCount = 7;
        initial.DeletedCount = 2;
        initial.EntryPoint = 3;
        initial.EntryPointLevel = 1;
        initial.MetadataHeapUsed = 999;

        var grown = QvecFormatLayout.CreateGrown(initial, 100, 128 * 1024);

        Assert.Equal(7, grown.CurrentCount);
        Assert.Equal(2, grown.DeletedCount);
        Assert.Equal(3, grown.EntryPoint);
        Assert.Equal(1, grown.EntryPointLevel);
        Assert.Equal(999, grown.MetadataHeapUsed);
        Assert.Equal(DistanceFunction.Cosine, grown.DistanceFunction);
        Assert.Equal(initial.CreatedUnixTimeSeconds, grown.CreatedUnixTimeSeconds);
    }

    [Fact]
    public void RecommendGrownMaxCount_GrowsGeometricallyAndAlwaysMakesProgress()
    {
        Assert.Equal(1, QvecFormatLayout.RecommendGrownMaxCount(0));
        Assert.Equal(2, QvecFormatLayout.RecommendGrownMaxCount(1));
        Assert.Equal(150, QvecFormatLayout.RecommendGrownMaxCount(100));
    }

    /// <summary>
    /// Every public mutator must leave the header committed. A completed write that leaves
    /// <c>WriteInProgress</c> set makes <c>IsHealthy()</c> report a false negative, and a crash
    /// straight afterwards would make the next open reject a perfectly sound file.
    /// </summary>
    [Theory]
    [InlineData("UpdateMetadata")]
    [InlineData("UpdateVector")]
    [InlineData("UpdateBoth")]
    [InlineData("UpdateMetadataOnlyViaUpdate")]
    [InlineData("Delete")]
    [InlineData("AddEntry")]
    public void Mutators_LeaveTheHeaderCommitted(string operation)
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 16);
        var id = db.AddEntry(Vec.Basis(4, 0), "{\"v\":0}");

        switch (operation)
        {
            case "UpdateMetadata": Assert.True(db.UpdateMetadata(id, "{\"v\":1}")); break;
            case "UpdateVector": Assert.True(db.UpdateVector(id, Vec.Basis(4, 1))); break;
            case "UpdateBoth": Assert.True(db.Update(id, Vec.Basis(4, 2), "{\"v\":2}")); break;
            case "UpdateMetadataOnlyViaUpdate": Assert.True(db.Update(id, null, "{\"v\":3}")); break;
            case "Delete": Assert.True(db.Delete(id)); break;
            case "AddEntry": db.AddEntry(Vec.Basis(4, 3), "{\"v\":4}"); break;
        }

        Assert.True(db.IsHealthy(), $"{operation} left the header marked as a write in progress.");
    }

    private static float[] VectorFor(int seq, int dim = 8)
    {
        var vector = new float[dim];
        for (int i = 0; i < dim; i++)
            vector[i] = ((seq + 1) * (i + 3) % 97) / 97f;
        vector[seq % dim] += 2f;
        return vector;
    }
}
