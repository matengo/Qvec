using Qvec.Core.Quantization;

namespace Qvec.Core.Sync;

/// <summary>What happened to a document. Values are stable on disk and on the wire.</summary>
public enum ChangeType : byte
{
    Upsert = 1,
    Delete = 2
}

/// <summary>How vectors travel in a <see cref="ChangeBatch"/>.</summary>
public enum ChangePayloadKind : byte
{
    /// <summary>Exact <c>float32</c> vectors; the source keeps floats (<c>None</c> or <c>Int8Rescored</c>).</summary>
    Float = 1,

    /// <summary>Int8 codes plus per-vector parameters; the source is pure <c>Int8</c> and has no floats to send.</summary>
    Int8 = 2
}

/// <summary>
/// One document's latest state as seen by the source replica. A <see cref="ChangeType.Delete"/>
/// carries only the id and the tombstone's version.
/// </summary>
public sealed class ChangeItem
{
    public required ChangeType Type { get; init; }
    public required Guid DocumentId { get; init; }
    public required EntryVersion Version { get; init; }

    /// <summary>Set for <see cref="ChangeType.Upsert"/> when the batch payload is <see cref="ChangePayloadKind.Float"/>.</summary>
    public float[]? Vector { get; init; }

    /// <summary>Set for <see cref="ChangeType.Upsert"/> when the batch payload is <see cref="ChangePayloadKind.Int8"/>.</summary>
    public byte[]? Codes { get; init; }

    /// <summary>Accompanies <see cref="Codes"/>.</summary>
    public Int8VectorParameters? Parameters { get; init; }

    /// <summary>Set for <see cref="ChangeType.Upsert"/>.</summary>
    public string? Metadata { get; init; }
}

/// <summary>
/// A coalesced slice of one replica's change log: every document touched by records
/// <c>FromSeq..ToSeq</c> appears once, with its current state. Produced by
/// <c>QvecDatabase.GetChanges</c>, consumed by <c>QvecDatabase.ApplyChanges</c>. The wire
/// encoding lives in <c>Qvec.Sync</c>.
/// </summary>
public sealed class ChangeBatch
{
    /// <summary>The replica whose log this slice came from.</summary>
    public required Guid SourceReplicaId { get; init; }

    public required int Dimension { get; init; }
    public required DistanceFunction DistanceFunction { get; init; }
    public required ChangePayloadKind Payload { get; init; }

    /// <summary>First log sequence number covered (exclusive cursor + 1).</summary>
    public required long FromSeq { get; init; }

    /// <summary>
    /// Last log sequence number covered. Persist this as the cursor for
    /// <see cref="SourceReplicaId"/> only after the batch has been applied successfully.
    /// </summary>
    public required long ToSeq { get; init; }

    public required IReadOnlyList<ChangeItem> Items { get; init; }

    /// <summary>True when the log had more records after <see cref="ToSeq"/> at read time.</summary>
    public required bool HasMore { get; init; }
}

public enum ApplyOutcome : byte
{
    /// <summary>The change was newer than the local state and was written.</summary>
    Applied,

    /// <summary>The local state was already at least as new; nothing was written.</summary>
    Skipped,

    /// <summary>The item could not be applied (wrong dimension, incompatible payload...). See the reason.</summary>
    Rejected
}

public readonly record struct ApplyItemResult(int Index, Guid DocumentId, ApplyOutcome Outcome, string? Reason);

/// <summary>Per-item outcome of <c>QvecDatabase.ApplyChanges</c> plus totals.</summary>
public sealed class ApplyResult
{
    public required int Applied { get; init; }
    public required int Skipped { get; init; }
    public required int Rejected { get; init; }
    public required IReadOnlyList<ApplyItemResult> Items { get; init; }
}

/// <summary>
/// The requested cursor points before the oldest record still in the change-log ring. The
/// caller has fallen too far behind and must bootstrap from a snapshot instead of a delta.
/// </summary>
public sealed class SyncCursorTooOldException : Exception
{
    public long RequestedSinceSeq { get; }
    public long OldestAvailableSeq { get; }

    public SyncCursorTooOldException(long requestedSinceSeq, long oldestAvailableSeq)
        : base($"Changes since seq {requestedSinceSeq} are no longer available; the oldest record in the log is seq {oldestAvailableSeq}. Bootstrap from a snapshot.")
    {
        RequestedSinceSeq = requestedSinceSeq;
        OldestAvailableSeq = oldestAvailableSeq;
    }
}
