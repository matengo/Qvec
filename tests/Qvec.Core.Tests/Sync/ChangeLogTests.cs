using Qvec.Core;
using Qvec.Core.Format;
using Qvec.Core.Sync;

namespace Qvec.Core.Tests.Sync;

public class ChangeLogTests
{
    private static ChangeTrackingOptions Tracking(long capacity) => new() { LogCapacity = capacity };

    [Fact]
    public void Untracked_HasNoLog()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        db.AddEntry(Vec.Basis(8, 0), "a");

        Assert.Equal(0, db.ChangeSeq);
        Assert.Equal(0, db.ChangeLogCount);
        Assert.Throws<InvalidOperationException>(() => db.GetChanges(0, 10));
    }

    [Fact]
    public void Tracked_EmptyDatabase_HasSeqZero()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking(16));

        Assert.Equal(0, db.ChangeSeq);
        Assert.Equal(0, db.ChangeLogCount);
        Assert.Equal(1, db.OldestChangeSeq);
        Assert.Equal(16, db.ChangeLogCapacity);
    }

    [Fact]
    public void EveryLocalMutation_AppendsOneRecord()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking(64));

        var a = db.AddEntry(Vec.Basis(8, 0), "a");            // 1
        var b = db.AddEntry(Vec.Basis(8, 1), "b");            // 2
        db.UpdateMetadata(a, "a2");                            // 3
        db.UpdateVector(b, Vec.Basis(8, 2));                   // 4
        db.Update(a, Vec.Basis(8, 3), "a3");                   // 5
        db.Update(b, null, "b2");                              // 6
        db.Delete(a);                                          // 7
        db.AddEntries([new QvecInsert(Vec.Basis(8, 4), "c"), new QvecInsert(Vec.Basis(8, 5), "d")]); // 8, 9

        Assert.Equal(9, db.ChangeSeq);
        Assert.Equal(9, db.ChangeLogCount);
        Assert.Equal(1, db.OldestChangeSeq);
    }

    [Fact]
    public void Records_CarryRowVersion_AndType()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking(64));
        var a = db.AddEntry(Vec.Basis(8, 0), "a");
        db.TryGetVersion(a, out var version);
        db.Delete(a);

        var batch = db.GetChanges(0, 100);
        var item = Assert.Single(batch.Items);
        Assert.Equal(ChangeType.Delete, item.Type);
        Assert.Equal(a, item.DocumentId);
        Assert.True(item.Version > version, "the tombstone must be newer than the row it replaced");
        Assert.Equal(db.ReplicaId, item.Version.Origin);
    }

    [Fact]
    public void Ring_Rotates_WhenFull()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking(4));
        var ids = new List<Guid>();
        for (int i = 0; i < 10; i++) ids.Add(db.AddEntry(Vec.Basis(8, i % 8), $"m{i}"));

        Assert.Equal(10, db.ChangeSeq);
        Assert.Equal(4, db.ChangeLogCount);
        Assert.Equal(7, db.OldestChangeSeq);

        // Cursor at 6 (= oldest - 1) is the last one still serviceable.
        var batch = db.GetChanges(6, 100);
        Assert.Equal(7, batch.FromSeq);
        Assert.Equal(10, batch.ToSeq);
        Assert.Equal(ids.Skip(6).ToHashSet(), batch.Items.Select(i => i.DocumentId).ToHashSet());

        var ex = Assert.Throws<SyncCursorTooOldException>(() => db.GetChanges(5, 100));
        Assert.Equal(5, ex.RequestedSinceSeq);
        Assert.Equal(7, ex.OldestAvailableSeq);
    }

    [Fact]
    public void Log_SurvivesReopen()
    {
        using var tmp = new TempDb();
        Guid a, b;
        using (var db = tmp.Open(changeTracking: Tracking(16)))
        {
            a = db.AddEntry(Vec.Basis(8, 0), "a");
            b = db.AddEntry(Vec.Basis(8, 1), "b");
            db.Delete(a);
        }

        using var reopened = QvecDatabase.Open(tmp.Path);
        Assert.Equal(3, reopened.ChangeSeq);
        Assert.Equal(3, reopened.ChangeLogCount);
        var batch = reopened.GetChanges(0, 100);
        Assert.Equal(2, batch.Items.Count);
        Assert.Equal(ChangeType.Delete, batch.Items.Single(i => i.DocumentId == a).Type);
        Assert.Equal(ChangeType.Upsert, batch.Items.Single(i => i.DocumentId == b).Type);
    }

    [Fact]
    public void RecordBeyondCount_IsIgnored_AfterSimulatedCrash()
    {
        // A record is written before the header that publishes it. If the process dies in
        // between, the record's bytes are on disk but ChangeSeq/ChangeLogCount do not include it.
        using var tmp = new TempDb();
        Guid a;
        using (var db = tmp.Open(changeTracking: Tracking(16)))
        {
            a = db.AddEntry(Vec.Basis(8, 0), "a");
            db.AddEntry(Vec.Basis(8, 1), "b");
        }

        var bytes = File.ReadAllBytes(tmp.Path);
        var header = V4Header.Read(bytes, bytes.Length);
        var log = header.GetRequiredSection(V4SectionIds.ChangeLog);

        // Forge a Delete record for 'a' in slot 2 (the next free slot) without publishing it.
        long slot = log.Offset + 2 * V4Header.ChangeRecordSize;
        BitConverter.GetBytes(3L).CopyTo(bytes, (int)slot);                    // Seq
        BitConverter.GetBytes(long.MaxValue).CopyTo(bytes, (int)slot + 8);     // Hlc
        a.TryWriteBytes(bytes.AsSpan((int)slot + 16, 16));                     // DocumentId
        header.ReplicaId.TryWriteBytes(bytes.AsSpan((int)slot + 32, 16));      // Origin
        bytes[(int)slot + 48] = (byte)ChangeType.Delete;
        File.WriteAllBytes(tmp.Path, bytes);

        using var reopened = QvecDatabase.Open(tmp.Path);
        Assert.Equal(2, reopened.ChangeSeq);
        Assert.Equal(2, reopened.ChangeLogCount);
        Assert.NotNull(reopened.GetByGuid(a));
        Assert.True(reopened.TryGetVersion(a, out _));
        Assert.All(reopened.GetChanges(0, 100).Items, i => Assert.Equal(ChangeType.Upsert, i.Type));
    }

    [Fact]
    public void Grow_PacksRing_AndKeepsEveryRecord()
    {
        using var tmp = new TempDb();
        // Tiny MaxCount so the third insert has to grow; small capacity so the ring has wrapped.
        using var db = tmp.Open(max: 2, changeTracking: Tracking(3));
        var ids = new List<Guid>();
        for (int i = 0; i < 6; i++) ids.Add(db.AddEntry(Vec.Basis(8, i), $"m{i}"));
        db.Delete(ids[0]);

        Assert.True(db.MaxCount > 2);
        Assert.True(db.ChangeLogCapacity >= 3);
        Assert.Equal(7, db.ChangeSeq);

        long oldest = db.OldestChangeSeq;
        var batch = db.GetChanges(oldest - 1, 100);
        Assert.Equal(oldest, batch.FromSeq);
        Assert.Equal(7, batch.ToSeq);
        Assert.Contains(batch.Items, i => i.DocumentId == ids[0] && i.Type == ChangeType.Delete);

        // Seq numbers inside the ring must still be contiguous after the repack.
        var seqs = new List<long>();
        for (long s = oldest; s <= db.ChangeSeq; s++)
        {
            var one = db.GetChanges(s - 1, 1);
            Assert.Equal(s, one.FromSeq);
            seqs.Add(one.ToSeq);
        }
        Assert.Equal(Enumerable.Range((int)oldest, (int)(db.ChangeSeq - oldest + 1)).Select(x => (long)x), seqs);
    }

    [Fact]
    public void Vacuum_KeepsRing_AndSeq()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking(32));
        var a = db.AddEntry(Vec.Basis(8, 0), "a");
        var b = db.AddEntry(Vec.Basis(8, 1), "b");
        db.Delete(a);
        long seq = db.ChangeSeq;
        long count = db.ChangeLogCount;
        var before = db.GetChanges(0, 100);

        db.Vacuum();

        Assert.Equal(seq, db.ChangeSeq);
        Assert.Equal(count, db.ChangeLogCount);
        var after = db.GetChanges(0, 100);
        Assert.Equal(
            before.Items.Select(i => (i.DocumentId, i.Type, i.Version)).OrderBy(x => x.DocumentId),
            after.Items.Select(i => (i.DocumentId, i.Type, i.Version)).OrderBy(x => x.DocumentId));
        Assert.Equal(ChangeType.Delete, after.Items.Single(i => i.DocumentId == a).Type);
        Assert.Null(db.GetByGuid(a));
        Assert.NotNull(db.GetByGuid(b));
    }

    [Fact]
    public void EnableChangeTracking_WritesOneUpsertPerLiveRow()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var ids = new List<Guid>();
        for (int i = 0; i < 5; i++) ids.Add(db.AddEntry(Vec.Basis(8, i), $"m{i}"));
        db.Delete(ids[1]);

        db.EnableChangeTracking();

        Assert.Equal(4, db.ChangeSeq);
        var batch = db.GetChanges(0, 100);
        Assert.Equal(4, batch.Items.Count);
        Assert.All(batch.Items, i => Assert.Equal(ChangeType.Upsert, i.Type));
        Assert.DoesNotContain(batch.Items, i => i.DocumentId == ids[1]);
    }

    [Fact]
    public void DeletedVersions_AreRebuiltFromLog_OnOpen()
    {
        using var tmp = new TempDb();
        Guid a;
        EntryVersion tombstone;
        using (var db = tmp.Open(changeTracking: Tracking(16)))
        {
            a = db.AddEntry(Vec.Basis(8, 0), "a");
            db.Delete(a);
            tombstone = db.GetChanges(0, 10).Items.Single().Version;
        }

        using var reopened = QvecDatabase.Open(tmp.Path);
        // An older remote upsert for the deleted document must lose against the tombstone, which
        // is only possible if the tombstone's version was recovered from the ring.
        var older = new EntryVersion(tombstone.Hlc - 1, Guid.NewGuid());
        var result = reopened.ApplyChanges(RemoteBatch(reopened, older, a, "resurrect"));
        Assert.Equal(ApplyOutcome.Skipped, result.Items.Single().Outcome);
        Assert.Null(reopened.GetByGuid(a));
    }

    internal static ChangeBatch RemoteBatch(QvecDatabase target, EntryVersion version, Guid id, string metadata) => new()
    {
        SourceReplicaId = version.Origin,
        Dimension = target.VectorDimension,
        DistanceFunction = target.DistanceFunction,
        Payload = ChangePayloadKind.Float,
        FromSeq = 1,
        ToSeq = 1,
        HasMore = false,
        Items =
        [
            new ChangeItem { Type = ChangeType.Upsert, DocumentId = id, Version = version, Vector = Vec.Basis(target.VectorDimension, 1), Metadata = metadata }
        ]
    };
}
