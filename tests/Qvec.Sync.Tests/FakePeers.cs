using Qvec.Core;
using Qvec.Core.Sync;

namespace Qvec.Sync.Tests;

/// <summary>
/// A hub peer that talks straight to a server-side <see cref="QvecDatabase"/>, which is what a
/// network transport would do on the other end of the wire. Lets the agent tests cover hub
/// topology, snapshot bootstrap and cursor-too-old without a socket.
/// </summary>
public sealed class InMemoryHubPeer : ISyncPeer
{
    private readonly QvecDatabase _server;

    public InMemoryHubPeer(QvecDatabase server) => _server = server;

    public Guid PeerId => _server.ReplicaId;
    public int Pushes { get; private set; }
    public int SnapshotsPublished { get; private set; }

    public Task<ChangeBatch?> PullAsync(SyncCursor cursor, int maxItems, CancellationToken cancellationToken)
    {
        long since = cursor[_server.ReplicaId];
        var batch = _server.GetChanges(since, maxItems, excludeOrigin: cursor.Self);
        return Task.FromResult(batch.ToSeq > since ? batch : null);
    }

    public Task PushAsync(ChangeBatch batch, CancellationToken cancellationToken)
    {
        Pushes++;
        _server.ApplyChanges(batch);
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenSnapshotAsync(SyncCursor cursor, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        _server.ExportSnapshot(ms);
        ms.Position = 0;
        return Task.FromResult<Stream?>(ms);
    }

    public Task PublishSnapshotAsync(Func<Stream, SnapshotInfo> export, CancellationToken cancellationToken)
    {
        SnapshotsPublished++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Wraps a peer and fails the N-th push, to simulate a dropped connection mid-iteration.</summary>
public sealed class FlakyPeer(ISyncPeer inner, int failOnPush) : ISyncPeer
{
    private int _pushes;

    public Guid PeerId => inner.PeerId;

    public Task<ChangeBatch?> PullAsync(SyncCursor cursor, int maxItems, CancellationToken ct) => inner.PullAsync(cursor, maxItems, ct);

    public Task PushAsync(ChangeBatch batch, CancellationToken ct)
    {
        if (++_pushes == failOnPush) throw new IOException("connection reset");
        return inner.PushAsync(batch, ct);
    }

    public Task<Stream?> OpenSnapshotAsync(SyncCursor cursor, CancellationToken ct) => inner.OpenSnapshotAsync(cursor, ct);
    public Task PublishSnapshotAsync(Func<Stream, SnapshotInfo> export, CancellationToken ct) => inner.PublishSnapshotAsync(export, ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
