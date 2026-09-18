using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qvec.Sync;

/// <summary>
/// What a <see cref="SyncAgent"/> remembers between runs: which local sequence it has pushed up to
/// and how far it has read every remote replica. Persisted as JSON via temp + rename.
/// </summary>
public sealed class SyncState
{
    /// <summary>The local replica this state belongs to. A mismatch on load is an error, not a guess.</summary>
    public Guid ReplicaId { get; set; }

    /// <summary>Highest local change-log sequence number already delivered to the peer.</summary>
    public long PushedSeq { get; set; }

    /// <summary>Remote replica → last applied sequence number.</summary>
    public Dictionary<Guid, long> Cursor { get; set; } = new();

    public DateTimeOffset? LastSnapshotUtc { get; set; }

    public SyncCursor ToCursor() => new(ReplicaId, Cursor);

    /// <summary>Loads the state file, or returns a fresh state for <paramref name="replicaId"/> when it does not exist.</summary>
    /// <exception cref="SyncStateException">The file is unreadable or belongs to another replica.</exception>
    public static SyncState LoadOrCreate(string path, Guid replicaId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) return new SyncState { ReplicaId = replicaId };

        SyncState? state;
        try
        {
            using var fs = File.OpenRead(path);
            state = JsonSerializer.Deserialize(fs, SyncStateJsonContext.Default.SyncState);
        }
        catch (JsonException ex)
        {
            throw new SyncStateException(path, "the file is not valid sync state JSON. Delete it to start from an empty cursor (the peer will skip everything it already has).", ex);
        }

        if (state is null)
            throw new SyncStateException(path, "the file is empty.");
        if (state.ReplicaId != replicaId)
            throw new SyncStateException(path, $"it belongs to replica {state.ReplicaId} but the database is replica {replicaId}.");
        if (state.PushedSeq < 0 || state.Cursor.Values.Any(v => v < 0))
            throw new SyncStateException(path, "it contains a negative sequence number.");

        return state;
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, this, SyncStateJsonContext.Default.SyncState);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SyncState))]
internal sealed partial class SyncStateJsonContext : JsonSerializerContext;
