using Qvec.Benchmarks;
using Qvec.Core;

// Honest recall/QPS measurement against a published ANN corpus.
//
//   dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --dataset siftsmall --download
//   dotnet run -c Release --project benchmarks/Qvec.Benchmarks -- --dataset sift --download --m 32
//
// This is deliberately not part of the test suite. SIFT-1M is a 500 MB download and a
// multi-minute index build, neither of which belongs in a CI gate; the regression gates live in
// Qvec.Core.Tests and run on generated data.

var arguments = CommandLine.Parse(args);

if (arguments.Help)
{
    CommandLine.PrintUsage();
    return 0;
}

if (!arguments.Flag("allow-throttling"))
{
    Console.WriteLine(PowerThrottling.TryDisable()
        ? "Power throttling (EcoQoS) disabled for this process."
        : "Power throttling exemption not available; results may vary with the OS power plan.");
}

string datasetName = arguments.Value("dataset") ?? "siftsmall";
string cacheDirectory = arguments.Value("data")
    ?? Path.Combine(Path.GetTempPath(), "qvec-ann-datasets");

string directory;
if (arguments.Flag("download"))
{
    directory = await AnnDataset.EnsureDownloadedAsync(cacheDirectory, datasetName);
}
else
{
    string directoryName = AnnDataset.Known.TryGetValue(datasetName, out var source) ? source.Name : datasetName;
    directory = Path.Combine(cacheDirectory, directoryName);
    if (!Directory.Exists(directory)) directory = cacheDirectory;
}

int maxBase = arguments.Int("max-base") ?? int.MaxValue;
var dataset = AnnDataset.Load(directory, datasetName, maxBase);

string indexPath = arguments.Value("index")
    ?? Path.Combine(Path.GetTempPath(), $"qvec-bench-{datasetName}.qvec");

var options = new BenchmarkOptions
{
    Dataset = dataset,
    IndexPath = indexPath,
    // Default to the metric the ground truth was computed under; overriding it is allowed but
    // produces a recall number that means nothing (see --help).
    Distance = arguments.Value("distance") is { } distanceName
        ? Enum.Parse<DistanceFunction>(distanceName, ignoreCase: true)
        : dataset.Metric,
    MaxNeighbors = arguments.Int("m") ?? 32,
    MaxLayers = arguments.Int("layers") ?? 5,
    Quantization = Enum.Parse<VectorQuantization>(arguments.Value("quantization") ?? nameof(VectorQuantization.None), ignoreCase: true),
    TopK = arguments.Int("k") ?? 10,
    QueryCount = arguments.Int("queries") ?? int.MaxValue,
    EfSearchSweep = arguments.Ints("ef") ?? [10, 20, 40, 80, 160, 320, 640],
    Concurrency = arguments.Int("concurrency") ?? 1,
    QueryPasses = arguments.Int("passes") ?? 1,
    BuildThreads = arguments.Int("threads") ?? 1,
    ReuseIndex = arguments.Flag("reuse-index"),
    ChangeTracking = arguments.Flag("tracking"),
    SyncItems = arguments.Int("sync-items") ?? 100_000,
    SyncBatchSize = arguments.Int("sync-batch") ?? 500,
};

var report = RecallBenchmark.Run(options);

string hardware = arguments.Value("hardware")
    ?? $"{Environment.ProcessorCount} logical cores, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

Console.WriteLine();
Console.WriteLine(report.ToMarkdown(hardware));

string? output = arguments.Value("out");
if (output is not null)
{
    File.WriteAllText(output, report.ToMarkdown(hardware));
    Console.WriteLine($"Wrote {output}");
}

// --keep-index leaves the file behind so two builds can be compared byte for byte, which is
// how a change to the insert path proves it did not alter the graph.
if (!arguments.Flag("keep-index"))
{
    try { File.Delete(indexPath); } catch { /* best effort */ }
}

return 0;

/// <summary>
/// A very small <c>--key value</c> / <c>--flag</c> parser. Bringing in a command-line package
/// for six options would be the larger cost, and this project is AOT-published.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public bool Help => _values.ContainsKey("help") || _values.ContainsKey("h");

    public static CommandLine Parse(string[] args)
    {
        var parsed = new CommandLine();

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal)) continue;

            string key = argument[2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : null;

            parsed._values[key] = value;
        }

        return parsed;
    }

    public string? Value(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public bool Flag(string key) => _values.ContainsKey(key);

    public int? Int(string key) =>
        Value(key) is { } raw && int.TryParse(raw, out int parsed) ? parsed : null;

    public int[]? Ints(string key) =>
        Value(key)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();

    public static void PrintUsage()
    {
        Console.WriteLine("""
            Qvec recall/QPS benchmark.

              --dataset <name>    siftsmall (default), sift, gist, cohere100k, cohere1m
              --download          fetch and cache the dataset if it is not already present
              --data <dir>        dataset cache directory (default: %TEMP%/qvec-ann-datasets)
              --distance <name>   Euclidean, Cosine or DotProduct; default is the dataset's own metric
              --m <int>           maxNeighbors, default 32
              --layers <int>      maxLayers, default 5
              --quantization <q>  None (default), Int8 or Int8Rescored; int8 stores one byte per
                                  dimension, int8rescored also keeps the floats and re-ranks on them
              --k <int>           top-k for recall@k, default 10
              --ef <list>         comma-separated efSearch sweep, default 10,20,40,80,160,320,640
              --queries <int>     limit the number of queries
              --concurrency <n>   query threads, default 1; QPS is aggregate over all threads
              --passes <n>        run the query set n times per efSearch row (default 1); use
                                  5-10 on Cohere, whose 1,000 queries finish in under a second
              --threads <n>       index build threads (AddEntries), default 1; 0 = all cores.
                                  Builds with more than one thread are not byte-reproducible.
              --max-base <int>    index only a prefix of the base set (invalidates recall)
              --index <path>      where to put the .qvec file
              --keep-index        do not delete the .qvec file afterwards
              --reuse-index       open the existing --index file instead of rebuilding it
              --tracking          build with change tracking on (replica id, per-document version,
                                  change-log ring sized to the dataset) and, after the build, measure
                                  GetChanges + wire encoding + ApplyChanges into a fresh replica
              --sync-items <n>    documents to push through that sync step, default 100000; 0 skips it
              --sync-batch <n>    documents per ChangeBatch in the sync step, default 500
              --hardware <text>   hardware description to print with the results
              --out <path>        also write the Markdown report to a file

            Note: SIFT and GIST ground truth is Euclidean, Cohere is Cosine. Measuring a dataset
            under another metric compares against neighbours that are not the ones the dataset
            means, and the resulting recall number says nothing useful.

            VectorDBBench (what Zvec and Milvus publish against) reports recall@100 under
            12-20 concurrent clients; use --k 100 --concurrency 16 to compare with those.
            """);
    }
}
