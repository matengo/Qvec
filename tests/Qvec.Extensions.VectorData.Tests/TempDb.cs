using Qvec.Core;

namespace Qvec.Extensions.VectorData.Tests;

public sealed class TempDb : IDisposable
{
    public string Path { get; }
    private readonly string _dir;

    public TempDb(string fileName = "test.qvec")
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qvec-vectordata-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Path = System.IO.Path.Combine(_dir, fileName);
    }

    public string Sibling(string fileName) => System.IO.Path.Combine(_dir, fileName);

    public QvecDatabase Open(int dim = 3, int max = 100, int maxNeighbors = 8, int maxLayers = 3, DistanceFunction distance = DistanceFunction.DotProduct)
        => new(Path, dim, max, maxNeighbors, maxLayers, distance);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }
}

public static class Vec
{
    public static float[] Basis(int dim, int index)
    {
        var v = new float[dim];
        v[index] = 1f;
        return v;
    }
}
