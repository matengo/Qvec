using Qvec.Core.Sync;

namespace Qvec.Sync;

/// <summary>Hint from a peer that new changes exist; the agent uses it to skip the poll wait.</summary>
public readonly record struct SyncSignal(Guid ReplicaId, long Seq);

/// <summary>
/// A transport the <see cref="SyncAgent"/> pushes local changes to and pulls remote changes from.
/// Implementations are either a <em>hub</em> (one replica on the other end, identified by
/// <see cref="PeerId"/>) or a <em>bus</em> (a shared medium every replica writes its own prefix to,
/// <see cref="PeerId"/> == <see cref="Guid.Empty"/>). The agent derives its push filter from that.
/// </summary>
public interface ISyncPeer : IAsyncDisposable
{
    /// <summary>Identity of the remote replica for a hub; <see cref="Guid.Empty"/> for a bus.</summary>
    Guid PeerId { get; }

    /// <summary>
    /// The next batch not yet covered by <paramref name="cursor"/>, or <c>null</c> when the peer has
    /// nothing newer. The returned batch must advance the cursor for its
    /// <see cref="ChangeBatch.SourceReplicaId"/>. <paramref name="maxItems"/> is advisory.
    /// </summary>
    /// <exception cref="SyncCursorTooOldException">The peer can no longer serve a delta from the cursor.</exception>
    Task<ChangeBatch?> PullAsync(SyncCursor cursor, int maxItems, CancellationToken cancellationToken);

    /// <summary>Delivers one batch of this replica's changes.</summary>
    Task PushAsync(ChangeBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// A stream over a snapshot file to bootstrap from, or <c>null</c> when none is available.
    /// <paramref name="cursor"/> identifies the caller so a shared medium can skip its own snapshot.
    /// </summary>
    Task<Stream?> OpenSnapshotAsync(SyncCursor cursor, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a snapshot of the local database so peers that fall behind the change log can
    /// bootstrap. <paramref name="export"/> writes the file and returns its identity; peers that
    /// cannot host snapshots may complete without calling it.
    /// </summary>
    Task PublishSnapshotAsync(Func<Stream, SnapshotInfo> export, CancellationToken cancellationToken);

    /// <summary>Change notifications; the default is silent, which makes the agent poll.</summary>
    IAsyncEnumerable<SyncSignal> WatchAsync(CancellationToken cancellationToken) => Empty(cancellationToken);

    private static async IAsyncEnumerable<SyncSignal> Empty(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
