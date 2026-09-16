using System.Buffers.Binary;
using System.Diagnostics;
using Qvec.Core;

namespace Qvec.Benchmarks;

/// <summary>One row of the published results table.</summary>
/// <param name="EfSearch">Beam width used for the run.</param>
/// <param name="RecallAt1">Fraction of queries whose top hit is the true nearest neighbour.</param>
/// <param name="RecallAtK">Mean overlap between the top-k returned and the true top-k.</param>
/// <param name="QueriesPerSecond">Single-threaded throughput.</param>
/// <param name="MeanLatencyMs">Mean wall-clock time for one query.</param>
public sealed record RecallQpsPoint(
    int EfSearch,
    double RecallAt1,
    double RecallAtK,
    double QueriesPerSecond,
    double MeanLatencyMs);

/// <summary>
/// Measures recall against a dataset's published ground truth, and throughput, across a sweep
/// of <c>efSearch</c> values.
///
/// Reporting a single recall number is close to meaningless for an ANN index, because recall
/// and speed trade against each other continuously: any implementation can reach 99% recall by
/// widening the beam until it has effectively scanned everything. The honest unit of comparison
/// is the whole recall-versus-QPS curve, which is what ann-benchmarks publishes and what this
/// produces.
/// </summary>
public static class RecallBenchmark
{
    /// <summary>
    /// Encodes a base-vector index as a Guid so a search hit can be mapped back to a row in the
    /// ground truth without keeping a million-entry dictionary alive next to the index.
    /// </summary>
    public static Guid IdForBaseIndex(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, index);
        return new Guid(bytes);
    }

    public static int BaseIndexForId(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!id.TryWriteBytes(bytes)) throw new InvalidOperationException("Guid did not fit in 16 bytes.");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    public static BenchmarkReport Run(BenchmarkOptions options)
    {
        var dataset = options.Dataset;

        Console.WriteLine(
            $"Dataset {dataset.Name}: {dataset.Base.Count:N0} base vectors, {dataset.Queries.Count:N0} queries, " +
            $"dim {dataset.Base.Dimension}, ground truth depth {dataset.GroundTruth.Dimension}.");

        // The shipped ground truth indexes the *full* base set. Indexing a prefix of it and then
        // scoring against that truth silently invents misses for every neighbour that was left
        // out, which would understate recall without any visible sign that anything is wrong.
        for (int q = 0; q < dataset.GroundTruth.Count; q++)
        {
            var truth = dataset.GroundTruth[q];
            for (int i = 0; i < options.TopK && i < truth.Length; i++)
            {
                if (truth[i] >= dataset.Base.Count)
                {
                    throw new InvalidOperationException(
                        $"Ground truth for query {q} refers to base vector {truth[i]}, but only " +
                        $"{dataset.Base.Count:N0} base vectors were loaded. Recall against a truncated " +
                        "base set is not meaningful; drop --max-base or recompute the ground truth.");
                }
            }
        }

        if (options.TopK > dataset.GroundTruth.Dimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"topK of {options.TopK} exceeds the ground-truth depth of {dataset.GroundTruth.Dimension}.");
        }

        // --reuse-index opens an index left behind by an earlier --keep-index run instead of
        // rebuilding it. Building takes minutes to hours; sweeping --concurrency or --k does not
        // need a fresh graph, and the build time is reported as zero so it cannot be mistaken.
        bool reuse = options.ReuseIndex && File.Exists(options.IndexPath);
        if (!reuse && File.Exists(options.IndexPath)) File.Delete(options.IndexPath);

        using var db = new QvecDatabase(
            options.IndexPath,
            dim: dataset.Base.Dimension,
            max: dataset.Base.Count,
            maxNeighbors: options.MaxNeighbors,
            maxLayers: options.MaxLayers,
            distanceFunction: options.Distance,
            // Pinned so a published number can be re-derived. HNSW layer assignment is random by
            // default, so without this two runs of the identical command produce two different
            // graphs and recall moves by a point or two for no visible reason.
            indexSeed: options.IndexSeed,
            quantization: options.Quantization);

        TimeSpan buildTime;
        if (reuse)
        {
            if (db.LiveCount != dataset.Base.Count)
            {
                throw new InvalidOperationException(
                    $"'{options.IndexPath}' holds {db.LiveCount:N0} vectors but the dataset has {dataset.Base.Count:N0}; " +
                    "it was built from something else. Drop --reuse-index.");
            }

            Console.WriteLine($"Reusing index {options.IndexPath} ({db.LiveCount:N0} vectors); build time not measured.");
            buildTime = TimeSpan.Zero;
        }
        else
        {
            buildTime = BuildIndex(db, dataset, options);
        }

        long fileSizeBytes = new FileInfo(options.IndexPath).Length;
        Console.WriteLine(
            (reuse ? "Reused, " : $"Built in {buildTime.TotalSeconds:F1}s ({dataset.Base.Count / Math.Max(buildTime.TotalSeconds, 0.001):N0} inserts/s), ") +
            $"file {fileSizeBytes / 1024.0 / 1024.0:F1} MiB.");

        var points = new List<RecallQpsPoint>();
        foreach (int efSearch in options.EfSearchSweep)
        {
            points.Add(MeasureOne(db, dataset, options, efSearch));
            var last = points[^1];
            Console.WriteLine(
                $"  efSearch {efSearch,5}: recall@1 {last.RecallAt1,7:P2}  " +
                $"recall@{options.TopK} {last.RecallAtK,7:P2}  " +
                $"{last.QueriesPerSecond,9:N0} QPS  {last.MeanLatencyMs,7:F3} ms");
        }

        return new BenchmarkReport(
            dataset.Name,
            dataset.Base.Count,
            dataset.Base.Dimension,
            dataset.Queries.Count,
            options.Distance,
            options.MaxNeighbors,
            options.MaxLayers,
            options.Quantization,
            options.TopK,
            options.Concurrency,
            buildTime,
            fileSizeBytes,
            points);
    }

    private static TimeSpan BuildIndex(QvecDatabase db, AnnDataset dataset, BenchmarkOptions options)
    {
        Console.WriteLine($"Indexing {dataset.Base.Count:N0} vectors (M={options.MaxNeighbors}, layers={options.MaxLayers}, {options.Distance}) ...");

        var vector = new float[dataset.Base.Dimension];
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < dataset.Base.Count; i++)
        {
            dataset.Base[i].CopyTo(vector);
            db.AddEntry(vector, string.Empty, IdForBaseIndex(i));

            if (options.ProgressEvery > 0 && (i + 1) % options.ProgressEvery == 0)
            {
                Console.WriteLine(
                    $"  {i + 1:N0}/{dataset.Base.Count:N0} " +
                    $"({(i + 1) / stopwatch.Elapsed.TotalSeconds:N0} inserts/s)");
            }
        }

        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static RecallQpsPoint MeasureOne(
        QvecDatabase db,
        AnnDataset dataset,
        BenchmarkOptions options,
        int efSearch)
    {
        int queryCount = Math.Min(options.QueryCount, dataset.Queries.Count);
        int topK = options.TopK;

        // Warm up so the first measured query is not paying for page faults on the mapped file
        // and JIT of the search path. Without this the first efSearch in the sweep looks slow
        // for reasons that have nothing to do with efSearch.
        for (int q = 0; q < Math.Min(options.WarmupQueries, queryCount); q++)
        {
            db.Search(dataset.Queries.ToArray(q), topK, efSearch);
        }

        double recallAt1 = 0;
        double recallAtK = 0;

        var queries = new float[queryCount][];
        for (int q = 0; q < queryCount; q++) queries[q] = dataset.Queries.ToArray(q);

        var stopwatch = Stopwatch.StartNew();
        var results = new List<(Guid Id, float Score, string Metadata)>[queryCount];
        if (options.Concurrency <= 1)
        {
            for (int q = 0; q < queryCount; q++)
            {
                results[q] = db.Search(queries[q], topK, efSearch);
            }
        }
        else
        {
            // Each thread takes the next query off a shared counter, the way VectorDBBench's
            // concurrent clients each run their own serial loop. Aggregate QPS is queries over
            // wall-clock time, so a slow thread shows up honestly instead of being averaged away.
            int next = -1;
            var threads = new Thread[options.Concurrency];
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    while (true)
                    {
                        int q = Interlocked.Increment(ref next);
                        if (q >= queryCount) break;
                        results[q] = db.Search(queries[q], topK, efSearch);
                    }
                });
                threads[t].Start();
            }

            foreach (var thread in threads) thread.Join();
        }
        stopwatch.Stop();

        // Scoring happens outside the timed region: mapping Guids back to base indices is our
        // bookkeeping, not work the library does for a user.
        for (int q = 0; q < queryCount; q++)
        {
            var truth = dataset.GroundTruth[q];
            var returned = results[q];

            if (returned.Count > 0 && BaseIndexForId(returned[0].Id) == truth[0])
            {
                recallAt1++;
            }

            var truthSet = new HashSet<int>(topK);
            for (int i = 0; i < topK; i++) truthSet.Add(truth[i]);

            int hits = 0;
            for (int i = 0; i < returned.Count && i < topK; i++)
            {
                if (truthSet.Contains(BaseIndexForId(returned[i].Id))) hits++;
            }

            recallAtK += hits / (double)topK;
        }

        double seconds = stopwatch.Elapsed.TotalSeconds;

        // With N threads the wall time per query understates what one caller waits; multiply
        // back up so the column is an estimate of per-query latency under that load.
        double meanLatencyMs = stopwatch.Elapsed.TotalMilliseconds * Math.Max(options.Concurrency, 1) / queryCount;

        return new RecallQpsPoint(
            efSearch,
            recallAt1 / queryCount,
            recallAtK / queryCount,
            queryCount / Math.Max(seconds, 1e-9),
            meanLatencyMs);
    }
}

/// <summary>Everything a benchmark run needs, so the parameters can be printed with the results.</summary>
public sealed class BenchmarkOptions
{
    public required AnnDataset Dataset { get; init; }
    public required string IndexPath { get; init; }
    public DistanceFunction Distance { get; init; } = DistanceFunction.Euclidean;
    public int MaxNeighbors { get; init; } = 32;
    public int MaxLayers { get; init; } = 5;
    public VectorQuantization Quantization { get; init; } = VectorQuantization.None;
    public int TopK { get; init; } = 10;
    public int QueryCount { get; init; } = int.MaxValue;
    public int WarmupQueries { get; init; } = 100;
    public int ProgressEvery { get; init; } = 100_000;
    public IReadOnlyList<int> EfSearchSweep { get; init; } = [10, 20, 40, 80, 160, 320, 640];

    /// <summary>Number of threads issuing queries; 1 is the single-threaded latency view.</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Open an existing index at <see cref="IndexPath"/> instead of rebuilding it.</summary>
    public bool ReuseIndex { get; init; }

    /// <summary>
    /// Seed for the HNSW layer assignment, so a published curve can be reproduced exactly.
    /// </summary>
    public int IndexSeed { get; init; } = 20240001;
}

/// <summary>A complete run, ready to be turned into the README table.</summary>
public sealed record BenchmarkReport(
    string Dataset,
    int BaseCount,
    int Dimension,
    int QueryCount,
    DistanceFunction Distance,
    int MaxNeighbors,
    int MaxLayers,
    VectorQuantization Quantization,
    int TopK,
    int Concurrency,
    TimeSpan BuildTime,
    long FileSizeBytes,
    IReadOnlyList<RecallQpsPoint> Points)
{
    /// <summary>Renders the run as a Markdown section, parameters included.</summary>
    public string ToMarkdown(string hardware)
    {
        var writer = new StringWriter();

        writer.WriteLine($"Dataset: **{Dataset}** — {BaseCount:N0} base vectors, {Dimension} dimensions, {QueryCount:N0} queries.");
        writer.WriteLine($"Metric: `{Distance}`. Index: `maxNeighbors = {MaxNeighbors}`, `maxLayers = {MaxLayers}`, `quantization = {Quantization}`.");
        writer.WriteLine($"Build: {BuildTime.TotalSeconds:F1} s ({BaseCount / Math.Max(BuildTime.TotalSeconds, 0.001):N0} inserts/s). File: {FileSizeBytes / 1024.0 / 1024.0:F1} MiB.");
        writer.WriteLine($"Hardware: {hardware}. {(Concurrency <= 1 ? "Single-threaded queries." : $"{Concurrency} concurrent query threads; QPS is aggregate, latency is estimated per query under that load.")}");
        writer.WriteLine();
        writer.WriteLine($"| efSearch | recall@1 | recall@{TopK} | QPS | mean latency |");
        writer.WriteLine("| ---: | ---: | ---: | ---: | ---: |");

        foreach (var point in Points)
        {
            writer.WriteLine(
                $"| {point.EfSearch} | {point.RecallAt1:P1} | {point.RecallAtK:P1} | " +
                $"{point.QueriesPerSecond:N0} | {point.MeanLatencyMs:F3} ms |");
        }

        return writer.ToString();
    }
}
