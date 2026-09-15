namespace Qvec.Core.Tests;

/// <summary>
/// Groups the timing-sensitive and CPU-heavy suites into a single xUnit collection so
/// they never run concurrently with each other. The concurrency soak tests assert on
/// wall-clock windows of a few hundred milliseconds; running them alongside the recall
/// measurements saturates every core and makes them fail for reasons that have nothing
/// to do with the library.
/// </summary>
[CollectionDefinition(TestCollections.TimingSensitive, DisableParallelization = true)]
public sealed class TimingSensitiveCollection;

public static class TestCollections
{
    public const string TimingSensitive = "TimingSensitive";
}
