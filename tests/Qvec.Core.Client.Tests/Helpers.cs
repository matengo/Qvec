using Qvec.Core;

namespace Qvec.Core.Client.Tests;

/// <summary>
/// Owns a throwaway .qvec file in a unique temp directory and deletes the whole
/// directory on dispose. Tests must never write database files into the repo.
/// </summary>
public sealed class TempDb : IDisposable
{
    public string Path { get; }
    private readonly string _dir;

    public TempDb(string fileName = "test.qvec")
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qvec-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Path = System.IO.Path.Combine(_dir, fileName);
    }

    public QvecDatabase Open(
        int dim = 4,
        int max = 100,
        int maxNeighbors = 16,
        int maxLayers = 4,
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
    public static float[] Basis(int dim, int index)
    {
        var v = new float[dim];
        v[index] = 1f;
        return v;
    }
}

/// <summary>
/// Test category names used with xUnit traits.
///
/// CI's blocking gate runs:   dotnet test --filter "Category!=KnownDefect"
/// </summary>
public static class TestCategories
{
    public const string KnownDefect = "KnownDefect";

    /// <summary>Slow, statistical tests (recall measurement, large datasets).</summary>
    public const string Slow = "Slow";
}
