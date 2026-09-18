using Qvec.Core;
using Qvec.Core.Sync;

namespace Qvec.Sync.Tests;

public static class TestCategories
{
    public const string Slow = "Slow";
}

/// <summary>A throwaway directory holding one or more .qvec files and a sync folder; deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qvec-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Dir(string name)
    {
        string p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public QvecDatabase OpenDb(string name, int dim = 8, int max = 200, long logCapacity = 4096, VectorQuantization quantization = VectorQuantization.None)
        => new(File(name), dim, max, 32, 5, DistanceFunction.DotProduct, null, quantization,
            new ChangeTrackingOptions { LogCapacity = logCapacity });

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* best effort: a mapped file may still be held on Windows */ }
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

    public static float[] Random(int dim, Random rng)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return v;
    }
}

public static class DbAssert
{
    /// <summary>Both databases hold the same live documents with the same versions, metadata and vectors.</summary>
    public static void SameContent(QvecDatabase x, QvecDatabase y)
    {
        Assert.Equal(Snapshot(x), Snapshot(y));

        static List<(Guid, EntryVersion, string, string)> Snapshot(QvecDatabase db)
        {
            var rows = new List<(Guid, EntryVersion, string, string)>();
            foreach (var id in db.GetChanges(0, int.MaxValue).Items.Select(i => i.DocumentId).Distinct())
            {
                var stored = db.GetByGuid(id);
                if (stored is null) continue;
                db.TryGetVersion(id, out var v);
                rows.Add((id, v, stored.Value.Metadata, string.Join(",", stored.Value.Vector)));
            }
            return rows.OrderBy(r => r.Item1).ToList();
        }
    }
}
