using BenchmarkDotNet.Attributes;
using Qvec.Core;
using Qvec.Core.Quantization;

namespace Qvec.MicroBenchmarks;

/// <summary>
/// The int8 path: quantising a query, the widened-ushort dot product the graph walk uses, and
/// the dequantisation that rescoring and change application pay for.
/// </summary>
[MemoryDiagnoser]
public class Int8KernelBenchmarks
{
    [Params(128, 768, 1536)]
    public int Dim;

    private float[] _floats = [];
    private byte[] _codesA = [];
    private byte[] _codesB = [];
    private byte[] _scratch = [];
    private float[] _restored = [];
    private Int8VectorParameters _pa;
    private Int8VectorParameters _pb;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _floats = new float[Dim];
        var other = new float[Dim];
        for (int i = 0; i < Dim; i++)
        {
            _floats[i] = (float)(rng.NextDouble() * 2 - 1);
            other[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        _codesA = new byte[Dim];
        _codesB = new byte[Dim];
        _scratch = new byte[Dim];
        _restored = new float[Dim];
        _pa = Int8Quantizer.Quantize(_floats, _codesA);
        _pb = Int8Quantizer.Quantize(other, _codesB);
    }

    [Benchmark]
    public Int8VectorParameters Quantize() => Int8Quantizer.Quantize(_floats, _scratch);

    [Benchmark]
    public void Dequantize() => Int8Quantizer.Dequantize(_codesA, _pa, _restored);

    [Benchmark(Baseline = true)]
    public float DotProduct() => Int8Quantizer.DotProduct(_codesA, _pa, _codesB, _pb);

    [Benchmark]
    public float Similarity_Euclidean()
        => Int8Quantizer.Similarity(DistanceFunction.Euclidean, _codesA, _pa, _codesB, _pb);
}
