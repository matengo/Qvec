using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Qvec.Core
{
    /// <summary>
    /// How a database stores its vectors on disk. Chosen at creation time and recorded in the
    /// header; every later open adopts whatever the file says.
    /// </summary>
    /// <remarks>
    /// The numeric values are the header's <c>QuantizationMode</c> field, so they must not be
    /// renumbered. Mode 1 (one scale for the whole dataset) is reserved in the format but not
    /// implemented, which is why the enum skips it.
    /// </remarks>
    public enum VectorQuantization : uint
    {
        /// <summary>Vectors are stored as 32-bit floats. Exact.</summary>
        None = 0,

        /// <summary>
        /// Each vector is stored as one unsigned byte per dimension plus 16 bytes of per-vector
        /// parameters, roughly a quarter of the float footprint. Scores become approximate;
        /// <see cref="QvecDatabase.GetByGuid"/> returns the dequantised vector, not the original.
        /// </summary>
        Int8 = 2,
    }
}

namespace Qvec.Core.Quantization
{
    /// <summary>
    /// Everything needed to turn a vector's byte codes back into floats and to score two coded
    /// vectors against each other without decoding them. Stored in section 10 as 16 bytes per row.
    /// </summary>
    /// <param name="scale">Width of one quantisation step in the original units.</param>
    /// <param name="offset">Value represented by code 0 (the vector's minimum).</param>
    /// <param name="sumOfCodes">Sum of all codes, which lets the dot product be expanded algebraically.</param>
    /// <param name="squaredNorm">Squared L2 norm of the dequantised vector, for Euclidean scoring.</param>
    public readonly struct Int8VectorParameters(float scale, float offset, int sumOfCodes, float squaredNorm)
    {
        public const int Size = 16;

        public float Scale { get; } = scale;
        public float Offset { get; } = offset;
        public int SumOfCodes { get; } = sumOfCodes;
        public float SquaredNorm { get; } = squaredNorm;

        public void WriteTo(Span<byte> destination)
        {
            BinaryPrimitives.WriteSingleLittleEndian(destination, Scale);
            BinaryPrimitives.WriteSingleLittleEndian(destination[4..], Offset);
            BinaryPrimitives.WriteInt32LittleEndian(destination[8..], SumOfCodes);
            BinaryPrimitives.WriteSingleLittleEndian(destination[12..], SquaredNorm);
        }

        public static Int8VectorParameters ReadFrom(ReadOnlySpan<byte> source) => new(
            BinaryPrimitives.ReadSingleLittleEndian(source),
            BinaryPrimitives.ReadSingleLittleEndian(source[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(source[8..]),
            BinaryPrimitives.ReadSingleLittleEndian(source[12..]));

        internal static unsafe Int8VectorParameters ReadFrom(byte* source)
            => ReadFrom(new ReadOnlySpan<byte>(source, Size));
    }

    /// <summary>
    /// Asymmetric per-vector scalar quantisation to unsigned bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each vector gets its own <c>[min, max]</c> range mapped linearly onto <c>0..255</c>. With
    /// <c>x ≈ offset + scale·q</c> the dot product of two vectors expands to
    /// </para>
    /// <code>
    /// a·b ≈ Sa·Sb·Σ(qa·qb) + Sa·Ob·Σqa + Sb·Oa·Σqb + d·Oa·Ob
    /// </code>
    /// <para>
    /// so only <c>Σ(qa·qb)</c> has to be computed per pair, and that is a pure integer dot
    /// product over bytes. The remaining terms come from the stored parameters. Euclidean
    /// distance is derived from the dot product and the two stored norms.
    /// </para>
    /// </remarks>
    public static class Int8Quantizer
    {
        private const float Levels = 255f;

        /// <summary>Quantises <paramref name="source"/> into <paramref name="codes"/> and returns the parameters needed to decode it.</summary>
        public static Int8VectorParameters Quantize(ReadOnlySpan<float> source, Span<byte> codes)
        {
            if (codes.Length < source.Length) throw new ArgumentException("Code buffer is too small.", nameof(codes));
            if (source.IsEmpty) return new Int8VectorParameters(0f, 0f, 0, 0f);

            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            foreach (float x in source)
            {
                if (x < min) min = x;
                if (x > max) max = x;
            }

            float range = max - min;
            float scale = range > 0f ? range / Levels : 0f;
            float inverse = scale > 0f ? 1f / scale : 0f;

            long sum = 0;
            double normSq = 0;
            for (int i = 0; i < source.Length; i++)
            {
                float q = MathF.Round((source[i] - min) * inverse);
                byte code = (byte)Math.Clamp(q, 0f, Levels);
                codes[i] = code;
                sum += code;
                double restored = min + scale * code;
                normSq += restored * restored;
            }

            return new Int8VectorParameters(scale, min, (int)sum, (float)normSq);
        }

        /// <summary>Restores an approximation of the original vector.</summary>
        public static void Dequantize(ReadOnlySpan<byte> codes, in Int8VectorParameters parameters, Span<float> destination)
        {
            if (destination.Length < codes.Length) throw new ArgumentException("Destination is too small.", nameof(destination));
            for (int i = 0; i < codes.Length; i++)
                destination[i] = parameters.Offset + parameters.Scale * codes[i];
        }

        /// <summary>Σ a[i]·b[i] over unsigned bytes. Exact.</summary>
        public static long DotProduct(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length) throw new ArgumentException("Vectors must have the same length.");
            unsafe
            {
                fixed (byte* pa = a, pb = b)
                    return DotProduct(pa, pb, a.Length);
            }
        }

        /// <summary>
        /// Integer dot product kernel. Bytes are widened to ushort so the products (≤ 65 025) fit,
        /// then to uint for accumulation. A uint accumulator lane overflows only after ~66 000
        /// products, far beyond any embedding dimension.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe long DotProduct(byte* a, byte* b, int count)
        {
            Vector<uint> acc = Vector<uint>.Zero;
            int i = 0;

            if (Vector.IsHardwareAccelerated && count >= Vector<byte>.Count)
            {
                int lanes = Vector<byte>.Count;
                int last = count - lanes;
                for (; i <= last; i += lanes)
                {
                    var va = Vector.LoadUnsafe(ref *(a + i));
                    var vb = Vector.LoadUnsafe(ref *(b + i));
                    Vector.Widen(va, out Vector<ushort> aLo, out Vector<ushort> aHi);
                    Vector.Widen(vb, out Vector<ushort> bLo, out Vector<ushort> bHi);
                    Vector.Widen(aLo * bLo, out Vector<uint> p0, out Vector<uint> p1);
                    Vector.Widen(aHi * bHi, out Vector<uint> p2, out Vector<uint> p3);
                    acc += p0 + p1 + p2 + p3;
                }
            }

            long sum = 0;
            for (int lane = 0; lane < Vector<uint>.Count; lane++) sum += acc[lane];
            for (; i < count; i++) sum += a[i] * b[i];
            return sum;
        }

        /// <summary>Approximate a·b from codes and parameters.</summary>
        public static float DotProduct(ReadOnlySpan<byte> a, in Int8VectorParameters pa, ReadOnlySpan<byte> b, in Int8VectorParameters pb)
        {
            if (a.Length != b.Length) throw new ArgumentException("Vectors must have the same length.");
            unsafe
            {
                fixed (byte* ptrA = a, ptrB = b)
                    return DotProduct(ptrA, pa, ptrB, pb, a.Length);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe float DotProduct(byte* a, in Int8VectorParameters pa, byte* b, in Int8VectorParameters pb, int dim)
        {
            long q = DotProduct(a, b, dim);
            double result = (double)pa.Scale * pb.Scale * q
                          + (double)pa.Scale * pb.Offset * pa.SumOfCodes
                          + (double)pb.Scale * pa.Offset * pb.SumOfCodes
                          + (double)dim * pa.Offset * pb.Offset;
            return (float)result;
        }

        /// <summary>
        /// Similarity under <paramref name="distance"/> using the same sign convention as the float
        /// path: higher is closer. Cosine assumes both vectors were normalised before quantisation,
        /// exactly as <see cref="QvecDatabase"/> does for float storage.
        /// </summary>
        public static float Similarity(DistanceFunction distance, ReadOnlySpan<byte> a, in Int8VectorParameters pa, ReadOnlySpan<byte> b, in Int8VectorParameters pb)
        {
            if (a.Length != b.Length) throw new ArgumentException("Vectors must have the same length.");
            unsafe
            {
                fixed (byte* ptrA = a, ptrB = b)
                    return Similarity(distance, ptrA, pa, ptrB, pb, a.Length);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe float Similarity(DistanceFunction distance, byte* a, in Int8VectorParameters pa, byte* b, in Int8VectorParameters pb, int dim)
        {
            float dot = DotProduct(a, pa, b, pb, dim);
            return distance == DistanceFunction.Euclidean
                ? -(pa.SquaredNorm + pb.SquaredNorm - 2f * dot)
                : dot;
        }
    }
}
