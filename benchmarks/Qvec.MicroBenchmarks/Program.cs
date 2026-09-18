using BenchmarkDotNet.Running;

namespace Qvec.MicroBenchmarks;

/// <summary>
/// Entry point. <c>dotnet run -c Release --project benchmarks/Qvec.MicroBenchmarks -- --filter '*'</c>
/// runs everything; <c>--filter '*Kernel*'</c> only the distance kernels; <c>--list flat</c> shows
/// the names. BenchmarkDotNet handles warm-up, statistics and the memory diagnoser; the
/// numbers it prints are per operation.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
