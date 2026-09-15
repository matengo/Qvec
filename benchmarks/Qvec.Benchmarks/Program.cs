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
    directory = Path.Combine(cacheDirectory, datasetName);
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
    Distance = Enum.Parse<DistanceFunction>(arguments.Value("distance") ?? nameof(DistanceFunction.Euclidean), ignoreCase: true),
    MaxNeighbors = arguments.Int("m") ?? 32,
    MaxLayers = arguments.Int("layers") ?? 5,
    TopK = arguments.Int("k") ?? 10,
    QueryCount = arguments.Int("queries") ?? int.MaxValue,
    EfSearchSweep = arguments.Ints("ef") ?? [10, 20, 40, 80, 160, 320, 640],
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

              --dataset <name>    siftsmall (default), sift, gist
              --download          fetch and cache the dataset if it is not already present
              --data <dir>        dataset cache directory (default: %TEMP%/qvec-ann-datasets)
              --distance <name>   Euclidean (default), Cosine, DotProduct
              --m <int>           maxNeighbors, default 32
              --layers <int>      maxLayers, default 5
              --k <int>           top-k for recall@k, default 10
              --ef <list>         comma-separated efSearch sweep, default 10,20,40,80,160,320,640
              --queries <int>     limit the number of queries
              --max-base <int>    index only a prefix of the base set (invalidates recall)
              --index <path>      where to put the .qvec file
              --keep-index        do not delete the .qvec file afterwards
              --hardware <text>   hardware description to print with the results
              --out <path>        also write the Markdown report to a file

            Note: SIFT and GIST ground truth is Euclidean. Measuring them under Cosine or
            DotProduct compares against neighbours that are not the ones the dataset means,
            and the resulting recall number says nothing useful.
            """);
    }
}
