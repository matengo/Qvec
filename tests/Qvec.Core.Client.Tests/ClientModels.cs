using Qvec.Core;

namespace Qvec.Core.Client.Tests;

public sealed record CatalogItem
{
    [QvecIndexed]
    public string Category { get; init; } = string.Empty;

    [QvecIndexed]
    public string Brand { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public int Price { get; init; }

    public string Description { get; init; } = string.Empty;
}

public sealed class ManualCatalogItemExtractor : IQvecFieldExtractor<CatalogItem>
{
    private static readonly string[] s_indexedFields =
    [
        nameof(CatalogItem.Category),
        nameof(CatalogItem.Brand)
    ];

    public ReadOnlySpan<string> IndexedFields => s_indexedFields;

    public IEnumerable<(string Field, string Value)> ExtractFields(CatalogItem item)
    {
        yield return (nameof(CatalogItem.Category), item.Category);
        yield return (nameof(CatalogItem.Brand), item.Brand);
    }
}

public sealed record NoIndexedCatalogItem
{
    public string Title { get; init; } = string.Empty;
}

public sealed class NoIndexedCatalogItemExtractor : IQvecFieldExtractor<NoIndexedCatalogItem>
{
    public ReadOnlySpan<string> IndexedFields => ReadOnlySpan<string>.Empty;

    public IEnumerable<(string Field, string Value)> ExtractFields(NoIndexedCatalogItem item)
    {
        yield break;
    }
}
