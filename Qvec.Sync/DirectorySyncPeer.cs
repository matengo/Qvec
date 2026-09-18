using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Qvec.Core.Sync;

namespace Qvec.Sync;

/// <summary>
/// A bus-style peer over a shared directory (local disk, SMB, a synced folder). Every replica
/// appends immutable segments under its own prefix and reads everyone else's:
/// <code>
/// root/
///   replicas/{replicaId}/log/{fromSeq:D20}-{toSeq:D20}.qvcb
///   replicas/{replicaId}/snapshot/{seq:D20}.qvec
///   manifest/{replicaId}.json            { latestSeq, snapshotSeq, updatedUtc }
/// </code>
/// Files are written to a <c>.tmp</c> name and renamed, so readers never see a partial segment.
/// Segments are never removed in this version, so a directory peer never reports a cursor as
/// too old; snapshots exist to shortcut a long catch-up, not to replace missing history.
/// <see cref="ISyncPeer.PeerId"/> is <see cref="Guid.Empty"/>: there is no single remote replica.
/// </summary>
public sealed class DirectorySyncPeer : ISyncPeer
{
    private const string SegmentExtension = ".qvcb";
    private const string SnapshotExtension = ".qvec";
    private readonly string _root;
    private readonly SyncCompression _compression;

    public DirectorySyncPeer(string root, SyncCompression compression = SyncCompression.Auto)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = Path.GetFullPath(root);
        _compression = compression;
        Directory.CreateDirectory(Path.Combine(_root, "replicas"));
        Directory.CreateDirectory(Path.Combine(_root, "manifest"));
    }

    public string Root => _root;

    public Guid PeerId => Guid.Empty;

    public Task<ChangeBatch?> PullAsync(SyncCursor cursor, int maxItems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (string replicaDir in ListReplicaDirectories())
        {
            if (!Guid.TryParseExact(Path.GetFileName(replicaDir), "D", out Guid replicaId) || replicaId == cursor.Self)
                continue;

            long position = cursor[replicaId];
            var manifest = ReadManifest(replicaId);
            if (manifest is not null && manifest.LatestSeq <= position) continue;

            Segment? next = null;
            foreach (var segment in ListSegments(replicaDir))
            {
                if (segment.ToSeq <= position) continue;
                if (next is null || segment.FromSeq < next.Value.FromSeq || (segment.FromSeq == next.Value.FromSeq && segment.ToSeq < next.Value.ToSeq))
                    next = segment;
            }
            if (next is null) continue;

            ChangeBatch batch;
            using (var fs = new FileStream(next.Value.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                batch = ChangeBatchWire.Read(fs);
            }

            if (batch.SourceReplicaId != replicaId)
                throw new SyncProtocolException($"Segment '{next.Value.Path}' was written for replica {batch.SourceReplicaId} but sits under {replicaId}.");
            if (batch.ToSeq != next.Value.ToSeq)
                throw new SyncProtocolException($"Segment '{next.Value.Path}' claims ToSeq {batch.ToSeq} in its header but {next.Value.ToSeq} in its name.");

            return Task.FromResult<ChangeBatch?>(batch);
        }

        return Task.FromResult<ChangeBatch?>(null);
    }

    public Task PushAsync(ChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();

        string logDir = Path.Combine(ReplicaDirectory(batch.SourceReplicaId), "log");
        Directory.CreateDirectory(logDir);

        string name = SegmentName(batch.FromSeq, batch.ToSeq);
        string final = Path.Combine(logDir, name);
        if (File.Exists(final)) return Task.CompletedTask;

        string tmp = final + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            ChangeBatchWire.Write(batch, fs, _compression);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, final, overwrite: true);

        var manifest = ReadManifest(batch.SourceReplicaId) ?? new ReplicaManifest();
        manifest.LatestSeq = Math.Max(manifest.LatestSeq, batch.ToSeq);
        WriteManifest(batch.SourceReplicaId, manifest);
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenSnapshotAsync(SyncCursor cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return OpenSnapshotAsync(cursor.Self, cancellationToken);
    }

    /// <summary>
    /// Opens the snapshot with the highest sequence number published by any replica other than
    /// <paramref name="self"/>, or <c>null</c> when nobody has published one.
    /// </summary>
    public Task<Stream?> OpenSnapshotAsync(Guid self, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? bestPath = null;
        long bestSeq = -1;
        foreach (string replicaDir in ListReplicaDirectories())
        {
            if (!Guid.TryParseExact(Path.GetFileName(replicaDir), "D", out Guid replicaId) || replicaId == self) continue;
            string snapshotDir = Path.Combine(replicaDir, "snapshot");
            if (!Directory.Exists(snapshotDir)) continue;

            foreach (string file in Directory.EnumerateFiles(snapshotDir, "*" + SnapshotExtension))
            {
                if (!long.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out long seq)) continue;
                if (seq > bestSeq) { bestSeq = seq; bestPath = file; }
            }
        }

        if (bestPath is null) return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(new FileStream(bestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public Task PublishSnapshotAsync(Func<Stream, SnapshotInfo> export, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);
        cancellationToken.ThrowIfCancellationRequested();

        // The identity is only known after the export, so write under a neutral temp name first.
        string tmp = Path.Combine(_root, "replicas", Guid.NewGuid().ToString("N") + ".snapshot.tmp");
        SnapshotInfo info;
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            info = export(fs);
            fs.Flush(flushToDisk: true);
        }

        string snapshotDir = Path.Combine(ReplicaDirectory(info.SourceReplicaId), "snapshot");
        Directory.CreateDirectory(snapshotDir);
        string final = Path.Combine(snapshotDir, info.ChangeSeq.ToString("D20", CultureInfo.InvariantCulture) + SnapshotExtension);
        File.Move(tmp, final, overwrite: true);

        foreach (string old in Directory.EnumerateFiles(snapshotDir, "*" + SnapshotExtension))
        {
            if (!string.Equals(old, final, StringComparison.OrdinalIgnoreCase))
                TryDelete(old);
        }

        var manifest = ReadManifest(info.SourceReplicaId) ?? new ReplicaManifest();
        manifest.SnapshotSeq = info.ChangeSeq;
        manifest.LatestSeq = Math.Max(manifest.LatestSeq, info.ChangeSeq);
        WriteManifest(info.SourceReplicaId, manifest);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Replica ids that have written anything to this directory.</summary>
    public IReadOnlyList<Guid> ListReplicas()
    {
        var ids = new List<Guid>();
        foreach (string dir in ListReplicaDirectories())
        {
            if (Guid.TryParseExact(Path.GetFileName(dir), "D", out Guid id)) ids.Add(id);
        }
        return ids;
    }

    public ReplicaManifest? ReadManifest(Guid replicaId)
    {
        string path = ManifestPath(replicaId);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize(fs, DirectorySyncPeerJsonContext.Default.ReplicaManifest);
        }
        catch (JsonException)
        {
            // A torn manifest is not fatal: the segment listing is authoritative.
            return null;
        }
    }

    private void WriteManifest(Guid replicaId, ReplicaManifest manifest)
    {
        manifest.UpdatedUtc = DateTimeOffset.UtcNow;
        string path = ManifestPath(replicaId);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, manifest, DirectorySyncPeerJsonContext.Default.ReplicaManifest);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private string ReplicaDirectory(Guid replicaId) => Path.Combine(_root, "replicas", replicaId.ToString("D"));
    private string ManifestPath(Guid replicaId) => Path.Combine(_root, "manifest", replicaId.ToString("D") + ".json");

    private IEnumerable<string> ListReplicaDirectories()
    {
        string replicas = Path.Combine(_root, "replicas");
        if (!Directory.Exists(replicas)) return [];
        var dirs = Directory.GetDirectories(replicas);
        Array.Sort(dirs, StringComparer.Ordinal);
        return dirs;
    }

    internal static string SegmentName(long fromSeq, long toSeq)
        => fromSeq.ToString("D20", CultureInfo.InvariantCulture) + "-" + toSeq.ToString("D20", CultureInfo.InvariantCulture) + SegmentExtension;

    private static IEnumerable<Segment> ListSegments(string replicaDir)
    {
        string logDir = Path.Combine(replicaDir, "log");
        if (!Directory.Exists(logDir)) yield break;

        foreach (string file in Directory.EnumerateFiles(logDir, "*" + SegmentExtension))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            int dash = stem.IndexOf('-');
            if (dash != 20 || stem.Length != 41) continue;
            if (!long.TryParse(stem.AsSpan(0, 20), NumberStyles.None, CultureInfo.InvariantCulture, out long from)) continue;
            if (!long.TryParse(stem.AsSpan(21), NumberStyles.None, CultureInfo.InvariantCulture, out long to)) continue;
            yield return new Segment(file, from, to);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private readonly record struct Segment(string Path, long FromSeq, long ToSeq);
}

/// <summary>Per-replica summary written next to the segments so readers can skip idle replicas.</summary>
public sealed class ReplicaManifest
{
    public long LatestSeq { get; set; }
    public long? SnapshotSeq { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReplicaManifest))]
internal sealed partial class DirectorySyncPeerJsonContext : JsonSerializerContext;
