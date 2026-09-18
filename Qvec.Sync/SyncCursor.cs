namespace Qvec.Sync;

/// <summary>
/// This replica's view of every other replica's change log: for each remote
/// <c>ReplicaId</c>, the last sequence number already applied locally. Unknown replicas read as 0.
/// <see cref="Self"/> identifies the local replica so a shared-medium peer can skip its own prefix.
/// </summary>
public sealed class SyncCursor
{
    private readonly Dictionary<Guid, long> _positions;

    public SyncCursor(Guid self) : this(self, new Dictionary<Guid, long>()) { }

    public SyncCursor(Guid self, IReadOnlyDictionary<Guid, long> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        Self = self;
        _positions = new Dictionary<Guid, long>(positions);
    }

    /// <summary>The local replica.</summary>
    public Guid Self { get; }

    public IReadOnlyDictionary<Guid, long> Positions => _positions;

    /// <summary>Last applied sequence number for <paramref name="replicaId"/>; 0 when never seen.</summary>
    public long this[Guid replicaId] => _positions.TryGetValue(replicaId, out long seq) ? seq : 0;

    /// <summary>Moves the position for <paramref name="replicaId"/> forward; never backwards.</summary>
    /// <returns>True when the position changed.</returns>
    public bool Advance(Guid replicaId, long seq)
    {
        if (seq <= this[replicaId]) return false;
        _positions[replicaId] = seq;
        return true;
    }

    public SyncCursor Clone() => new(Self, _positions);
}
