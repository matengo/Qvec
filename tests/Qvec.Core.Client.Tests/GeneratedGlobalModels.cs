using Qvec.Core;

public sealed class GlobalIndexedThing
{
    [QvecIndexed]
    public string? Label { get; init; }
}

public sealed class GlobalNoIndexedThing
{
    public string? Label { get; init; }
}
