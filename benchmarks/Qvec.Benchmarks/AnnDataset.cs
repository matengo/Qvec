using System.Formats.Tar;
using System.IO.Compression;
using Qvec.Core;

namespace Qvec.Benchmarks;

/// <summary>
/// One of the TexMex ANN corpora: a set of base vectors to index, a set of held-out query
/// vectors, and the exact nearest neighbours of each query, precomputed by the dataset
/// authors.
///
/// Using the shipped ground truth rather than our own brute-force scan is the point of the
/// exercise. A benchmark that computes its own truth only proves the index agrees with our
/// own linear scan, which shares every assumption the index makes — the same distance
/// function, the same normalisation, the same tie-breaking. The published ground truth is
/// independent of all of that, and it is what every other vector database reports against,
/// so the numbers are comparable.
/// </summary>
public sealed class AnnDataset
{
    private AnnDataset(string name, DistanceFunction metric, VecBlock<float> baseVectors, VecBlock<float> queries, VecBlock<int> groundTruth)
    {
        Name = name;
        Metric = metric;
        Base = baseVectors;
        Queries = queries;
        GroundTruth = groundTruth;
    }

    public string Name { get; }

    /// <summary>
    /// The metric the ground truth was computed under. Measuring under any other metric compares
    /// the index against neighbours the dataset does not mean, and the recall number is noise.
    /// </summary>
    public DistanceFunction Metric { get; }

    /// <summary>Vectors to index.</summary>
    public VecBlock<float> Base { get; }

    /// <summary>Held-out query vectors; none of these appear in <see cref="Base"/>.</summary>
    public VecBlock<float> Queries { get; }

    /// <summary>
    /// Row <c>q</c> holds the indices into <see cref="Base"/> of the exact nearest neighbours
    /// of query <c>q</c>, nearest first.
    /// </summary>
    public VecBlock<int> GroundTruth { get; }

    /// <summary>The known corpora, keyed by the name used on the command line.</summary>
    public static IReadOnlyDictionary<string, AnnDatasetSource> Known { get; } =
        new Dictionary<string, AnnDatasetSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["siftsmall"] = new(
                "siftsmall",
                "ftp://ftp.irisa.fr/local/texmex/corpus/siftsmall.tar.gz",
                BaseCount: 10_000,
                Dimension: 128,
                DistanceFunction.Euclidean,
                DatasetFormat.TexMex),
            ["sift"] = new(
                "sift",
                "ftp://ftp.irisa.fr/local/texmex/corpus/sift.tar.gz",
                BaseCount: 1_000_000,
                Dimension: 128,
                DistanceFunction.Euclidean,
                DatasetFormat.TexMex),
            ["gist"] = new(
                "gist",
                "ftp://ftp.irisa.fr/local/texmex/corpus/gist.tar.gz",
                BaseCount: 1_000_000,
                Dimension: 960,
                DistanceFunction.Euclidean,
                DatasetFormat.TexMex),
            // The VectorDBBench corpora Zvec, Milvus and most vendor benchmarks publish against.
            // Cohere ground truth is cosine; VectorDBBench scores recall@100.
            ["cohere100k"] = new(
                "cohere_small_100k",
                "https://assets.zilliz.com/benchmark/cohere_small_100k/",
                BaseCount: 100_000,
                Dimension: 768,
                DistanceFunction.Cosine,
                DatasetFormat.VectorDbBenchParquet),
            ["cohere1m"] = new(
                "cohere_medium_1m",
                "https://assets.zilliz.com/benchmark/cohere_medium_1m/",
                BaseCount: 1_000_000,
                Dimension: 768,
                DistanceFunction.Cosine,
                DatasetFormat.VectorDbBenchParquet),
        };

    /// <summary>
    /// Loads a dataset from <paramref name="directory"/>: either the four TexMex
    /// <c>{name}_*.fvecs</c> / <c>.ivecs</c> files, or the VectorDBBench parquet triple.
    /// </summary>
    /// <param name="maxBaseCount">
    /// Index only the first N base vectors. Note that the shipped ground truth refers to the
    /// full base set, so a truncated run is only meaningful if the caller recomputes truth;
    /// <see cref="RecallBenchmark"/> refuses to report recall against a truncated base.
    /// </param>
    public static AnnDataset Load(string directory, string name, int maxBaseCount = int.MaxValue)
    {
        if (!Known.TryGetValue(name, out var source))
        {
            throw new ArgumentException(
                $"Unknown dataset '{name}'. Known datasets: {string.Join(", ", Known.Keys)}.",
                nameof(name));
        }

        VecBlock<float> baseVectors;
        VecBlock<float> queries;
        VecBlock<int> groundTruth;

        if (source.Format == DatasetFormat.VectorDbBenchParquet)
        {
            (baseVectors, queries, groundTruth) = ParquetDataset.Load(directory, maxBaseCount);
        }
        else
        {
            string Require(string suffix)
            {
                string path = Path.Combine(directory, $"{source.Name}_{suffix}");
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"Dataset file '{path}' was not found. Pass --download to fetch it, or point " +
                        "--data at a directory holding the extracted TexMex files.",
                        path);
                }

                return path;
            }

            baseVectors = VecFile.ReadFvecs(Require("base.fvecs"), maxBaseCount);
            queries = VecFile.ReadFvecs(Require("query.fvecs"));
            groundTruth = VecFile.ReadIvecs(Require("groundtruth.ivecs"));
        }

        if (queries.Dimension != baseVectors.Dimension)
        {
            throw new InvalidDataException(
                $"Query vectors are {queries.Dimension}-dimensional but base vectors are " +
                $"{baseVectors.Dimension}-dimensional.");
        }

        if (groundTruth.Count != queries.Count)
        {
            throw new InvalidDataException(
                $"Ground truth has {groundTruth.Count} rows but there are {queries.Count} queries.");
        }

        return new AnnDataset(name, source.Metric, baseVectors, queries, groundTruth);
    }

    /// <summary>
    /// Downloads a known dataset into <paramref name="cacheDirectory"/> unless the files are
    /// already there. Returns the directory that holds the files.
    /// </summary>
    public static async Task<string> EnsureDownloadedAsync(
        string cacheDirectory,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!Known.TryGetValue(name, out var source))
        {
            throw new ArgumentException(
                $"Unknown dataset '{name}'. Known datasets: {string.Join(", ", Known.Keys)}.",
                nameof(name));
        }

        Directory.CreateDirectory(cacheDirectory);
        string extracted = Path.Combine(cacheDirectory, source.Name);

        if (source.Format == DatasetFormat.VectorDbBenchParquet)
        {
            Directory.CreateDirectory(extracted);
            foreach (string file in new[] { ParquetDataset.TestFile, ParquetDataset.NeighborsFile, ParquetDataset.TrainFile })
            {
                string destination = Path.Combine(extracted, file);
                if (File.Exists(destination)) continue;

                string url = source.Url + file;
                Console.WriteLine($"Downloading {url} ...");
                await DownloadAsync(url, destination, cancellationToken).ConfigureAwait(false);
            }

            return extracted;
        }

        if (File.Exists(Path.Combine(extracted, $"{source.Name}_base.fvecs")))
        {
            Console.WriteLine($"Using cached dataset at {extracted}");
            return extracted;
        }

        string archive = Path.Combine(cacheDirectory, $"{source.Name}.tar.gz");
        if (!File.Exists(archive))
        {
            Console.WriteLine($"Downloading {source.Url} ...");
            await DownloadAsync(source.Url, archive, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"Extracting {archive} ...");
        await using (var file = File.OpenRead(archive))
        await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        {
            await TarFile.ExtractToDirectoryAsync(gzip, cacheDirectory, overwriteFiles: true, cancellationToken)
                .ConfigureAwait(false);
        }

        return extracted;
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        string temporary = destination + ".partial";

        // The TexMex corpora are served over FTP, which HttpClient does not speak. WebClient is
        // obsolete and WebRequest.Create refuses ftp:// on modern .NET, so the URL is handed to
        // curl, which ships with Windows 10+, macOS and every mainstream Linux distribution.
        var startInfo = new System.Diagnostics.ProcessStartInfo("curl")
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--fail");
        startInfo.ArgumentList.Add("--location");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(temporary);
        startInfo.ArgumentList.Add(url);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start curl. Install curl or download the dataset manually.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw new InvalidOperationException(
                $"curl exited with code {process.ExitCode} downloading {url}. " +
                "Download the archive manually and extract it into the --data directory.");
        }

        File.Move(temporary, destination, overwrite: true);
    }
}

/// <summary>Where a known dataset lives and what it should contain once unpacked.</summary>
/// <param name="Name">Directory name under the cache (and file-name prefix for TexMex).</param>
/// <param name="Url">Download location: a tarball for TexMex, a directory URL for parquet.</param>
/// <param name="BaseCount">Expected number of base vectors, for a sanity check.</param>
/// <param name="Dimension">Expected dimensionality, for a sanity check.</param>
/// <param name="Metric">Metric the shipped ground truth was computed under.</param>
/// <param name="Format">On-disk layout.</param>
public sealed record AnnDatasetSource(
    string Name,
    string Url,
    int BaseCount,
    int Dimension,
    DistanceFunction Metric,
    DatasetFormat Format);

public enum DatasetFormat
{
    /// <summary><c>.fvecs</c> / <c>.ivecs</c> as shipped by the TexMex corpora.</summary>
    TexMex,

    /// <summary><c>train/test/neighbors.parquet</c> as shipped by VectorDBBench.</summary>
    VectorDbBenchParquet,
}
