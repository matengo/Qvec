using Qvec.Core;

namespace Qvec.Core.Client.Tests.GeneratedModels;

public enum GeneratedKind
{
    Unknown,
    Primary
}

public sealed class NamedIndexedThing
{
    [QvecIndexed]
    public string? Text { get; init; }

    [QvecIndexed]
    public int? Number { get; init; }

    [QvecIndexed]
    public long? LongNumber { get; init; }

    [QvecIndexed]
    public double? DoubleNumber { get; init; }

    [QvecIndexed]
    public decimal? DecimalNumber { get; init; }

    [QvecIndexed]
    public bool? Flag { get; init; }

    [QvecIndexed]
    public GeneratedKind? Kind { get; init; }

    [QvecIndexed]
    public string? Missing { get; init; }
}

public sealed class SeveralIndexedThing
{
    [QvecIndexed]
    public string? One { get; init; }

    [QvecIndexed]
    public string? Two { get; init; }

    [QvecIndexed]
    public string? Three { get; init; }
}
