using System.Numerics.Tensors;
using BenchmarkDotNet.Attributes;
using Qvec.Core;

namespace Qvec.MicroBenchmarks;

/// <summary>
/// The float distance kernels in <see cref="QvecDatabase"/>, measured against
/// <see cref="TensorPrimitives"/> as the "how fast can this be on this machine" reference.
/// Both operands are ordinary managed arrays, so this isolates the arithmetic from the
/// memory-mapped reads the graph walk does around it.
/// </summary>
[MemoryDiagnoser]
public class FloatKernelBenchmarks
{
    [Params(128, 768, 1536)]
    public int Dim;

    private float[] _left = [];
    private float[] _right = [];

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _left = new float[Dim];
        _right = new float[Dim];
        for (int i = 0; i < Dim; i++)
        {
            _left[i] = (float)(rng.NextDouble() * 2 - 1);
            _right[i] = (float)(rng.NextDouble() * 2 - 1);
        }
    }

    [Benchmark(Baseline = true)]
    public float DotProduct_Array() => QvecDatabase.DotProduct(_left, _right, Dim);

    [Benchmark]
    public float DotProduct_Span() => QvecDatabase.DotProduct((ReadOnlySpan<float>)_left, (ReadOnlySpan<float>)_right);

    [Benchmark]
    public float DotProduct_TensorPrimitives() => TensorPrimitives.Dot<float>(_left, _right);

    /// <summary>The variant the graph walk actually calls: query array against a raw pointer into the mapping.</summary>
    [Benchmark]
    public unsafe float DotProduct_Pointer()
    {
        fixed (float* p = _right) return QvecDatabase.DotProductUnsafe(_left, p, Dim);
    }

    [Benchmark]
    public float NegativeSquaredDistance_Array() => QvecDatabase.NegativeSquaredDistance(_left, _right, Dim);

    [Benchmark]
    public float NegativeSquaredDistance_Span() => QvecDatabase.NegativeSquaredDistance((ReadOnlySpan<float>)_left, (ReadOnlySpan<float>)_right);

    [Benchmark]
    public unsafe float NegativeSquaredDistance_Pointer()
    {
        fixed (float* p = _right) return QvecDatabase.NegativeSquaredDistanceUnsafe(_left, p, Dim);
    }

    /// <summary>
    /// <see cref="TensorPrimitives.Distance{T}"/> takes the square root, which our kernel does
    /// not; the cost of one <c>sqrt</c> per call is negligible next to the loop, so the
    /// comparison is still fair.
    /// </summary>
    [Benchmark]
    public float NegativeSquaredDistance_TensorPrimitives()
    {
        float d = TensorPrimitives.Distance<float>(_left, _right);
        return -(d * d);
    }
}
