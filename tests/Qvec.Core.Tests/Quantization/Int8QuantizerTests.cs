using Qvec.Core;
using Qvec.Core.Quantization;

namespace Qvec.Core.Tests.Quantization;

/// <summary>
/// The int8 codec in isolation. Every property here is one the database relies on: bounded
/// reconstruction error, an integer dot product that matches the scalar definition for every
/// tail length the SIMD loop can leave behind, and similarity scores that track the float
/// scores closely enough for HNSW to navigate by them.
/// </summary>
public class Int8QuantizerTests
{
    private static float[] Random(int dim, int seed)
    {
        var rng = new Random(seed);
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = (float)(rng.NextDouble() * 4.0 - 1.5);
        return v;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(128)]
    [InlineData(1536)]
    public void RoundTrip_ErrorIsBoundedByHalfAStep(int dim)
    {
        float[] source = Random(dim, seed: dim);
        var codes = new byte[dim];
        var parameters = Int8Quantizer.Quantize(source, codes);

        var restored = new float[dim];
        Int8Quantizer.Dequantize(codes, parameters, restored);

        float range = source.Max() - source.Min();
        float halfStep = range / 255f / 2f;
        for (int i = 0; i < dim; i++)
        {
            Assert.True(Math.Abs(source[i] - restored[i]) <= halfStep + 1e-5f,
                $"element {i}: {source[i]} became {restored[i]} (half step {halfStep})");
        }
    }

    [Fact]
    public void ConstantVector_QuantizesWithoutDivisionByZero()
    {
        float[] source = Vec.Filled(16, 0.75f);
        var codes = new byte[16];
        var parameters = Int8Quantizer.Quantize(source, codes);

        var restored = new float[16];
        Int8Quantizer.Dequantize(codes, parameters, restored);

        Assert.All(restored, x => Assert.Equal(0.75f, x, precision: 5));
        Assert.False(float.IsNaN(parameters.Scale));
        Assert.False(float.IsInfinity(parameters.Scale));
    }

    [Fact]
    public void Parameters_RoundTripThroughSixteenBytes()
    {
        var parameters = new Int8VectorParameters(scale: 0.0123f, offset: -1.5f, sumOfCodes: 12345, squaredNorm: 42.5f);
        Span<byte> bytes = stackalloc byte[Int8VectorParameters.Size];
        parameters.WriteTo(bytes);
        var read = Int8VectorParameters.ReadFrom(bytes);

        Assert.Equal(16, Int8VectorParameters.Size);
        Assert.Equal(parameters.Scale, read.Scale);
        Assert.Equal(parameters.Offset, read.Offset);
        Assert.Equal(parameters.SumOfCodes, read.SumOfCodes);
        Assert.Equal(parameters.SquaredNorm, read.SquaredNorm);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1536)]
    public void IntegerDotProduct_MatchesScalarDefinitionForEveryTail(int dim)
    {
        var rng = new Random(dim * 31);
        var a = new byte[dim];
        var b = new byte[dim];
        rng.NextBytes(a);
        rng.NextBytes(b);
        // Force the extreme so overflow in intermediate lanes would be caught.
        a[0] = 255; b[0] = 255;

        long expected = 0;
        for (int i = 0; i < dim; i++) expected += a[i] * b[i];

        Assert.Equal(expected, Int8Quantizer.DotProduct(a, b));
    }

    [Theory]
    [InlineData(DistanceFunction.DotProduct)]
    [InlineData(DistanceFunction.Euclidean)]
    [InlineData(DistanceFunction.Cosine)]
    public void Similarity_TracksFloatSimilarityClosely(DistanceFunction distance)
    {
        const int dim = 256;
        const int pairs = 200;
        var rng = new Random(99);

        double worstRelativeError = 0;
        for (int p = 0; p < pairs; p++)
        {
            float[] x = Random(dim, rng.Next());
            float[] y = Random(dim, rng.Next());
            if (distance == DistanceFunction.Cosine)
            {
                QvecDatabase.NormalizeVector(x);
                QvecDatabase.NormalizeVector(y);
            }

            float exact = distance == DistanceFunction.Euclidean
                ? QvecDatabase.NegativeSquaredDistance(x, y, dim)
                : QvecDatabase.DotProduct(x, y);

            var cx = new byte[dim];
            var cy = new byte[dim];
            var px = Int8Quantizer.Quantize(x, cx);
            var py = Int8Quantizer.Quantize(y, cy);

            float approx = Int8Quantizer.Similarity(distance, cx, px, cy, py);

            // Scale the tolerance by the magnitude of the vectors involved rather than the
            // score itself: two nearly orthogonal vectors have a tiny dot product but the
            // same absolute quantisation error as any other pair.
            double magnitude = Math.Sqrt(x.Sum(v => (double)v * v) * y.Sum(v => (double)v * v));
            double relative = Math.Abs(exact - approx) / Math.Max(magnitude, 1e-6);
            worstRelativeError = Math.Max(worstRelativeError, relative);
        }

        Assert.True(worstRelativeError < 0.01,
            $"worst error relative to vector magnitude was {worstRelativeError:P3}");
    }

    [Fact]
    public void Similarity_OfAVectorWithItself_IsZeroDistanceUnderEuclidean()
    {
        float[] x = Random(128, 5);
        var codes = new byte[128];
        var parameters = Int8Quantizer.Quantize(x, codes);

        float self = Int8Quantizer.Similarity(DistanceFunction.Euclidean, codes, parameters, codes, parameters);

        // The stored norm is that of the dequantised vector, so the identity
        // |a|^2 + |a|^2 - 2 a.a holds exactly up to float rounding.
        Assert.Equal(0f, self, precision: 3);
    }
}
