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
        DistanceFunction distance = DistanceFunction.DotProduct)
        => new(Path, dim, max, maxNeighbors, maxLayers, distance);

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
}
