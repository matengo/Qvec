using Qvec.Core;
using Qvec.Core.Sync;

namespace Qvec.Sync;

/// <summary>Counters for one <see cref="SyncAgent.SyncOnceAsync"/> pass.</summary>
public readonly record struct SyncIterationResult(
    int PushedBatches,
    int PushedItems,
    int PulledBatches,
    int Applied,
    int Skipped,
    int Rejected,
    bool Bootstrapped,
    bool SnapshotPublished)
{
    public bool DidWork => PushedBatches > 0 || PulledBatches > 0 || Bootstrapped || SnapshotPublished;
}

/// <summary>
/// Keeps one <see cref="QvecDatabase"/> in sync with an <see cref="ISyncPeer"/>: pushes the local
/// change log, pulls and applies remote batches, and remembers where it got to in a small state
/// file next to the database. Every pass is idempotent, so a crash at any point is recovered by
/// simply running again. One agent per database; the agent does not take ownership of the database
/// unless it has to replace it during a bootstrap.
/// </summary>
public sealed class SyncAgent : IAsyncDisposable
{
    private readonly ISyncPeer _peer;
    private readonly SyncOptions _options;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private QvecDatabase _db;
    private SyncState _state;
    private SyncCursor _cursor;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _disposed;

    public SyncAgent(QvecDatabase database, ISyncPeer peer, SyncOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(peer);
        if (!database.IsChangeTrackingEnabled)
            throw new ArgumentException("The database must have change tracking enabled (see ChangeTrackingOptions) before it can be synchronised.", nameof(database));

        _options = options ?? new SyncOptions();
        _options.Validate();
        _db = database;
        _peer = peer;
        _statePath = _options.StatePath ?? database.FilePath + ".sync";
        _state = SyncState.LoadOrCreate(_statePath, database.ReplicaId);
        _cursor = _state.ToCursor();
    }

    /// <summary>The database being synchronised. Changes after <see cref="DatabaseReplaced"/>.</summary>
    public QvecDatabase Database => _db;

    public ISyncPeer Peer => _peer;

    public string StatePath => _statePath;

    /// <summary>Highest local sequence number already pushed.</summary>
    public long PushedSeq => _state.PushedSeq;

    /// <summary>Where this agent has read every remote replica up to.</summary>
    public SyncCursor Cursor => _cursor.Clone();

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>
    /// The agent replaced the database file with a snapshot. The old <see cref="QvecDatabase"/>
    /// instance was disposed; read <see cref="Database"/> for the new one.
    /// </summary>
    public event Action<QvecDatabase>? DatabaseReplaced;

    public event Action<SyncIterationResult>? IterationCompleted;

    public event Action<Exception>? IterationFailed;

    /// <summary>
    /// Runs one push–pull pass and returns. Safe to call concurrently with the background loop;
    /// passes are serialised.
    /// </summary>
    /// <exception cref="SyncLogOverrunException">The local ring rotated past the last pushed sequence.</exception>
    /// <exception cref="SyncCursorTooOldException">The peer cannot serve a delta and snapshot bootstrap is disabled or unavailable.</exception>
    public async Task<SyncIterationResult> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SyncOnceCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Exports the database and hands it to the peer as a bootstrap snapshot.</summary>
    public async Task PublishSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PublishSnapshotCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Starts the background loop: poll every <see cref="SyncOptions.PollInterval"/>, wake early on peer signals, back off on errors.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) throw new InvalidOperationException("The agent is already running.");
        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_loopCts.Token));
    }

    public async Task StopAsync()
    {
        if (_loop is null) return;
        _loopCts!.Cancel();
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            _loopCts.Dispose();
            _loopCts = null;
            _loop = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _gate.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var wake = new SemaphoreSlim(0, 1);
        var watcher = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in _peer.WatchAsync(ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (wake.CurrentCount == 0) wake.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { IterationFailed?.Invoke(ex); }
        }, ct);

        TimeSpan backoff = _options.InitialBackoff;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var result = await SyncOnceAsync(ct).ConfigureAwait(false);
                IterationCompleted?.Invoke(result);
                backoff = _options.InitialBackoff;
                wait = _options.PollInterval;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                IterationFailed?.Invoke(ex);
                wait = backoff;
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
            }

            try { await wake.WaitAsync(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        try { await watcher.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private async Task<SyncIterationResult> SyncOnceCoreAsync(CancellationToken ct)
    {
        int pushedBatches = 0, pushedItems = 0, pulledBatches = 0, applied = 0, skipped = 0, rejected = 0;
        bool bootstrapped = false, snapshotPublished = false;

        // 1. Push everything after PushedSeq.
        Guid? excludeOrigin = _peer.PeerId == Guid.Empty ? null : _peer.PeerId;
        if (_state.PushedSeq > _db.ChangeSeq)
            throw new SyncStateException(_statePath, $"it says seq {_state.PushedSeq} was pushed but the database log only reaches {_db.ChangeSeq}; the database was probably restored from an older copy. Delete the state file to re-push from the start of the log.");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ChangeBatch batch;
            try
            {
                batch = _db.GetChanges(_state.PushedSeq, _options.BatchSize, excludeOrigin);
            }
            catch (SyncCursorTooOldException ex)
            {
                throw new SyncLogOverrunException(_state.PushedSeq, ex.OldestAvailableSeq);
            }

            if (batch.Items.Count > 0)
            {
                await _peer.PushAsync(batch, ct).ConfigureAwait(false);
                pushedBatches++;
                pushedItems += batch.Items.Count;
            }

            if (batch.ToSeq > _state.PushedSeq)
            {
                _state.PushedSeq = batch.ToSeq;
                _state.Save(_statePath);
            }
            if (!batch.HasMore) break;
        }

        // 2. Optional periodic snapshot so peers can catch up when the ring has rotated.
        if (_options.SnapshotInterval is { } interval &&
            (_state.LastSnapshotUtc is null || DateTimeOffset.UtcNow - _state.LastSnapshotUtc.Value >= interval))
        {
            await PublishSnapshotCoreAsync(ct).ConfigureAwait(false);
            snapshotPublished = true;
        }

        // 3. Pull until the peer has nothing newer.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ChangeBatch? batch;
            try
            {
                batch = await _peer.PullAsync(_cursor, _options.BatchSize, ct).ConfigureAwait(false);
            }
            catch (SyncCursorTooOldException)
            {
                if (!_options.BootstrapFromSnapshotIfBehind) throw;
                if (!await BootstrapAsync(ct).ConfigureAwait(false)) throw;
                bootstrapped = true;
                continue;
            }

            if (batch is null) break;
            pulledBatches++;

            var result = _db.ApplyChanges(batch);
            applied += result.Applied;
            skipped += result.Skipped;
            rejected += result.Rejected;

            if (!_cursor.Advance(batch.SourceReplicaId, batch.ToSeq))
                throw new SyncProtocolException($"Peer returned a batch for {batch.SourceReplicaId} ending at seq {batch.ToSeq}, which does not advance the cursor ({_cursor[batch.SourceReplicaId]}). Refusing to loop.");

            _state.Cursor = new Dictionary<Guid, long>(_cursor.Positions);
            _state.Save(_statePath);
        }

        return new SyncIterationResult(pushedBatches, pushedItems, pulledBatches, applied, skipped, rejected, bootstrapped, snapshotPublished);
    }

    private async Task PublishSnapshotCoreAsync(CancellationToken ct)
    {
        await _peer.PublishSnapshotAsync(stream => _db.ExportSnapshot(stream), ct).ConfigureAwait(false);
        _state.LastSnapshotUtc = DateTimeOffset.UtcNow;
        _state.Save(_statePath);
    }

    /// <summary>
    /// Replaces the local file with the peer's snapshot. The database becomes a new replica whose
    /// log is the source's log, so the cursor is reset to just the source position and everything
    /// in the snapshot counts as already pushed.
    /// </summary>
    private async Task<bool> BootstrapAsync(CancellationToken ct)
    {
        Stream? source = await _peer.OpenSnapshotAsync(_cursor, ct).ConfigureAwait(false);
        if (source is null) return false;

        string path = _db.FilePath;
        string tmp = path + ".bootstrap";
        await using (source.ConfigureAwait(false))
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            await source.CopyToAsync(fs, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        var extractor = _options.FieldIndexExtractor ?? _db.FieldIndexExtractor;
        _db.Dispose();

        Guid newId = Guid.NewGuid();
        QvecDatabase.AdoptAsReplica(tmp, newId, out Guid sourceId, out long sourceSeq);
        File.Move(tmp, path, overwrite: true);

        var db = QvecDatabase.Open(path);
        if (extractor is not null)
        {
            db.FieldIndexExtractor = extractor;
            db.RebuildFieldIndex(extractor);
        }
        _db = db;

        _state = new SyncState
        {
            ReplicaId = newId,
            PushedSeq = sourceSeq,
            Cursor = new Dictionary<Guid, long> { [sourceId] = sourceSeq },
            LastSnapshotUtc = null,
        };
        _cursor = _state.ToCursor();
        _state.Save(_statePath);

        DatabaseReplaced?.Invoke(db);
        return true;
    }
}
