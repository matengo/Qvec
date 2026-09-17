using Qvec.Core;
using Qvec.Core.Sync;

namespace Qvec.Core.Tests.Sync;

public class GetChangesTests
{
    private static readonly ChangeTrackingOptions Tracking = new() { LogCapacity = 64 };

    [Fact]
    public void Coalesces_RepeatedUpdates_IntoOneUpsertWithLatestState()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var a = db.AddEntry(Vec.Basis(8, 0), "v1");
        db.UpdateMetadata(a, "v2");
        db.UpdateMetadata(a, "v3");
        db.UpdateVector(a, Vec.Basis(8, 5));

        var batch = db.GetChanges(0, 100);

        Assert.Equal(1, batch.FromSeq);
        Assert.Equal(4, batch.ToSeq);
        Assert.False(batch.HasMore);
        var item = Assert.Single(batch.Items);
        Assert.Equal(ChangeType.Upsert, item.Type);
        Assert.Equal("v3", item.Metadata);
        Assert.Equal(Vec.Basis(8, 5), item.Vector);
        db.TryGetVersion(a, out var current);
        Assert.Equal(current, item.Version);
    }

    [Fact]
    public void DeleteAfterUpsert_YieldsOnlyDelete()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var a = db.AddEntry(Vec.Basis(8, 0), "a");
        db.UpdateMetadata(a, "a2");
        db.Delete(a);

        var item = Assert.Single(db.GetChanges(0, 100).Items);
        Assert.Equal(ChangeType.Delete, item.Type);
        Assert.Null(item.Vector);
        Assert.Null(item.Metadata);
    }

    [Fact]
    public void ReaddedAfterDelete_YieldsUpsert()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var a = db.AddEntry(Vec.Basis(8, 0), "a");
        db.Delete(a);
        db.AddEntry(Vec.Basis(8, 1), "again", a);

        var item = Assert.Single(db.GetChanges(0, 100).Items);
        Assert.Equal(ChangeType.Upsert, item.Type);
        Assert.Equal("again", item.Metadata);
    }

    [Fact]
    public void Cursor_ReturnsOnlyNewerRecords()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var a = db.AddEntry(Vec.Basis(8, 0), "a");
        var b = db.AddEntry(Vec.Basis(8, 1), "b");
        long cursor = db.ChangeSeq;
        var c = db.AddEntry(Vec.Basis(8, 2), "c");
        db.UpdateMetadata(a, "a2");

        var batch = db.GetChanges(cursor, 100);
        Assert.Equal(cursor + 1, batch.FromSeq);
        Assert.Equal(4, batch.ToSeq);
        Assert.Equal(new[] { a, c }.ToHashSet(), batch.Items.Select(i => i.DocumentId).ToHashSet());
        Assert.DoesNotContain(batch.Items, i => i.DocumentId == b);
    }

    [Fact]
    public void CursorAtHead_ReturnsEmptyBatch()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        db.AddEntry(Vec.Basis(8, 0), "a");

        var batch = db.GetChanges(db.ChangeSeq, 100);
        Assert.Empty(batch.Items);
        Assert.Equal(db.ChangeSeq, batch.ToSeq);
        Assert.False(batch.HasMore);
    }

    [Fact]
    public void MaxItems_LimitsDistinctDocuments_AndReportsHasMore()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var ids = new List<Guid>();
        for (int i = 0; i < 5; i++) ids.Add(db.AddEntry(Vec.Basis(8, i), $"m{i}"));

        var first = db.GetChanges(0, 2);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, first.ToSeq);
        Assert.True(first.HasMore);

        var second = db.GetChanges(first.ToSeq, 2);
        Assert.Equal(2, second.Items.Count);
        Assert.Equal(4, second.ToSeq);
        Assert.True(second.HasMore);

        var third = db.GetChanges(second.ToSeq, 2);
        Assert.Single(third.Items);
        Assert.Equal(5, third.ToSeq);
        Assert.False(third.HasMore);

        var all = first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.DocumentId).ToHashSet();
        Assert.Equal(ids.ToHashSet(), all);
    }

    [Fact]
    public void ExcludeOrigin_DropsDocumentsWhoseCurrentVersionCameFromThatReplica()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var mine = db.AddEntry(Vec.Basis(8, 0), "mine");

        var peer = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        db.ApplyChanges(ChangeLogTests.RemoteBatch(db, new EntryVersion(db.LastHlc + 1000, peer), theirs, "theirs"));
        Assert.Equal(2, db.ChangeSeq);

        var forPeer = db.GetChanges(0, 100, excludeOrigin: peer);
        Assert.Equal(mine, Assert.Single(forPeer.Items).DocumentId);
        Assert.Equal(2, forPeer.ToSeq);

        var forOthers = db.GetChanges(0, 100);
        Assert.Equal(2, forOthers.Items.Count);
    }

    [Fact]
    public void Batch_CarriesSourceIdentityAndShape()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(dim: 12, distance: DistanceFunction.Cosine, changeTracking: Tracking);
        db.AddEntry(Vec.Random(12, new Random(3)), "a");

        var batch = db.GetChanges(0, 10);
        Assert.Equal(db.ReplicaId, batch.SourceReplicaId);
        Assert.Equal(12, batch.Dimension);
        Assert.Equal(DistanceFunction.Cosine, batch.DistanceFunction);
        Assert.Equal(ChangePayloadKind.Float, batch.Payload);
    }

    [Fact]
    public void Int8Rescored_SendsFloats()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(quantization: VectorQuantization.Int8Rescored, changeTracking: Tracking);
        var v = Vec.Random(8, new Random(4));
        db.AddEntry(v, "a");

        var batch = db.GetChanges(0, 10);
        Assert.Equal(ChangePayloadKind.Float, batch.Payload);
        var item = Assert.Single(batch.Items);
        Assert.NotNull(item.Vector);
        Assert.Null(item.Codes);
    }

    [Fact]
    public void PureInt8_SendsCodesAndParameters()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(quantization: VectorQuantization.Int8, changeTracking: Tracking);
        db.AddEntry(Vec.Random(8, new Random(4)), "a");

        var batch = db.GetChanges(0, 10);
        Assert.Equal(ChangePayloadKind.Int8, batch.Payload);
        var item = Assert.Single(batch.Items);
        Assert.Null(item.Vector);
        Assert.NotNull(item.Codes);
        Assert.Equal(8, item.Codes!.Length);
        Assert.NotNull(item.Parameters);
    }

    [Fact]
    public void NegativeCursor_IsRejected()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        Assert.Throws<ArgumentOutOfRangeException>(() => db.GetChanges(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => db.GetChanges(0, 0));
    }

    [Fact]
    public void CursorAheadOfHead_IsRejected()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        db.AddEntry(Vec.Basis(8, 0), "a");
        Assert.Throws<ArgumentOutOfRangeException>(() => db.GetChanges(db.ChangeSeq + 1, 10));
    }
}
