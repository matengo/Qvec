using System.Runtime.InteropServices;
using Qvec.Core;

namespace Qvec.Core.Tests;

/// <summary>
/// A Qvec file is laid out for its full <c>max</c> row capacity the moment it is created. Finding
/// #21 in the review was that this made an empty database for a million OpenAI embeddings cost
/// 7.3 GB of disk before a single vector was inserted. The fix is to create the file sparse, so
/// the cost is address space rather than allocated clusters.
/// </summary>
public class SparseAllocationTests
{
    /// <summary>
    /// Sized for 250 000 rows at 1536 dimensions, so the logical file is roughly 1.5 GB. If the
    /// clusters were allocated eagerly this test would both take a long time and need 1.5 GB of
    /// free space; sparse, it costs only the header and the pages the first insert touches.
    /// </summary>
    [Fact]
    public void Create_LargeDatabase_DoesNotPhysicallyAllocateTheWholeFile()
    {
        using var temp = new TempDb();
        using (var db = temp.Open(dim: 1536, max: 250_000))
        {
            db.AddEntry(Vec.Basis(1536, 0), "{}");
        }

        long logical = new FileInfo(temp.Path).Length;
        Assert.True(logical > 1_000_000_000,
            $"Expected a multi-gigabyte logical layout for this configuration but got {logical} bytes.");

        long? physical = TryGetAllocatedSize(temp.Path);
        if (physical is null) return; // Platform without a portable way to read allocated size.

        Assert.True(physical.Value < logical / 10,
            $"Expected the file to be sparse but {physical.Value} of {logical} bytes are allocated.");
    }

    [Fact]
    public void Create_ThenReopen_PreservesDataInASparseFile()
    {
        using var temp = new TempDb();
        Guid id;
        using (var db = temp.Open(dim: 64, max: 50_000))
        {
            id = db.AddEntry(Vec.Basis(64, 7), "{\"kind\":\"sparse\"}");
        }

        using var reopened = temp.Open(dim: 64, max: 50_000);
        Assert.True(reopened.IsHealthy());
        Assert.Equal("{\"kind\":\"sparse\"}", reopened.GetByGuid(id)?.Metadata);
    }

    private static long? TryGetAllocatedSize(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;

        uint low = GetCompressedFileSizeW(path, out uint high);
        if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0) return null;
        return ((long)high << 32) | low;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);
}
