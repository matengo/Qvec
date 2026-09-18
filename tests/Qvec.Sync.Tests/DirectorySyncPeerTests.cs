using Qvec.Core;
using Qvec.Core.Sync;

namespace Qvec.Sync.Tests;

public class DirectorySyncPeerTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static ChangeBatch Batch(Guid source, long from, long to, int items = 1)
    {
        var list = new List<ChangeItem>();
        for (int i = 0; i < items; i++)
        {
            list.Add(new ChangeItem
            {
                Type = ChangeType.Upsert, DocumentId = Guid.NewGuid(), Version = new EntryVersion(from + i, source),
                Vector = Vec.Basis(8, i % 8), Metadata = "m" + i,
            });
        }
        return new ChangeBatch
        {
            SourceReplicaId = source, Dimension = 8, DistanceFunction = DistanceFunction.DotProduct,
            Payload = ChangePayloadKind.Float, FromSeq = from, ToSeq = to, Items = list, HasMore = false,
        };
    }

    [Fact]
    public async Task Push_WritesSegmentAndManifest_WithoutLeavingTemp()
    {
        using var tmp = new TempDir();
        await using var peer = new DirectorySyncPeer(tmp.Dir("bus"));

        await peer.PushAsync(Batch(A, 1, 5), CancellationToken.None);

        string log = Path.Combine(peer.Root, "replicas", A.ToString("D"), "log");
        var files = Directory.GetFiles(log).Select(f => Path.GetFileName(f)).ToArray();
        Assert.Equal(["00000000000000000001-00000000000000000005.qvcb"], files);
        Assert.Empty(Directory.GetFiles(log, "*.tmp"));

        var manifest = peer.ReadManifest(A);
        Assert.NotNull(manifest);
        Assert.Equal(5, manifest.LatestSeq);
        Assert.Null(manifest.SnapshotSeq);
        Assert.Equal([A], peer.ListReplicas());
    }

    [Fact]
    public async Task Pull_SkipsOwnPrefix_ReturnsOthersInOrder_AndNullWhenCaughtUp()
    {
        using var tmp = new TempDir();
        await using var peer = new DirectorySyncPeer(tmp.Dir("bus"));
        await peer.PushAsync(Batch(A, 1, 3), CancellationToken.None);
        await peer.PushAsync(Batch(A, 4, 9), CancellationToken.None);
        await peer.PushAsync(Batch(B, 1, 2), CancellationToken.None);

        var cursor = new SyncCursor(A);
        var first = await peer.PullAsync(cursor, 100, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(B, first.SourceReplicaId);
        cursor.Advance(B, first.ToSeq);
        Assert.Null(await peer.PullAsync(cursor, 100, CancellationToken.None));

        var bCursor = new SyncCursor(B);
        var seen = new List<(long, long)>();
        while (await peer.PullAsync(bCursor, 100, CancellationToken.None) is { } batch)
        {
            Assert.Equal(A, batch.SourceReplicaId);
            seen.Add((batch.FromSeq, batch.ToSeq));
            bCursor.Advance(A, batch.ToSeq);
        }
        Assert.Equal([(1L, 3L), (4L, 9L)], seen);
    }

    [Fact]
    public async Task Pull_ResumesFromCursor_AndToleratesGaps()
    {
        using var tmp = new TempDir();
        await using var peer = new DirectorySyncPeer(tmp.Dir("bus"));
        await peer.PushAsync(Batch(A, 1, 3), CancellationToken.None);
        await peer.PushAsync(Batch(A, 10, 12), CancellationToken.None); // 4..9 were all filtered out and never pushed

        var cursor = new SyncCursor(B);
        cursor.Advance(A, 3);
        var batch = await peer.PullAsync(cursor, 100, CancellationToken.None);
        Assert.NotNull(batch);
        Assert.Equal(10, batch.FromSeq);
    }

    [Fact]
    public async Task Push_SameSegmentTwice_IsIdempotent()
    {
        using var tmp = new TempDir();
        await using var peer = new DirectorySyncPeer(tmp.Dir("bus"));
        await peer.PushAsync(Batch(A, 1, 3), CancellationToken.None);
        await peer.PushAsync(Batch(A, 1, 3), CancellationToken.None);

        string log = Path.Combine(peer.Root, "replicas", A.ToString("D"), "log");
        Assert.Single(Directory.GetFiles(log));
    }

    [Fact]
    public async Task Pull_IgnoresForeignFilesAndTornManifest()
    {
        using var tmp = new TempDir();
        await using var peer = new DirectorySyncPeer(tmp.Dir("bus"));
        await peer.PushAsync(Batch(A, 1, 3), CancellationToken.None);
        string log = Path.Combine(peer.Root, "replicas", A.ToString("D"), "log");
        File.WriteAllText(Path.Combine(log, "notes.txt"), "hi");
        File.WriteAllText(Path.Combine(log, "00000000000000000004-00000000000000000006.qvcb.tmp"), "partial");
        File.WriteAllText(Path.Combine(peer.Root, "manifest", A.ToString("D") + ".json"), "{ torn");
        Directory.CreateDirectory(Path.Combine(peer.Root, "replicas", "not-a-guid"));

        var batch = await peer.PullAsync(new SyncCursor(B), 100, CancellationToken.None);
        Assert.NotNull(batch);
        Assert.Equal(3, batch.ToSeq);
    }

    [Fact]
    public async Task Snapshot_PublishAndOpen_KeepsOnlyNewest_AndSkipsSelf()
    {
        using var tmp = new TempDir();
        await using var peer = new DirectorySyncPeer(tmp.Dir("bus"));

        await peer.PublishSnapshotAsync(s => { s.Write([1, 2, 3]); return new SnapshotInfo(A, 10, 3); }, CancellationToken.None);
        await peer.PublishSnapshotAsync(s => { s.Write([4, 5, 6, 7]); return new SnapshotInfo(A, 20, 4); }, CancellationToken.None);
        await peer.PublishSnapshotAsync(s => { s.Write([9]); return new SnapshotInfo(B, 5, 1); }, CancellationToken.None);

        string aSnapshots = Path.Combine(peer.Root, "replicas", A.ToString("D"), "snapshot");
        Assert.Equal(["00000000000000000020.qvec"], Directory.GetFiles(aSnapshots).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(Path.Combine(peer.Root, "replicas"), "*.tmp"));
        Assert.Equal(20, peer.ReadManifest(A)!.SnapshotSeq);

        await using (var s = await peer.OpenSnapshotAsync(new SyncCursor(B), CancellationToken.None))
        {
            Assert.NotNull(s);
            var buf = new byte[4];
            s.ReadExactly(buf);
            Assert.Equal([4, 5, 6, 7], buf);
        }

        await using (var s = await peer.OpenSnapshotAsync(new SyncCursor(A), CancellationToken.None))
        {
            Assert.NotNull(s);
            Assert.Equal(1, s.Length);
        }

        await using var peer2 = new DirectorySyncPeer(tmp.Dir("empty"));
        Assert.Null(await peer2.OpenSnapshotAsync(new SyncCursor(A), CancellationToken.None));
    }

    [Fact]
    public async Task Pull_SegmentUnderWrongReplica_IsProtocolError()
    {
        using var tmp = new TempDir();
        await using var peer = new DirectorySyncPeer(tmp.Dir("bus"));
        await peer.PushAsync(Batch(A, 1, 3), CancellationToken.None);

        string aLog = Path.Combine(peer.Root, "replicas", A.ToString("D"), "log");
        string bLog = Path.Combine(peer.Root, "replicas", B.ToString("D"), "log");
        Directory.CreateDirectory(bLog);
        File.Copy(Directory.GetFiles(aLog)[0], Path.Combine(bLog, Path.GetFileName(Directory.GetFiles(aLog)[0])));

        var cursor = new SyncCursor(Guid.NewGuid());
        cursor.Advance(A, 3);
        await Assert.ThrowsAsync<SyncProtocolException>(() => peer.PullAsync(cursor, 100, CancellationToken.None));
    }
}
