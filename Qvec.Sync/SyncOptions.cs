namespace Qvec.Sync;

/// <summary>Configuration for a <see cref="SyncAgent"/>.</summary>
public sealed class SyncOptions
{
    /// <summary>
    /// Where the agent persists its cursor and push position. Defaults to
    /// <c>&lt;database file&gt;.sync</c>. Written atomically (temp + rename).
    /// </summary>
    public string? StatePath { get; set; }

    /// <summary>How long the background loop waits between iterations when no signal arrives.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Documents per pushed batch; also passed to the peer as the pull hint.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// When the peer reports <see cref="Core.Sync.SyncCursorTooOldException"/>, replace the local
    /// database with the peer's snapshot instead of failing. The agent then owns the database
    /// lifecycle: it closes, replaces and reopens the file, and <see cref="SyncAgent.Database"/>
    /// changes. Local changes not yet pushed at that point are lost; the agent always pushes
    /// before pulling to keep that window small.
    /// </summary>
    public bool BootstrapFromSnapshotIfBehind { get; set; }

    /// <summary>
    /// Publish a snapshot to the peer when the previous one is older than this. <c>null</c> never
    /// publishes automatically; <see cref="SyncAgent.PublishSnapshotAsync"/> remains available.
    /// </summary>
    public TimeSpan? SnapshotInterval { get; set; }

    /// <summary>
    /// Field-index terms derived from metadata, applied to changes received from peers and used
    /// to rebuild the index after a snapshot bootstrap. Set this to the same function your
    /// application uses with <c>RebuildFieldIndex</c>.
    /// </summary>
    public Func<string, IEnumerable<(string Field, string Value)>>? FieldIndexExtractor { get; set; }

    /// <summary>Delay after the first failed iteration; doubles per failure up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);

    internal void Validate()
    {
        if (BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(BatchSize), "BatchSize must be positive.");
        if (PollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(PollInterval), "PollInterval must be positive.");
        if (InitialBackoff <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(InitialBackoff), "InitialBackoff must be positive.");
        if (MaxBackoff < InitialBackoff) throw new ArgumentOutOfRangeException(nameof(MaxBackoff), "MaxBackoff must be at least InitialBackoff.");
        if (SnapshotInterval is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SnapshotInterval), "SnapshotInterval must be positive when set.");
    }
}
