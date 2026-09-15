using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// Owns a throwaway .qvec file in a unique temp directory and deletes the whole
/// directory on dispose. Tests must never write database files into the repo,
/// and each test needs its own path so the suite can run in parallel.
/// </summary>
public sealed class TempDb : IDisposable
{
    public string Path { get; }
    private readonly string _dir;

    public TempDb(string fileName = "test.qvec")
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qvec-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Path = System.IO.Path.Combine(_dir, fileName);
    }

    /// <summary>Path to a sibling file in the same temp directory.</summary>
    public string Sibling(string fileName) => System.IO.Path.Combine(_dir, fileName);

    public QvecDatabase Open(
        int dim = 8,
        int max = 100,
        int maxNeighbors = 32,
        int maxLayers = 5,
        DistanceFunction distance = DistanceFunction.DotProduct,
        int? indexSeed = null)
        => new(Path, dim, max, maxNeighbors, maxLayers, distance, indexSeed);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort: a mapped file may still be held on Windows */ }
    }
}

/// <summary>Deterministic vector helpers so tests never depend on ambient randomness.</summary>
public static class Vec
{
    /// <summary>A unit basis vector, e.g. Basis(8, 2) => [0,0,1,0,0,0,0,0].</summary>
    public static float[] Basis(int dim, int index)
    {
        var v = new float[dim];
        v[index] = 1f;
        return v;
    }

    public static float[] Random(int dim, Random rng)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return v;
    }

    public static float[] Filled(int dim, float value)
    {
        var v = new float[dim];
        Array.Fill(v, value);
        return v;
    }

    /// <summary>
    /// Vectors drawn from a mixture of Gaussian clusters, which is a far better stand-in for
    /// real embeddings than <see cref="Random"/>.
    ///
    /// Uniformly random vectors in high dimensions are all roughly equidistant from each other,
    /// so "the nearest neighbour" is barely distinguishable from the tenth nearest and any
    /// graph index looks bad. Real embeddings are strongly clustered: the nearest neighbour is
    /// genuinely much closer than the rest, and that gap is what HNSW navigates by. Measuring
    /// only on uniform data therefore understates the index and, worse, hides regressions that
    /// would only show up on the data users actually have.
    /// </summary>
    public sealed class ClusteredVectors
    {
        private readonly float[][] _centroids;
        private readonly float _spread;
        private int _next;

        public ClusteredVectors(int dim, int clusterCount, int seed, float spread = 0.08f)
        {
            var rng = new Random(seed);
            _centroids = new float[clusterCount][];
            for (int c = 0; c < clusterCount; c++) _centroids[c] = Random(dim, rng);
            _spread = spread;
        }

        /// <summary>
        /// Matches the delegate shape taken by the recall harness. Successive calls walk the
        /// clusters in turn, so the set is evenly populated regardless of how many are drawn.
        /// </summary>
        public float[] Next(int dim, Random rng)
        {
            var centroid = _centroids[_next++ % _centroids.Length];
            var v = new float[dim];

            for (int i = 0; i < dim; i++)
            {
                v[i] = centroid[i] + (_spread * Gaussian(rng));
            }

            return v;
        }

        // Box-Muller. Random has no Gaussian of its own, and a sum-of-uniforms approximation
        // would put a hard bound on the tails, which is exactly where the interesting
        // near-miss queries live.
        private static float Gaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
    }
}
