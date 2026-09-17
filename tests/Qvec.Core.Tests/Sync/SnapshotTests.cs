using System.Diagnostics;
using Qvec.Core;
using Qvec.Core.Format;
using Qvec.Core.Sync;

namespace Qvec.Core.Tests.Sync;

public class SnapshotTests
{
    private static ChangeTrackingOptions Tracking(long capacity = 64) => new() { LogCapacity = capacity };

    private static void Export(QvecDatabase source, string path, out SnapshotInfo info)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        info = source.ExportSnapshot(fs);
    }

    [Fact]
    public void Untracked_CannotExport()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        db.AddEntry(Vec.Basis(8, 0), "a");

        using var ms = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => db.ExportSnapshot(ms));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void ExportSnapshot_NullStream_Throws()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking());
        Assert.Throws<ArgumentNullException>(() => db.ExportSnapshot(null!));
    }

    [Fact]
    public void Snapshot_IsHealthyCopy_WithSourceIdentity()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking());
        var a = db.AddEntry(Vec.Basis(8, 0), "a");
        var b = db.AddEntry(Vec.Basis(8, 1), "b");
        db.UpdateMetadata(a, "a2");
        db.Delete(b);
        db.AddEntry(Vec.Basis(8, 2), "c");

        string copyPath = tmp.Sibling("copy.qvec");
        Export(db, copyPath, out var info);

        Assert.Equal(db.ReplicaId, info.SourceReplicaId);
        Assert.Equal(db.ChangeSeq, info.ChangeSeq);
        Assert.Equal(new FileInfo(copyPath).Length, info.Length);
        Assert.Equal(new FileInfo(tmp.Path).Length, info.Length);

        using var copy = QvecDatabase.Open(copyPath);
        Assert.True(copy.IsHealthy());
        Assert.True(copy.IsChangeTrackingEnabled);
        Assert.Equal(db.ReplicaId, copy.ReplicaId);
        Assert.Equal(db.ChangeSeq, copy.ChangeSeq);
        Assert.Equal(db.ChangeLogCount, copy.ChangeLogCount);
        Assert.Equal(db.LastHlc, copy.LastHlc);
        Assert.Equal(db.LiveCount, copy.LiveCount);
        ApplyChangesTests.AssertSameContent(db, copy);
    }

    [Fact]
    public void Snapshot_IsConsistent_WhenSourceKeepsWriting()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking());
        var a = db.AddEntry(Vec.Basis(8, 0), "a");

        string copyPath = tmp.Sibling("copy.qvec");
        Export(db, copyPath, out var info);

        db.AddEntry(Vec.Basis(8, 1), "b");
        db.Delete(a);

        using var copy = QvecDatabase.Open(copyPath);
        Assert.True(copy.IsHealthy());
        Assert.Equal(1, copy.ChangeSeq);
        Assert.Equal(info.ChangeSeq, copy.ChangeSeq);
        Assert.NotNull(copy.GetByGuid(a));
        Assert.Equal(1, copy.LiveCount);
    }

    [Fact]
    public void Snapshot_OfEmptyTrackedDatabase_Opens()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking());

        string copyPath = tmp.Sibling("copy.qvec");
        Export(db, copyPath, out var info);

        Assert.Equal(0, info.ChangeSeq);
        using var copy = QvecDatabase.Open(copyPath);
        Assert.True(copy.IsHealthy());
        Assert.Equal(0, copy.LiveCount);
    }

    [Fact]
    public void Snapshot_PreservesInt8Codes()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(quantization: VectorQuantization.Int8, changeTracking: Tracking());
        var rng = new Random(7);
        var ids = new List<Guid>();
        for (int i = 0; i < 10; i++) ids.Add(db.AddEntry(Vec.Random(8, rng), $"m{i}"));

        string copyPath = tmp.Sibling("copy.qvec");
        Export(db, copyPath, out _);

        using var copy = QvecDatabase.Open(copyPath);
        Assert.True(copy.IsHealthy());
        foreach (var id in ids)
            Assert.Equal(db.GetByGuid(id)!.Value.Vector, copy.GetByGuid(id)!.Value.Vector);
    }

    [Fact]
    public void Adopt_ChangesIdentity_KeepsVersionsAndRing()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking());
        var a = db.AddEntry(Vec.Basis(8, 0), "a");
        var b = db.AddEntry(Vec.Basis(8, 1), "b");
        db.Delete(b);
        Assert.True(db.TryGetVersion(a, out var versionA));

        string copyPath = tmp.Sibling("copy.qvec");
        Export(db, copyPath, out _);

        var newId = Guid.NewGuid();
        QvecDatabase.AdoptAsReplica(copyPath, newId, out var sourceId, out long sourceSeq);

        Assert.Equal(db.ReplicaId, sourceId);
        Assert.Equal(db.ChangeSeq, sourceSeq);

        using var replica = QvecDatabase.Open(copyPath);
        Assert.True(replica.IsHealthy());
        Assert.Equal(newId, replica.ReplicaId);
        Assert.Equal(db.ChangeSeq, replica.ChangeSeq);
        Assert.Equal(db.ChangeLogCount, replica.ChangeLogCount);
        Assert.Equal(db.LastHlc, replica.LastHlc);
        Assert.True(replica.TryGetVersion(a, out var replicaVersionA));
        Assert.Equal(versionA, replicaVersionA);
        Assert.Equal(db.ReplicaId, replicaVersionA.Origin);
        ApplyChangesTests.AssertSameContent(db, replica);

        // The adopted file's own tombstone knowledge survives: a stale upsert of b is refused.
        var stale = new ChangeBatch
        {
            SourceReplicaId = Guid.NewGuid(),
            Dimension = 8,
            DistanceFunction = replica.DistanceFunction,
            Payload = ChangePayloadKind.Float,
            FromSeq = 0,
            ToSeq = 1,
            HasMore = false,
            Items = [new ChangeItem { Type = ChangeType.Upsert, DocumentId = b, Version = new EntryVersion(1, Guid.NewGuid()), Vector = Vec.Basis(8, 1), Metadata = "old" }],
        };
        Assert.Equal(1, replica.ApplyChanges(stale).Skipped);
        Assert.Null(replica.GetByGuid(b));
    }

    [Fact]
    public void Adopt_NewLocalWrites_CarryNewOrigin()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking());
        db.AddEntry(Vec.Basis(8, 0), "a");

        string copyPath = tmp.Sibling("copy.qvec");
        Export(db, copyPath, out _);
        var newId = Guid.NewGuid();
        QvecDatabase.AdoptAsReplica(copyPath, newId, out _, out _);

        using var replica = QvecDatabase.Open(copyPath);
        var c = replica.AddEntry(Vec.Basis(8, 2), "c");
        Assert.True(replica.TryGetVersion(c, out var versionC));
        Assert.Equal(newId, versionC.Origin);
        Assert.True(versionC.Hlc > db.LastHlc);
    }

    [Fact]
    public void Adopt_RejectsBadArguments()
    {
        using var tmp = new TempDb();
        using (var db = tmp.Open(changeTracking: Tracking()))
        {
            db.AddEntry(Vec.Basis(8, 0), "a");
            Export(db, tmp.Sibling("copy.qvec"), out _);
        }

        Assert.Throws<ArgumentException>(() => QvecDatabase.AdoptAsReplica(tmp.Sibling("copy.qvec"), Guid.Empty, out _, out _));
        Assert.Throws<FileNotFoundException>(() => QvecDatabase.AdoptAsReplica(tmp.Sibling("missing.qvec"), Guid.NewGuid(), out _, out _));

        using (var untracked = new QvecDatabase(tmp.Sibling("plain.qvec"), 8, 10, 8, 3, DistanceFunction.DotProduct))
            untracked.AddEntry(Vec.Basis(8, 0), "a");
        Assert.Throws<QvecFormatException>(() => QvecDatabase.AdoptAsReplica(tmp.Sibling("plain.qvec"), Guid.NewGuid(), out _, out _));

        // Adopting into the same identity would create two replicas that skip each other's
        // writes as "their own", so it is refused.
        var copy = QvecDatabase.Open(tmp.Sibling("copy.qvec"));
        var sameId = copy.ReplicaId;
        copy.Dispose();
        Assert.Throws<ArgumentException>(() => QvecDatabase.AdoptAsReplica(tmp.Sibling("copy.qvec"), sameId, out _, out _));
    }

    [Fact]
    public void Adopt_WhileOpen_Throws()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(changeTracking: Tracking());
        Assert.Throws<IOException>(() => QvecDatabase.AdoptAsReplica(tmp.Path, Guid.NewGuid(), out _, out _));
    }

    [Fact]
    public void Adopt_IsIdempotentOnFailure_FileStaysOpenable()
    {
        using var tmp = new TempDb();
        using (var db = tmp.Open(changeTracking: Tracking()))
        {
            db.AddEntry(Vec.Basis(8, 0), "a");
            Export(db, tmp.Sibling("copy.qvec"), out _);
        }

        Assert.Throws<ArgumentException>(() => QvecDatabase.AdoptAsReplica(tmp.Sibling("copy.qvec"), Guid.Empty, out _, out _));
        using var copy = QvecDatabase.Open(tmp.Sibling("copy.qvec"));
        Assert.True(copy.IsHealthy());
    }

    [Fact]
    public void Delta_FromSourceSeq_ConvergesWithoutDuplicates()
    {
        using var tmp = new TempDb();
        using var source = tmp.Open(changeTracking: Tracking());
        var ids = new List<Guid>();
        for (int i = 0; i < 5; i++) ids.Add(source.AddEntry(Vec.Basis(8, i), $"m{i}"));

        string copyPath = tmp.Sibling("copy.qvec");
        Export(source, copyPath, out _);
        QvecDatabase.AdoptAsReplica(copyPath, Guid.NewGuid(), out var sourceId, out long cursor);

        // Source keeps working after the snapshot was taken.
        source.Delete(ids[0]);
        source.UpdateMetadata(ids[1], "changed");
        var f = source.AddEntry(Vec.Basis(8, 6), "f");

        using var replica = QvecDatabase.Open(copyPath);
        var delta = source.GetChanges(cursor, 100, excludeOrigin: replica.ReplicaId);
        Assert.Equal(sourceId, delta.SourceReplicaId);
        Assert.Equal(3, delta.Items.Count);

        var result = replica.ApplyChanges(delta);
        Assert.Equal(3, result.Applied);
        Assert.Equal(0, result.Rejected);

        Assert.Equal(source.LiveCount, replica.LiveCount);
        Assert.Null(replica.GetByGuid(ids[0]));
        Assert.Equal("changed", replica.GetByGuid(ids[1])!.Value.Metadata);
        Assert.NotNull(replica.GetByGuid(f));
        ApplyChangesTests.AssertSameContent(source, replica);

        // Nothing older than the cursor is ever re-sent, and the replica's pull is stable.
        var again = source.GetChanges(delta.ToSeq, 100, excludeOrigin: replica.ReplicaId);
        Assert.Empty(again.Items);
        Assert.False(again.HasMore);
    }

    [Fact]
    public void Delta_BothDirections_AfterBootstrap()
    {
        using var tmp = new TempDb();
        using var source = tmp.Open(changeTracking: Tracking());
        var a = source.AddEntry(Vec.Basis(8, 0), "a");

        string copyPath = tmp.Sibling("copy.qvec");
        Export(source, copyPath, out _);
        QvecDatabase.AdoptAsReplica(copyPath, Guid.NewGuid(), out _, out long cursor);
        using var replica = QvecDatabase.Open(copyPath);

        var b = replica.AddEntry(Vec.Basis(8, 1), "b");
        source.UpdateMetadata(a, "a2");

        // Replica → source: the snapshot rows share the source's origin, so nothing before
        // `cursor` flows back, and the replica's cursor for the source is the adopted seq.
        var toSource = replica.GetChanges(cursor, 100, excludeOrigin: source.ReplicaId);
        Assert.Single(toSource.Items);
        Assert.Equal(b, toSource.Items[0].DocumentId);
        Assert.Equal(1, source.ApplyChanges(toSource).Applied);

        var toReplica = source.GetChanges(cursor, 100, excludeOrigin: replica.ReplicaId);
        Assert.Single(toReplica.Items);
        Assert.Equal(a, toReplica.Items[0].DocumentId);
        Assert.Equal(1, replica.ApplyChanges(toReplica).Applied);

        ApplyChangesTests.AssertSameContent(source, replica);
    }

    [Fact]
    [Trait("Category", TestCategories.Slow)]
    public void Bootstrap_IsAFileCopy_NotAReindex()
    {
        const int dim = 32;
        const int n = 60_000;
        using var tmp = new TempDb();
        var rng = new Random(11);

        var build = Stopwatch.StartNew();
        using var source = tmp.Open(dim: dim, max: n, maxNeighbors: 16, changeTracking: Tracking(n));
        var inserts = new List<QvecInsert>(n);
        for (int i = 0; i < n; i++) inserts.Add(new QvecInsert(Vec.Random(dim, rng), $"m{i}"));
        source.AddEntries(inserts);
        build.Stop();

        string copyPath = tmp.Sibling("copy.qvec");
        var bootstrap = Stopwatch.StartNew();
        Export(source, copyPath, out var info);
        QvecDatabase.AdoptAsReplica(copyPath, Guid.NewGuid(), out _, out long seq);
        using var replica = QvecDatabase.Open(copyPath);
        bootstrap.Stop();

        Assert.Equal(n, info.ChangeSeq);
        Assert.Equal(n, seq);
        Assert.Equal(n, replica.LiveCount);
        Assert.True(replica.IsHealthy());
        Assert.True(bootstrap.Elapsed < build.Elapsed / 4,
            $"bootstrap {bootstrap.ElapsedMilliseconds} ms should be far cheaper than indexing {build.ElapsedMilliseconds} ms");
    }
}
