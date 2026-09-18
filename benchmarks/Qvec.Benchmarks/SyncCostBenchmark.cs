using System.Diagnostics;
using Qvec.Core;
using Qvec.Core.Sync;
using Qvec.Sync;

namespace Qvec.Benchmarks;

/// <summary>
/// What replication costs on top of a build: reading the change log back as batches
/// (<see cref="QvecDatabase.GetChanges"/>), encoding them for the wire, and applying them to an
/// empty replica (<see cref="QvecDatabase.ApplyChanges"/>). Runs on the freshly built index so the
/// vectors are the real dataset, not noise.
/// </summary>
public static class SyncCostBenchmark
{
    public static SyncCostReport Measure(QvecDatabase source, int items, int batchSize)
    {
        items = (int)Math.Min(items, source.ChangeSeq);
        Console.WriteLine($"Sync cost: GetChanges + wire + ApplyChanges over {items:N0} documents (batches of {batchSize}) ...");

        string replicaPath = source.FilePath + ".replica";
        if (File.Exists(replicaPath)) File.Delete(replicaPath);

        var batches = new List<ChangeBatch>();
        var read = Stopwatch.StartNew();
        long seq = 0;
        int collected = 0;
        while (collected < items)
        {
            var batch = source.GetChanges(seq, Math.Min(batchSize, items - collected));
            if (batch.Items.Count == 0 && !batch.HasMore) break;
            batches.Add(batch);
            collected += batch.Items.Count;
            seq = batch.ToSeq;
        }
        read.Stop();

        long wireBytes = 0, rawBytes = 0;
        var encode = Stopwatch.StartNew();
        var encoded = new List<byte[]>(batches.Count);
        foreach (var batch in batches)
        {
            encoded.Add(ChangeBatchWire.Write(batch));
            wireBytes += encoded[^1].Length;
            rawBytes += ChangeBatchWire.Write(batch, SyncCompression.None).Length;
        }
        encode.Stop();

        var decode = Stopwatch.StartNew();
        var decoded = new List<ChangeBatch>(encoded.Count);
        foreach (var bytes in encoded) decoded.Add(ChangeBatchWire.Read(bytes));
        decode.Stop();

        TimeSpan applyTime;
        long replicaBytes;
        using (var replica = new QvecDatabase(
            replicaPath,
            dim: source.VectorDimension,
            max: Math.Max(items, 1024),
            maxNeighbors: 32,
            maxLayers: 5,
            distanceFunction: source.DistanceFunction,
            quantization: source.Quantization,
            changeTracking: new ChangeTrackingOptions { LogCapacity = Math.Max(items, 1024) }))
        {
            var apply = Stopwatch.StartNew();
            int applied = 0;
            foreach (var batch in decoded) applied += replica.ApplyChanges(batch).Applied;
            apply.Stop();
            applyTime = apply.Elapsed;
            if (applied != collected)
                throw new InvalidOperationException($"Replica applied {applied:N0} of {collected:N0} items.");
            replicaBytes = new FileInfo(replicaPath).Length;
        }
        try { File.Delete(replicaPath); } catch { /* best effort */ }

        var report = new SyncCostReport(collected, batches.Count, read.Elapsed, encode.Elapsed, decode.Elapsed, applyTime, rawBytes, wireBytes, replicaBytes);
        Console.WriteLine($"  GetChanges {report.GetChangesPerSecond:N0} docs/s, encode {report.EncodePerSecond:N0} docs/s, decode {report.DecodePerSecond:N0} docs/s, ApplyChanges {report.ApplyPerSecond:N0} docs/s, {report.WireBytesPerDocument:N0} B/doc on the wire ({report.CompressionRatio:P0} of raw).");
        return report;
    }
}

public sealed record SyncCostReport(
    int Documents,
    int Batches,
    TimeSpan GetChangesTime,
    TimeSpan EncodeTime,
    TimeSpan DecodeTime,
    TimeSpan ApplyTime,
    long RawBytes,
    long WireBytes,
    long ReplicaFileBytes)
{
    public double GetChangesPerSecond => Documents / Math.Max(GetChangesTime.TotalSeconds, 1e-9);
    public double EncodePerSecond => Documents / Math.Max(EncodeTime.TotalSeconds, 1e-9);
    public double DecodePerSecond => Documents / Math.Max(DecodeTime.TotalSeconds, 1e-9);
    public double ApplyPerSecond => Documents / Math.Max(ApplyTime.TotalSeconds, 1e-9);
    public double WireBytesPerDocument => Documents == 0 ? 0 : (double)WireBytes / Documents;
    public double CompressionRatio => RawBytes == 0 ? 1 : (double)WireBytes / RawBytes;

    public string ToMarkdown()
    {
        var writer = new StringWriter();
        writer.WriteLine($"Sync cost over {Documents:N0} documents in {Batches:N0} batches:");
        writer.WriteLine();
        writer.WriteLine("| step | time | docs/s |");
        writer.WriteLine("| --- | ---: | ---: |");
        writer.WriteLine($"| `GetChanges` | {GetChangesTime.TotalSeconds:F2} s | {GetChangesPerSecond:N0} |");
        writer.WriteLine($"| wire encode | {EncodeTime.TotalSeconds:F2} s | {EncodePerSecond:N0} |");
        writer.WriteLine($"| wire decode | {DecodeTime.TotalSeconds:F2} s | {DecodePerSecond:N0} |");
        writer.WriteLine($"| `ApplyChanges` (fresh replica, single thread) | {ApplyTime.TotalSeconds:F2} s | {ApplyPerSecond:N0} |");
        writer.WriteLine();
        writer.WriteLine($"Wire: {WireBytes / 1024.0 / 1024.0:F1} MiB, {WireBytesPerDocument:N0} bytes/document ({CompressionRatio:P0} of the uncompressed encoding). Replica file: {ReplicaFileBytes / 1024.0 / 1024.0:F1} MiB.");
        return writer.ToString();
    }
}
