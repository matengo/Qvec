using Qvec.Core;
using Qvec.Core.Quantization;
using Qvec.Core.Sync;

namespace Qvec.Core.Tests.Sync;

public class ApplyChangesTests
{
    private static readonly ChangeTrackingOptions Tracking = new() { LogCapacity = 256 };

    private static ChangeBatch Batch(QvecDatabase target, params ChangeItem[] items) => new()
    {
        SourceReplicaId = items.Length > 0 ? items[0].Version.Origin : Guid.NewGuid(),
        Dimension = target.VectorDimension,
        DistanceFunction = target.DistanceFunction,
        Payload = ChangePayloadKind.Float,
        FromSeq = 1,
        ToSeq = items.Length,
        HasMore = false,
        Items = items
    };

    private static ChangeItem Upsert(Guid id, EntryVersion version, float[] vector, string metadata) => new()
    {
        Type = ChangeType.Upsert, DocumentId = id, Version = version, Vector = vector, Metadata = metadata
    };

    private static ChangeItem Delete(Guid id, EntryVersion version) => new()
    {
        Type = ChangeType.Delete, DocumentId = id, Version = version
    };

    [Fact]
    public void Untracked_Throws()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        Assert.Throws<InvalidOperationException>(() => db.ApplyChanges(Batch(db)));
    }

    [Fact]
    public void NewDocument_IsApplied_WithRemoteVersion()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var peer = Guid.NewGuid();
        var id = Guid.NewGuid();
        var version = new EntryVersion(db.LastHlc + 5000, peer);

        var result = db.ApplyChanges(Batch(db, Upsert(id, version, Vec.Basis(8, 2), "remote")));

        Assert.Equal(1, result.Applied);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(ApplyOutcome.Applied, result.Items.Single().Outcome);
        var stored = db.GetByGuid(id);
        Assert.NotNull(stored);
        Assert.Equal("remote", stored!.Value.Metadata);
        Assert.Equal(Vec.Basis(8, 2), stored.Value.Vector);
        Assert.True(db.TryGetVersion(id, out var actual));
        Assert.Equal(version, actual);
        // The HLC must have jumped past the remote version so our next write is newer.
        Assert.True(db.LastHlc >= version.Hlc);
    }

    [Fact]
    public void Apply_AppendsToLocalLog_WithRemoteVersion()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var version = new EntryVersion(db.LastHlc + 1, Guid.NewGuid());
        var id = Guid.NewGuid();

        db.ApplyChanges(Batch(db, Upsert(id, version, Vec.Basis(8, 0), "x")));

        Assert.Equal(1, db.ChangeSeq);
        var item = Assert.Single(db.GetChanges(0, 10).Items);
        Assert.Equal(version, item.Version);
        Assert.Equal(id, item.DocumentId);
    }

    [Fact]
    public void SameBatchTwice_IsIdempotent()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = Guid.NewGuid();
        var batch = Batch(db, Upsert(id, new EntryVersion(db.LastHlc + 1, Guid.NewGuid()), Vec.Basis(8, 0), "x"));

        db.ApplyChanges(batch);
        var second = db.ApplyChanges(batch);

        Assert.Equal(0, second.Applied);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, db.LiveCount);
        Assert.Equal(0, db.DeletedCount);
        Assert.Equal(1, db.ChangeSeq);
    }

    [Fact]
    public void OwnChangeComingBack_IsSkipped()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "mine");
        db.TryGetVersion(id, out var mine);

        var result = db.ApplyChanges(Batch(db, Upsert(id, mine, Vec.Basis(8, 0), "mine")));

        Assert.Equal(ApplyOutcome.Skipped, result.Items.Single().Outcome);
        Assert.Equal(1, db.ChangeSeq);
    }

    [Fact]
    public void NewerRemoteUpsert_WinsOverLocal_AndKeepsGuid()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "local");
        var newer = new EntryVersion(db.LastHlc + 10, Guid.NewGuid());

        var result = db.ApplyChanges(Batch(db, Upsert(id, newer, Vec.Basis(8, 3), "remote")));

        Assert.Equal(ApplyOutcome.Applied, result.Items.Single().Outcome);
        Assert.Equal("remote", db.GetByGuid(id)!.Value.Metadata);
        Assert.Equal(Vec.Basis(8, 3), db.GetByGuid(id)!.Value.Vector);
        Assert.True(db.TryGetVersion(id, out var v));
        Assert.Equal(newer, v);
        Assert.Equal(1, db.LiveCount);
    }

    [Fact]
    public void OlderRemoteUpsert_LosesToLocal()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "local");
        db.TryGetVersion(id, out var mine);
        var older = new EntryVersion(mine.Hlc - 1, Guid.NewGuid());

        var result = db.ApplyChanges(Batch(db, Upsert(id, older, Vec.Basis(8, 3), "remote")));

        Assert.Equal(ApplyOutcome.Skipped, result.Items.Single().Outcome);
        Assert.Equal("local", db.GetByGuid(id)!.Value.Metadata);
    }

    [Fact]
    public void EqualHlc_TieBreaksOnOrigin_Deterministically()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "local");
        db.TryGetVersion(id, out var mine);

        var lowOrigin = new Guid("00000000-0000-0000-0000-000000000001");
        var highOrigin = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
        Assert.NotEqual(mine.Origin, lowOrigin);
        Assert.NotEqual(mine.Origin, highOrigin);

        var low = db.ApplyChanges(Batch(db, Upsert(id, new EntryVersion(mine.Hlc, lowOrigin), Vec.Basis(8, 1), "low")));
        var high = db.ApplyChanges(Batch(db, Upsert(id, new EntryVersion(mine.Hlc, highOrigin), Vec.Basis(8, 2), "high")));

        Assert.Equal(mine.Origin.CompareTo(lowOrigin) < 0 ? ApplyOutcome.Applied : ApplyOutcome.Skipped, low.Items.Single().Outcome);
        Assert.Equal(ApplyOutcome.Applied, high.Items.Single().Outcome);
        Assert.Equal("high", db.GetByGuid(id)!.Value.Metadata);
    }

    [Fact]
    public void NewerRemoteDelete_RemovesLocalRow()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "local");

        var result = db.ApplyChanges(Batch(db, Delete(id, new EntryVersion(db.LastHlc + 1, Guid.NewGuid()))));

        Assert.Equal(ApplyOutcome.Applied, result.Items.Single().Outcome);
        Assert.Null(db.GetByGuid(id));
        Assert.Equal(1, db.DeletedCount);
        Assert.Equal(ChangeType.Delete, db.GetChanges(0, 10).Items.Single().Type);
    }

    [Fact]
    public void OlderRemoteDelete_IsSkipped()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "local");
        db.TryGetVersion(id, out var mine);

        var result = db.ApplyChanges(Batch(db, Delete(id, new EntryVersion(mine.Hlc - 1, Guid.NewGuid()))));

        Assert.Equal(ApplyOutcome.Skipped, result.Items.Single().Outcome);
        Assert.NotNull(db.GetByGuid(id));
    }

    [Fact]
    public void DeleteWins_OverOlderUpsert()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "local");
        db.Delete(id);
        var tombstone = db.GetChanges(0, 10).Items.Single().Version;

        var result = db.ApplyChanges(Batch(db, Upsert(id, new EntryVersion(tombstone.Hlc - 1, Guid.NewGuid()), Vec.Basis(8, 1), "stale")));

        Assert.Equal(ApplyOutcome.Skipped, result.Items.Single().Outcome);
        Assert.Null(db.GetByGuid(id));
    }

    [Fact]
    public void UpsertWins_OverOlderDelete()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = db.AddEntry(Vec.Basis(8, 0), "local");
        db.Delete(id);

        var result = db.ApplyChanges(Batch(db, Upsert(id, new EntryVersion(db.LastHlc + 1, Guid.NewGuid()), Vec.Basis(8, 1), "fresh")));

        Assert.Equal(ApplyOutcome.Applied, result.Items.Single().Outcome);
        Assert.Equal("fresh", db.GetByGuid(id)!.Value.Metadata);
        Assert.Equal(ChangeType.Upsert, db.GetChanges(0, 10).Items.Single().Type);
    }

    [Fact]
    public void DeleteForUnknownDocument_IsAppliedAsTombstone()
    {
        // A peer may delete a document we never saw. Recording the tombstone matters: a later,
        // older upsert for that id from a third replica must still lose.
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var id = Guid.NewGuid();
        var tombstone = new EntryVersion(db.LastHlc + 10, Guid.NewGuid());

        var first = db.ApplyChanges(Batch(db, Delete(id, tombstone)));
        Assert.Equal(ApplyOutcome.Applied, first.Items.Single().Outcome);
        Assert.Equal(0, db.DeletedCount);
        Assert.Equal(1, db.ChangeSeq);

        var stale = db.ApplyChanges(Batch(db, Upsert(id, new EntryVersion(tombstone.Hlc - 1, Guid.NewGuid()), Vec.Basis(8, 0), "stale")));
        Assert.Equal(ApplyOutcome.Skipped, stale.Items.Single().Outcome);
        Assert.Null(db.GetByGuid(id));
    }

    [Fact]
    public void WrongDimension_IsRejected_PerItem_AndOthersStillApply()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var peer = Guid.NewGuid();
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var batch = new ChangeBatch
        {
            SourceReplicaId = peer,
            Dimension = 8,
            DistanceFunction = db.DistanceFunction,
            Payload = ChangePayloadKind.Float,
            FromSeq = 1,
            ToSeq = 2,
            HasMore = false,
            Items =
            [
                Upsert(bad, new EntryVersion(db.LastHlc + 1, peer), new float[4], "bad"),
                Upsert(good, new EntryVersion(db.LastHlc + 2, peer), Vec.Basis(8, 0), "good")
            ]
        };

        var result = db.ApplyChanges(batch);

        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.Rejected);
        var rejected = result.Items.Single(i => i.Outcome == ApplyOutcome.Rejected);
        Assert.Equal(bad, rejected.DocumentId);
        Assert.Contains("dimension", rejected.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(db.GetByGuid(good));
        Assert.Null(db.GetByGuid(bad));
    }

    [Fact]
    public void BatchDimensionMismatch_IsRejectedUpFront()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(dim: 8, changeTracking: Tracking);
        var batch = new ChangeBatch
        {
            SourceReplicaId = Guid.NewGuid(), Dimension = 16, DistanceFunction = db.DistanceFunction,
            Payload = ChangePayloadKind.Float, FromSeq = 1, ToSeq = 0, HasMore = false, Items = []
        };
        Assert.Throws<ArgumentException>(() => db.ApplyChanges(batch));
    }

    [Fact]
    public void Int8Payload_IntoFloatDatabase_IsRejected()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        var codes = new byte[8];
        var parameters = Int8Quantizer.Quantize(Vec.Basis(8, 0), codes);
        var batch = new ChangeBatch
        {
            SourceReplicaId = Guid.NewGuid(), Dimension = 8, DistanceFunction = db.DistanceFunction,
            Payload = ChangePayloadKind.Int8, FromSeq = 1, ToSeq = 1, HasMore = false,
            Items =
            [
                new ChangeItem
                {
                    Type = ChangeType.Upsert, DocumentId = Guid.NewGuid(),
                    Version = new EntryVersion(db.LastHlc + 1, Guid.NewGuid()),
                    Codes = codes, Parameters = parameters, Metadata = "q"
                }
            ]
        };

        var result = db.ApplyChanges(batch);

        Assert.Equal(1, result.Rejected);
        Assert.Contains("int8", result.Items.Single().Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, db.LiveCount);
    }

    [Fact]
    public void Int8Payload_IntoInt8Database_StoresExactCodes()
    {
        using var srcTmp = new TempDb();
        using var dstTmp = new TempDb();
        using var src = srcTmp.Open(quantization: VectorQuantization.Int8, changeTracking: Tracking);
        using var dst = dstTmp.Open(quantization: VectorQuantization.Int8, changeTracking: Tracking);
        var v = Vec.Random(8, new Random(9));
        var id = src.AddEntry(v, "q");

        var result = dst.ApplyChanges(src.GetChanges(0, 10));

        Assert.Equal(1, result.Applied);
        Assert.Equal(src.GetByGuid(id)!.Value.Vector, dst.GetByGuid(id)!.Value.Vector);
        src.TryGetVersion(id, out var sv);
        dst.TryGetVersion(id, out var dv);
        Assert.Equal(sv, dv);
    }

    [Fact]
    public void FloatPayload_IntoInt8Database_QuantisesLocally()
    {
        using var srcTmp = new TempDb();
        using var dstTmp = new TempDb();
        using var src = srcTmp.Open(changeTracking: Tracking);
        using var dst = dstTmp.Open(quantization: VectorQuantization.Int8, changeTracking: Tracking);
        var v = Vec.Random(8, new Random(9));
        var id = src.AddEntry(v, "f");

        var result = dst.ApplyChanges(src.GetChanges(0, 10));

        Assert.Equal(1, result.Applied);
        var stored = dst.GetByGuid(id)!.Value.Vector;
        for (int i = 0; i < 8; i++) Assert.InRange(stored[i], v[i] - 0.02f, v[i] + 0.02f);
    }

    [Fact]
    public void Apply_UpdatesFieldIndex_WhenExtractorIsSet()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        db.FieldIndexExtractor = meta => [("tag", meta)];
        var id = Guid.NewGuid();
        var peer = Guid.NewGuid();

        db.ApplyChanges(Batch(db, Upsert(id, new EntryVersion(db.LastHlc + 1, peer), Vec.Basis(8, 0), "red")));
        Assert.Single(db.GetIndexedCandidates([("tag", "red")])!);

        db.ApplyChanges(Batch(db, Upsert(id, new EntryVersion(db.LastHlc + 1, peer), Vec.Basis(8, 1), "blue")));
        Assert.Empty(db.GetIndexedCandidates([("tag", "red")])!);
        Assert.Single(db.GetIndexedCandidates([("tag", "blue")])!);

        db.ApplyChanges(Batch(db, Delete(id, new EntryVersion(db.LastHlc + 1, peer))));
        Assert.Empty(db.GetIndexedCandidates([("tag", "blue")])!);
    }

    [Fact]
    public void RebuildFieldIndex_RemembersExtractor()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking);
        Assert.Null(db.FieldIndexExtractor);
        db.RebuildFieldIndex(meta => [("tag", meta)]);
        Assert.NotNull(db.FieldIndexExtractor);
    }

    [Fact]
    public void Apply_SurvivesReopen()
    {
        using var tmp = new TempDb();
        var id = Guid.NewGuid();
        var version = new EntryVersion(0, Guid.NewGuid());
        using (var db = tmp.Open(changeTracking: Tracking))
        {
            version = new EntryVersion(db.LastHlc + 1, version.Origin);
            db.ApplyChanges(Batch(db, Upsert(id, version, Vec.Basis(8, 0), "x")));
        }

        using var reopened = QvecDatabase.Open(tmp.Path);
        Assert.True(reopened.IsHealthy());
        Assert.True(reopened.TryGetVersion(id, out var v));
        Assert.Equal(version, v);
        Assert.True(reopened.LastHlc >= version.Hlc);
    }

    [Fact]
    public void RoundTrip_TwoDatabases_ConvergeAfterOneExchange()
    {
        using var aTmp = new TempDb();
        using var bTmp = new TempDb();
        using var a = aTmp.Open(changeTracking: Tracking);
        using var b = bTmp.Open(changeTracking: Tracking);

        var a1 = a.AddEntry(Vec.Basis(8, 0), "a1");
        var a2 = a.AddEntry(Vec.Basis(8, 1), "a2");
        var b1 = b.AddEntry(Vec.Basis(8, 2), "b1");
        a.Delete(a2);

        b.ApplyChanges(a.GetChanges(0, 100));
        a.ApplyChanges(b.GetChanges(0, 100, excludeOrigin: a.ReplicaId));

        AssertSameContent(a, b);
        Assert.NotNull(a.GetByGuid(b1));
        Assert.NotNull(b.GetByGuid(a1));
        Assert.Null(b.GetByGuid(a2));
    }

    internal static void AssertSameContent(QvecDatabase x, QvecDatabase y)
    {
        Assert.Equal(Snapshot(x), Snapshot(y));

        static List<(Guid, EntryVersion, string, string)> Snapshot(QvecDatabase db)
        {
            var rows = new List<(Guid, EntryVersion, string, string)>();
            foreach (var id in db.GetChanges(0, int.MaxValue).Items.Select(i => i.DocumentId).Distinct())
            {
                var stored = db.GetByGuid(id);
                if (stored is null) continue;
                db.TryGetVersion(id, out var v);
                rows.Add((id, v, stored.Value.Metadata, string.Join(",", stored.Value.Vector)));
            }
            return rows.OrderBy(r => r.Item1).ToList();
        }
    }
}
