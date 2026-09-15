using System.Buffers.Binary;
using Qvec.Benchmarks;

namespace Qvec.Benchmarks.Tests;

/// <summary>
/// Proof for the <c>fvecs</c> / <c>ivecs</c> reader.
///
/// These tests build the bytes by hand rather than downloading SIFT, for two reasons. A CI gate
/// must not depend on an FTP server in Rennes, and more importantly a real file cannot express
/// the cases that matter here: truncation, a ragged dimension, and a byte-swapped header. Those
/// are exactly the failures that would otherwise produce a plausible-looking block of numbers
/// and a benchmark that measures nothing.
/// </summary>
public class VecFileTests : IDisposable
{
    private readonly string _directory;

    public VecFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qvec-vecfile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteFvecs(params float[][] vectors)
    {
        string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.fvecs");
        using var stream = File.Create(path);
        Span<byte> word = stackalloc byte[4];

        foreach (var vector in vectors)
        {
            BinaryPrimitives.WriteInt32LittleEndian(word, vector.Length);
            stream.Write(word);
            foreach (float value in vector)
            {
                BinaryPrimitives.WriteSingleLittleEndian(word, value);
                stream.Write(word);
            }
        }

        return path;
    }

    private string WriteIvecs(params int[][] vectors)
    {
        string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.ivecs");
        using var stream = File.Create(path);
        Span<byte> word = stackalloc byte[4];

        foreach (var vector in vectors)
        {
            BinaryPrimitives.WriteInt32LittleEndian(word, vector.Length);
            stream.Write(word);
            foreach (int value in vector)
            {
                BinaryPrimitives.WriteInt32LittleEndian(word, value);
                stream.Write(word);
            }
        }

        return path;
    }

    [Fact]
    public void ReadFvecs_ReturnsEveryVectorInOrder()
    {
        string path = WriteFvecs(
            [1f, 2f, 3f],
            [-4f, 5.5f, 6f],
            [0f, 0f, 0f]);

        var block = VecFile.ReadFvecs(path);

        Assert.Equal(3, block.Count);
        Assert.Equal(3, block.Dimension);
        Assert.Equal(new[] { 1f, 2f, 3f }, block[0].ToArray());
        Assert.Equal(new[] { -4f, 5.5f, 6f }, block[1].ToArray());
        Assert.Equal(new[] { 0f, 0f, 0f }, block[2].ToArray());
    }

    [Fact]
    public void ReadIvecs_ReturnsEveryVectorInOrder()
    {
        string path = WriteIvecs(
            [10, 20],
            [30, 40]);

        var block = VecFile.ReadIvecs(path);

        Assert.Equal(2, block.Count);
        Assert.Equal(2, block.Dimension);
        Assert.Equal(new[] { 10, 20 }, block[0].ToArray());
        Assert.Equal(new[] { 30, 40 }, block[1].ToArray());
    }

    [Fact]
    public void ReadFvecs_WithMaxCount_StopsEarlyWithoutReadingTheRest()
    {
        string path = WriteFvecs([1f, 1f], [2f, 2f], [3f, 3f], [4f, 4f]);

        var block = VecFile.ReadFvecs(path, maxCount: 2);

        Assert.Equal(2, block.Count);
        Assert.Equal(new[] { 1f, 1f }, block[0].ToArray());
        Assert.Equal(new[] { 2f, 2f }, block[1].ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => block.ToArray(2));
    }

    [Fact]
    public void ReadFvecs_WhenTruncatedMidVector_Throws()
    {
        string path = WriteFvecs([1f, 2f, 3f], [4f, 5f, 6f]);

        // Lop off the last four bytes: the file now claims a vector it does not contain.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(stream.Length - 4);
        }

        var exception = Assert.Throws<InvalidDataException>(() => VecFile.ReadFvecs(path));
        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadFvecs_WhenVectorsHaveDifferentDimensions_Throws()
    {
        // Same total length as four 3-dimensional vectors would not be, so the record-size check
        // cannot catch this one; only the per-record dimension check can.
        string path = WriteFvecs([1f, 2f], [3f, 4f], [5f, 6f, 7f, 8f]);

        var exception = Assert.Throws<InvalidDataException>(() => VecFile.ReadFvecs(path));
        Assert.Contains("dimension", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadFvecs_WhenHeaderIsByteSwapped_Throws()
    {
        string path = Path.Combine(_directory, "swapped.fvecs");
        var bytes = new byte[4 + (128 * 4)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 128);
        File.WriteAllBytes(path, bytes);

        var exception = Assert.Throws<InvalidDataException>(() => VecFile.ReadFvecs(path));
        Assert.Contains("plausible", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadFvecs_WhenFileIsEmpty_Throws()
    {
        string path = Path.Combine(_directory, "empty.fvecs");
        File.WriteAllBytes(path, []);

        Assert.Throws<InvalidDataException>(() => VecFile.ReadFvecs(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void IdForBaseIndex_RoundTrips(int index)
    {
        Assert.Equal(index, RecallBenchmark.BaseIndexForId(RecallBenchmark.IdForBaseIndex(index)));
    }

    [Fact]
    public void IdForBaseIndex_IsDistinctPerIndex()
    {
        var ids = Enumerable.Range(0, 1000).Select(RecallBenchmark.IdForBaseIndex).ToHashSet();

        Assert.Equal(1000, ids.Count);
    }
}
