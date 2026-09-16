using System.Buffers.Binary;

namespace Qvec.Benchmarks;

/// <summary>
/// A block of equal-length vectors read from an <c>.fvecs</c> or <c>.ivecs</c> file.
/// The values are kept in one flat array rather than an array of arrays: SIFT-1M is a
/// million 128-dimensional vectors, and a million small arrays costs both a great deal of
/// allocation and a great deal of GC pressure for no benefit.
/// </summary>
/// <typeparam name="T">Element type, <see cref="float"/> for fvecs or <see cref="int"/> for ivecs.</typeparam>
public sealed class VecBlock<T>
    where T : unmanaged
{
    private readonly T[] _values;

    public VecBlock(T[] values, int count, int dimension)
    {
        _values = values;
        Count = count;
        Dimension = dimension;
    }

    /// <summary>Number of vectors in the file.</summary>
    public int Count { get; }

    /// <summary>Number of components per vector.</summary>
    public int Dimension { get; }

    /// <summary>The flat backing array, <see cref="Count"/> × <see cref="Dimension"/> long.</summary>
    public T[] Values => _values;

    /// <summary>Returns vector <paramref name="index"/> as a view into the backing array.</summary>
    public ReadOnlySpan<T> this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _values.AsSpan(index * Dimension, Dimension);
        }
    }

    /// <summary>Copies vector <paramref name="index"/> into a fresh array.</summary>
    public T[] ToArray(int index) => this[index].ToArray();
}

/// <summary>
/// Reader for the <c>fvecs</c> / <c>ivecs</c> family used by the TexMex ANN corpora
/// (SIFT, GIST) and by essentially every published ANN benchmark since.
///
/// The format is a bare concatenation of records, with no header:
/// each record is a little-endian <c>Int32</c> dimension followed by that many elements.
/// The dimension is therefore repeated once per vector, which is redundant but is exactly
/// what lets a reader validate the file: every record must declare the same dimension, and
/// the file length must come out exact. Both are checked here, because a truncated or
/// byte-swapped download otherwise produces a plausible-looking block of garbage and a
/// benchmark that silently measures nothing.
/// </summary>
public static class VecFile
{
    /// <summary>Reads an <c>.fvecs</c> file.</summary>
    public static VecBlock<float> ReadFvecs(string path, int maxCount = int.MaxValue) =>
        Read<float>(path, maxCount, static (source, destination) =>
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(i * sizeof(float)));
            }
        });

    /// <summary>Reads an <c>.ivecs</c> file, the format used for ground-truth neighbour ids.</summary>
    public static VecBlock<int> ReadIvecs(string path, int maxCount = int.MaxValue) =>
        Read<int>(path, maxCount, static (source, destination) =>
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * sizeof(int)));
            }
        });

    private delegate void Decode<T>(ReadOnlySpan<byte> source, Span<T> destination);

    private static VecBlock<T> Read<T>(string path, int maxCount, Decode<T> decode)
        where T : unmanaged
    {
        if (maxCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxCount));

        int elementSize = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1 << 20,
            FileOptions.SequentialScan);

        long length = stream.Length;
        if (length == 0) throw new InvalidDataException($"'{path}' is empty.");
        if (length < sizeof(int)) throw new InvalidDataException($"'{path}' is too short to contain a dimension.");

        Span<byte> header = stackalloc byte[sizeof(int)];
        stream.ReadExactly(header);
        int dimension = BinaryPrimitives.ReadInt32LittleEndian(header);

        // A byte-swapped or misidentified file shows up here first, as an absurd dimension.
        if (dimension <= 0 || dimension > 1_000_000)
        {
            throw new InvalidDataException(
                $"'{path}' declares a first-vector dimension of {dimension}, which is not plausible. " +
                "The file is probably truncated, big-endian, or not a vecs file at all.");
        }

        long recordSize = sizeof(int) + ((long)dimension * elementSize);
        if (length % recordSize != 0)
        {
            throw new InvalidDataException(
                $"'{path}' is {length} bytes, which is not a whole number of {recordSize}-byte records " +
                $"for dimension {dimension}. The file is truncated or corrupt.");
        }

        long available = length / recordSize;
        int count = (int)Math.Min(available, maxCount);

        var values = new T[(long)count * dimension];
        byte[] record = new byte[dimension * elementSize];

        stream.Position = 0;
        for (int i = 0; i < count; i++)
        {
            stream.ReadExactly(header);
            int declared = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (declared != dimension)
            {
                throw new InvalidDataException(
                    $"'{path}' vector {i} declares dimension {declared} but vector 0 declared {dimension}. " +
                    "Ragged vecs files are not supported.");
            }

            stream.ReadExactly(record);
            decode(record, values.AsSpan(i * dimension, dimension));
        }

        return new VecBlock<T>(values, count, dimension);
    }
}
