using System;

namespace Qvec.Core.Sync;

/// <summary>
/// Turns on per-document version tracking, the foundation for replicating a database between
/// processes or machines. Tracking is off by default: a database that never syncs pays nothing,
/// and its file stays byte-for-byte compatible with readers that predate this feature. A tracked
/// file is written as format version 6 so that older readers refuse it instead of silently
/// writing rows without versions.
/// </summary>
public sealed record ChangeTrackingOptions
{
    /// <summary>
    /// Identity of this replica, recorded in the file header and stamped on every write it makes.
    /// Defaults to a fresh <see cref="Guid.NewGuid"/>. Must not be <see cref="Guid.Empty"/>.
    /// </summary>
    public Guid? ReplicaId { get; init; }

    /// <summary>
    /// Number of change records the file retains for peers to pull. Defaults to the row capacity
    /// (<c>max</c>) and grows with it. A peer that falls further behind than this has to bootstrap
    /// from a snapshot. Each record costs 64 bytes of file space.
    /// </summary>
    public long? LogCapacity { get; init; }

    internal void Validate()
    {
        if (ReplicaId is { } id && id == Guid.Empty)
            throw new ArgumentException("ReplicaId must not be Guid.Empty.", nameof(ReplicaId));
        if (LogCapacity is { } capacity)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity, nameof(LogCapacity));
    }
}
