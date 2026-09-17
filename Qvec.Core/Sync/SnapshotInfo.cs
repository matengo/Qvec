namespace Qvec.Core.Sync;

/// <summary>
/// What a caller needs to remember after <see cref="QvecDatabase.ExportSnapshot"/>: the
/// snapshot carries <paramref name="SourceReplicaId"/>'s identity and contains every change up
/// to and including <paramref name="ChangeSeq"/>, so a replica bootstrapped from it should set
/// its cursor for that peer to <paramref name="ChangeSeq"/>.
/// </summary>
/// <param name="SourceReplicaId">Identity of the database the snapshot was taken from.</param>
/// <param name="ChangeSeq">Newest change-log sequence number contained in the snapshot.</param>
/// <param name="Length">Bytes written to the destination stream.</param>
public readonly record struct SnapshotInfo(Guid SourceReplicaId, long ChangeSeq, long Length);
