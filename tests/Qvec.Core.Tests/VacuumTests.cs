using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// Deletes only tombstone a row, updates append a new metadata blob and orphan the old one, and
/// <c>UpdateVector</c> writes a whole new row. Findings #10 and #22 were that none of this space
/// was ever reclaimed and that the graph degraded as tombstones accumulated. <c>Vacuum()</c> is
/// the operation that repairs both.
/// </summary>
public class VacuumTests
{
    [Fact]
    public void Vacuum_AfterDeletes_ReclaimsRowsAndPreservesLiveEntries()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 8, max: 64);

        var ids = new List<Guid>();
        for (int i = 0; i < 40; i++)
            ids.Add(db.AddEntry(Vec.Basis(8, i % 8), $"{{\"seq\":{i}}}"));

        for (int i = 0; i < 40; i += 2)
            Assert.True(db.Delete(ids[i]));

        Assert.Equal(40, db.GetCount());
        Assert.Equal(20, db.DeletedCount);

        db.Vacuum();

        Assert.Equal(20, db.GetCount());
        Assert.Equal(0, db.DeletedCount);
        Assert.Equal(20, db.LiveCount);

        for (int i = 0; i < 40; i++)
        {
            var found = db.GetByGuid(ids[i]);
            if (i % 2 == 0) Assert.Null(found);
            else Assert.Equal($"{{\"seq\":{i}}}", found?.Metadata);
        }
    }

    [Fact]
    public void Vacuum_KeepsSearchWorkingAndTheDatabaseHealthy()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 16, max: 256, distance: DistanceFunction.Cosine);

        var all = new List<Guid>();
        for (int i = 0; i < 120; i++)
            all.Add(db.AddEntry(Vec.Basis(16, i % 16), $"{{\"seq\":{i}}}"));

        for (int i = 0; i < 120; i += 3)
            Assert.True(db.Delete(all[i]));

        db.Vacuum();

        Assert.True(db.IsHealthy());
        Assert.Equal(80, db.LiveCount);

        // The rebuilt graph must still be navigable: a query aimed straight at a surviving
        // basis vector has to find something, and nothing deleted may come back.
        var deleted = Enumerable.Range(0, 120).Where(i => i % 3 == 0).Select(i => all[i]).ToHashSet();
        var hits = db.Search(Vec.Basis(16, 1), topK: 10);
        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.DoesNotContain(hit.Id, deleted));
    }

    [Fact]
    public void Vacuum_ReclaimsMetadataHeapOrphanedByRepeatedUpdates()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 16);

        var id = db.AddEntry(Vec.Basis(4, 0), "{\"v\":0}");
        string padding = new('x', 512);

        // The heap is 64 KiB here, so ~100 updates of ~530 bytes fill roughly 80% of it.
        for (int i = 1; i <= 100; i++)
            Assert.True(db.UpdateMetadata(id, $"{{\"v\":{i},\"pad\":\"{padding}\"}}"));

        db.Vacuum();

        Assert.Equal($"{{\"v\":100,\"pad\":\"{padding}\"}}", db.GetByGuid(id)?.Metadata);
        Assert.Equal(1, db.LiveCount);

        // Without reclamation the orphaned blobs would still occupy the heap and this second
        // round -- which needs as much space again -- would throw QvecFullException.
        for (int i = 101; i <= 200; i++)
            Assert.True(db.UpdateMetadata(id, $"{{\"v\":{i},\"pad\":\"{padding}\"}}"));
    }

    [Fact]
    public void Vacuum_SurvivesReopen()
    {
        using var temp = new TempDb();
        Guid kept;
        using (var db = temp.Open(dim: 8, max: 32))
        {
            var dropped = db.AddEntry(Vec.Basis(8, 1), "{\"keep\":false}");
            kept = db.AddEntry(Vec.Basis(8, 2), "{\"keep\":true}");
            db.Delete(dropped);
            db.Vacuum();
        }

        using var reopened = temp.Open(dim: 8, max: 32);
        Assert.True(reopened.IsHealthy());
        Assert.Equal(1, reopened.GetCount());
        Assert.Equal("{\"keep\":true}", reopened.GetByGuid(kept)?.Metadata);
    }

    [Fact]
    public void Vacuum_LeavesNoTemporaryFileBehind()
    {
        using var temp = new TempDb();
        using var db = temp.Open(dim: 4, max: 8);
        db.AddEntry(Vec.Basis(4, 0), "{}");
        db.Vacuum();

        Assert.False(File.Exists(temp.Path + ".vacuum"));
    }
}
