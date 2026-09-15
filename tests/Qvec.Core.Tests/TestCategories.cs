namespace Qvec.Core.Tests;

/// <summary>
/// Test category names used with xUnit traits.
///
/// Tests that assert *correct* behaviour which the library does not yet exhibit are
/// marked <c>[Trait("Category", TestCategories.KnownDefect)]</c>. They are expected to
/// fail until the corresponding fix lands, at which point the trait is removed.
///
/// CI's blocking gate runs:   dotnet test --filter "Category!=KnownDefect"
/// A full local run:          dotnet test
/// Only the outstanding bugs: dotnet test --filter "Category=KnownDefect"
/// </summary>
public static class TestCategories
{
    public const string KnownDefect = "KnownDefect";

    /// <summary>Slow, statistical tests (recall measurement, large datasets).</summary>
    public const string Slow = "Slow";
}
