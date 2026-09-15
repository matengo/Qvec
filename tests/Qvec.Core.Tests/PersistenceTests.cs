using System.Text;
using Qvec.Core;
using Qvec.Core.Format;

namespace Qvec.Core.Tests;

/// <summary>
/// Durability and on-disk format round-trip coverage for the memory-mapped
/// persistence file.
/// </summary>
public class PersistenceTests
{
    private const int HeaderSize = V4Header.HeaderSizeValue;

    private const int MagicNumberOffset = V4Header.MagicNumberOffset;
    private const int VersionOffset = V4Header.VersionOffset;

    public static TheoryData<DistanceFunction, int> DistanceAndDimensionCases => new()
    {
        { DistanceFunction.DotProduct, 2 },
        { DistanceFunction.DotProduct, 4 },
        { DistanceFunction.Cosine, 2 },
        { DistanceFunction.Cosine, 4 },
    };

    [Theory]
    [MemberData(nameof(DistanceAndDimensionCases))]
    public void Entries_RoundTripWithIdenticalConstructorArguments(DistanceFunction distance, int dim)
    {
        using var temp = new TempDb();
        var vectors = Enumerable.Range(0, dim).Select(i => Vec.Basis(dim, i)).ToArray();
        var metadata = Enumerable.Range(0, dim).Select(i => $$"""{"kind":"roundtrip","index":{{i}}}""").ToArray();
        Guid[] ids;

        using (var db = temp.Open(dim: dim, max: 8, maxNeighbors: 4, maxLayers: 2, distance: distance))
        {
            ids = vectors.Select((v, i) => db.AddEntry(v.ToArray(), metadata[i])).ToArray();
            Assert.Equal(dim, db.GetCount());
            Assert.Equal(dim, db.LiveCount);
        }

        using (var reopened = temp.Open(dim: dim, max: 8, maxNeighbors: 4, maxLayers: 2, distance: distance))
        {
            Assert.Equal(dim, reopened.GetCount());
            Assert.Equal(dim, reopened.LiveCount);
            Assert.Equal(0, reopened.DeletedCount);
            Assert.True(reopened.IsHealthy());

            for (int i = 0; i < ids.Length; i++)
            {
                var entry = RequireEntry(reopened, ids[i]);
                Assert.Equal(vectors[i], entry.Vector);
                Assert.Equal(metadata[i], entry.Metadata);

                var results = reopened.SearchSimple(vectors[i].ToArray(), topK: dim);
                Assert.Contains(results, r => r.Id == ids[i] && r.Metadata == metadata[i]);
            }
        }
    }

    /// <summary>
    /// Defect: existing files are mapped with section offsets computed from the
    /// caller's constructor arguments before the persisted header is honored.
    /// Reopening with the wrong capacity currently reports a healthy database
    /// with the right count while GUID lookup silently misses the stored row.
    /// </summary>
    [Fact]
    public void Reopen_WithMismatchedMax_EitherHonorsHeaderOrThrowsFormatException()
    {
        AssertMismatchedOpenIsSafe((temp, id) =>
            temp.Open(dim: 4, max: 16, maxNeighbors: 3, maxLayers: 2, distance: DistanceFunction.DotProduct));
    }

    /// <summary>
    /// Defect: the persisted VectorDimension is read only after the vector,
    /// graph, metadata, GUID, and tombstone section offsets were derived from
    /// the caller's mismatched dimension.
    /// </summary>
    [Fact]
    public void Reopen_WithMismatchedDimension_EitherHonorsHeaderOrThrowsFormatException()
    {
        AssertMismatchedOpenIsSafe((temp, id) =>
            temp.Open(dim: 6, max: 8, maxNeighbors: 3, maxLayers: 2, distance: DistanceFunction.DotProduct));
    }

    /// <summary>
    /// Defect: the persisted MaxNeighbors is read only after graph-dependent
    /// section offsets were computed from the caller's mismatched value.
    /// </summary>
    [Fact]
    public void Reopen_WithMismatchedMaxNeighbors_EitherHonorsHeaderOrThrowsFormatException()
    {
        AssertMismatchedOpenIsSafe((temp, id) =>
            temp.Open(dim: 4, max: 8, maxNeighbors: 5, maxLayers: 2, distance: DistanceFunction.DotProduct));
    }

    /// <summary>
    /// Defect: the constructor never validates DbHeader.MagicNumber. Corrupting
    /// just that field should reject the file with QvecFormatException instead
    /// of constructing a database that later behaves nonsensically.
    /// </summary>
    [Fact]
    public void Open_WithInvalidMagicNumber_ThrowsFormatException()
    {
        using var temp = new TempDb();
        CreateDisposedDatabase(temp);
        PatchInt32(temp.Path, MagicNumberOffset, 0);

        Assert.Throws<QvecFormatException>(() =>
        {
            using var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2);
        });
    }

    /// <summary>
    /// Defect: new files are written as version 3, but the constructor only
    /// migrates versions less than 2 and does not reject a future version that
    /// this implementation cannot safely interpret.
    /// </summary>
    [Fact]
    public void Open_WithFutureHeaderVersion_ThrowsFormatException()
    {
        using var temp = new TempDb();
        CreateDisposedDatabase(temp);
        PatchInt32(temp.Path, VersionOffset, 99);

        Assert.Throws<QvecFormatException>(() =>
        {
            using var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2);
        });
    }

    /// <summary>
    /// Defect: a zero-length existing file is expanded and interpreted as a
    /// database with a zeroed header instead of being rejected as not a Qvec
    /// file.
    /// </summary>
    [Fact]
    public void Open_WithZeroLengthFile_ThrowsFormatException()
    {
        using var temp = new TempDb();
        File.WriteAllBytes(temp.Path, Array.Empty<byte>());

        Assert.Throws<QvecFormatException>(() =>
        {
            using var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2);
        });
    }

    /// <summary>
    /// Defect: a file with a valid header but a truncated body is silently
    /// extended by FileMode.OpenOrCreate rather than being rejected.
    /// </summary>
    [Fact]
    public void Open_WithTruncatedBody_ThrowsFormatException()
    {
        using var temp = new TempDb();
        CreateDisposedDatabase(temp);

        using (var stream = new FileStream(temp.Path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(HeaderSize + 8);
        }

        Assert.Throws<QvecFormatException>(() =>
        {
            using var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2);
        });
    }

    [Fact]
    public void Dispose_PersistsDataVisibleToImmediateReopen()
    {
        using var temp = new TempDb();
        var first = new Guid("11111111-1111-1111-1111-111111111111");
        var second = new Guid("22222222-2222-2222-2222-222222222222");

        using (var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2))
        {
            db.AddEntry(new[] { 1f, 2f, 3f, 4f }, "first", first);
            db.AddEntry(new[] { 4f, 3f, 2f, 1f }, "second", second);
        }

        using var reopened = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2);
        Assert.Equal(2, reopened.GetCount());
        Assert.Equal("first", RequireEntry(reopened, first).Metadata);
        Assert.Equal("second", RequireEntry(reopened, second).Metadata);
    }

    [Fact]
    public void Dispose_PersistsExpectedRawFileLengthAndMetadataBytes()
    {
        using var temp = new TempDb();
        const int dim = 4;
        const int max = 8;
        const int maxNeighbors = 3;
        const int maxLayers = 2;
        const string metadata = """{"durable":true,"slot":0}""";

        using (var db = temp.Open(dim: dim, max: max, maxNeighbors: maxNeighbors, maxLayers: maxLayers))
        {
            db.AddEntry(Vec.Basis(dim, 0), metadata);
        }

        var info = new FileInfo(temp.Path);
        Assert.Equal(ExpectedFileSize(dim, max, maxNeighbors, maxLayers), info.Length);

        byte[] bytes = File.ReadAllBytes(temp.Path);
        var layout = ExpectedLayout(dim, max, maxNeighbors, maxLayers);
        Assert.Equal(metadata, ReadRawMetadata(bytes, layout, index: 0));
    }

    /// <summary>
    /// The 512-byte metadata slot is gone in v4. Metadata now lives in an append-only heap,
    /// so a payload far larger than the old cap must round-trip intact.
    /// </summary>
    [Fact]
    public void Metadata_LargerThanTheOldFixedSlot_RoundTripsThroughTheHeap()
    {
        using var temp = new TempDb();
        string large = "{\"payload\":\"" + new string('x', 4_000) + "\"}";
        var id = Guid.NewGuid();

        using (var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2))
        {
            db.AddEntry(Vec.Basis(4, 0), large, id);
        }

        using var reopened = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2);
        Assert.Equal(large, RequireEntry(reopened, id).Metadata);
    }

    /// <summary>
    /// IsHealthy must detect a damaged header rather than only checking the magic number.
    /// Flipping a byte anywhere in the 4 KiB header region invalidates the CRC-32.
    /// </summary>
    [Fact]
    public void IsHealthy_AfterHeaderCorruption_ReturnsFalse()
    {
        using var temp = new TempDb();

        using (var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2))
        {
            db.AddEntry(Vec.Basis(4, 0), "healthy");
            Assert.True(db.IsHealthy());
        }

        // Corrupt a reserved byte: no field reads it, so only the checksum can catch this.
        using (var stream = new FileStream(temp.Path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 3_000;
            stream.WriteByte(0xFF);
        }

        Assert.Throws<QvecFormatException>(() =>
        {
            using var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2);
        });
    }

    [Fact]
    public void SmallDatabase_PreallocatesExpectedOnDiskSize()
    {
        using var temp = new TempDb();
        const int dim = 3;
        const int max = 5;
        const int maxNeighbors = 2;
        const int maxLayers = 2;

        using (temp.Open(dim: dim, max: max, maxNeighbors: maxNeighbors, maxLayers: maxLayers))
        {
        }

        Assert.Equal(ExpectedFileSize(dim, max, maxNeighbors, maxLayers), new FileInfo(temp.Path).Length);
    }

    [Fact]
    public void EmptyDatabase_RoundTripsAndCanBeUsedAfterReopen()
    {
        using var temp = new TempDb();

        using (temp.Open(dim: 4, max: 5, maxNeighbors: 2, maxLayers: 2))
        {
        }

        using (var reopened = temp.Open(dim: 4, max: 5, maxNeighbors: 2, maxLayers: 2))
        {
            Assert.Equal(0, reopened.GetCount());
            Assert.Equal(0, reopened.LiveCount);
            Assert.Equal(0, reopened.DeletedCount);
            Assert.True(reopened.IsHealthy());
            Assert.Empty(reopened.Search(Vec.Basis(4, 0), topK: 3));

            var id = reopened.AddEntry(Vec.Basis(4, 0), "after-reopen");
            Assert.Equal(1, reopened.GetCount());
            Assert.Equal("after-reopen", RequireEntry(reopened, id).Metadata);
        }
    }

    [Fact]
    public void DeletedEntries_StayDeletedAfterReopen()
    {
        using var temp = new TempDb();
        Guid keepA;
        Guid deleted;
        Guid keepB;

        using (var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2))
        {
            keepA = db.AddEntry(Vec.Basis(4, 0), "keep-a");
            deleted = db.AddEntry(Vec.Basis(4, 1), "delete-me");
            keepB = db.AddEntry(Vec.Basis(4, 2), "keep-b");
            Assert.True(db.Delete(deleted));
            Assert.Equal(1, db.DeletedCount);
            Assert.Equal(2, db.LiveCount);
        }

        using var reopened = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2);
        Assert.Equal(3, reopened.GetCount());
        Assert.Equal(1, reopened.DeletedCount);
        Assert.Equal(2, reopened.LiveCount);
        Assert.Null(reopened.GetByGuid(deleted));
        Assert.Equal("keep-a", RequireEntry(reopened, keepA).Metadata);
        Assert.Equal("keep-b", RequireEntry(reopened, keepB).Metadata);

        var results = reopened.SearchSimple(Vec.Basis(4, 1), topK: 3);
        Assert.DoesNotContain(results, r => r.Id == deleted);
        Assert.Contains(results, r => r.Id == keepA);
        Assert.Contains(results, r => r.Id == keepB);
    }

    private static void AssertMismatchedOpenIsSafe(Func<TempDb, Guid, QvecDatabase> openMismatched)
    {
        using var temp = new TempDb();
        var expectedVector = Vec.Basis(4, 2);
        const string expectedMetadata = """{"self":"describing"}""";
        Guid id;

        using (var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2))
        {
            id = db.AddEntry(expectedVector, expectedMetadata);
        }

        try
        {
            using var reopened = openMismatched(temp, id);
            Assert.Equal(1, reopened.GetCount());
            Assert.True(reopened.IsHealthy());
            var entry = RequireEntry(reopened, id);
            Assert.Equal(expectedVector, entry.Vector);
            Assert.Equal(expectedMetadata, entry.Metadata);
        }
        catch (QvecFormatException)
        {
        }
    }

    private static void CreateDisposedDatabase(TempDb temp)
    {
        using var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2);
        db.AddEntry(Vec.Basis(4, 0), "valid-body");
    }

    private static (float[] Vector, string Metadata) RequireEntry(QvecDatabase db, Guid id)
    {
        var entry = db.GetByGuid(id);
        Assert.True(entry.HasValue, $"Expected persisted entry {id} to be addressable by GUID.");
        return entry.GetValueOrDefault();
    }

    private static void PatchInt32(string path, int offset, int value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        stream.Position = offset;
        writer.Write(value);
    }

    /// <summary>
    /// Simulates a crash mid-write: the WriteInProgress flag is set on disk before data is
    /// written, so a file whose writer never reached its commit must be refused rather than
    /// read back as if it were sound.
    /// </summary>
    [Fact]
    public void Open_AfterAnUncommittedWrite_ThrowsFormatException()
    {
        using var temp = new TempDb();

        using (var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2))
        {
            db.AddEntry(Vec.Basis(4, 0), "committed");
        }

        // Rewrite the header with WriteInProgress set, exactly as BeginWrite leaves it.
        byte[] bytes = File.ReadAllBytes(temp.Path);
        var header = V4Header.Read(bytes.AsSpan(0, HeaderSize), bytes.LongLength);
        header.WriteInProgress = 1;
        header.WriteTo(bytes.AsSpan(0, HeaderSize));
        File.WriteAllBytes(temp.Path, bytes);

        var ex = Assert.Throws<QvecFormatException>(() =>
        {
            using var db = temp.Open(dim: 4, max: 8, maxNeighbors: 3, maxLayers: 2);
        });
        Assert.Contains("write", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The layout a database with these parameters must have on disk. Derived from the same
    /// layout calculator the library uses, so this asserts the file matches its own header
    /// rather than re-encoding the layout a second time and drifting.
    /// </summary>
    private static V4Header ExpectedLayout(int dim, int max, int maxNeighbors, int maxLayers)
        => QvecFormatLayout.CreateInitial(
            dim, max, maxNeighbors, maxLayers,
            QvecFormatLayout.RecommendMetadataHeapCapacity(max));

    private static long ExpectedFileSize(int dim, int max, int maxNeighbors, int maxLayers)
        => ExpectedLayout(dim, max, maxNeighbors, maxLayers).FileLength;

    /// <summary>
    /// Reads row <paramref name="index"/>'s metadata straight out of the raw file bytes by
    /// following its heap descriptor, without going through the library.
    /// </summary>
    private static string ReadRawMetadata(byte[] fileBytes, V4Header layout, int index)
    {
        SectionExtent descriptors = layout.GetRequiredSection(V4SectionIds.MetadataDescriptors);
        SectionExtent heap = layout.GetRequiredSection(V4SectionIds.MetadataHeap);

        int descriptorPos = checked((int)(descriptors.Offset + (long)index * descriptors.ElementSize));
        long heapOffset = BitConverter.ToInt64(fileBytes, descriptorPos);
        int length = BitConverter.ToInt32(fileBytes, descriptorPos + 8);

        return length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(fileBytes, checked((int)(heap.Offset + heapOffset)), length);
    }
}
