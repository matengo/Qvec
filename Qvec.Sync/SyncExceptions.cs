namespace Qvec.Sync;

/// <summary>Base for failures raised by <c>Qvec.Sync</c> itself (as opposed to transport I/O errors).</summary>
public class SyncException : Exception
{
    public SyncException(string message) : base(message) { }
    public SyncException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>The agent's state file is unreadable or belongs to a different replica.</summary>
public sealed class SyncStateException : SyncException
{
    public string Path { get; }

    public SyncStateException(string path, string message, Exception? inner = null)
        : base($"Sync state '{path}': {message}", inner)
    {
        Path = path;
    }
}

/// <summary>A peer or wire payload violated the protocol (bad magic, CRC, non-advancing batch...).</summary>
public sealed class SyncProtocolException : SyncException
{
    public SyncProtocolException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The local change-log ring rotated past the position this agent had pushed up to, which
/// means changes were made offline than the ring can hold. Documents whose only record rotated
/// out cannot reach peers through a delta any more; publish a snapshot or enlarge the ring.
/// </summary>
public sealed class SyncLogOverrunException : SyncException
{
    public long PushedSeq { get; }
    public long OldestAvailableSeq { get; }

    public SyncLogOverrunException(long pushedSeq, long oldestAvailableSeq)
        : base($"The local change log no longer contains records after the last pushed position {pushedSeq} (oldest available: {oldestAvailableSeq}). " +
               "Changes made offline exceeded the ring capacity. Publish a snapshot so peers can bootstrap, or enlarge LogCapacity.")
    {
        PushedSeq = pushedSeq;
        OldestAvailableSeq = oldestAvailableSeq;
    }
}
