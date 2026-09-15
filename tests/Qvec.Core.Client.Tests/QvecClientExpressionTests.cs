using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Qvec.Core;
using Qvec.Core.Client;

namespace Qvec.Core.Client.Tests;

public sealed class QvecClientExpressionTests
{
    private static readonly CatalogItem[] Items =
    [
        new() { Category = "Boats", Brand = "Northwind", Title = "Dinghy", Price = 20 },
        new() { Category = "Boats", Brand = "Contoso", Title = "Kayak", Price = 35 },
        new() { Category = "Cars", Brand = "Contoso", Title = "Sedan", Price = 40 },
        new() { Category = "Cars", Brand = "Northwind", Title = "Coupe", Price = 55 }
    ];

    [Theory]
    [MemberData(nameof(Predicates))]
    public void Where_ReturnsSameSetAsInMemoryLinq(Expression<Func<CatalogItem, bool>> predicate)
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, new ManualCatalogItemExtractor());

        for (int i = 0; i < Items.Length; i++)
        {
            client.AddEntry(new VectorData<CatalogItem> { Item = Items[i], vector = Vec.Basis(4, i) });
        }

        var compiled = predicate.Compile();
        var expected = Items.Where(compiled).Select(x => x.Title).Order(StringComparer.Ordinal).ToArray();
        var actual = client.Where(predicate)
            .Select(x => x.Item!.Title)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Where_CapturedObjectMember_IsNotTreatedAsLambdaParameterField()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, new ManualCatalogItemExtractor());
        AddCatalogItems(client);
        var other = new CatalogItem { Category = "Cars" };

        Expression<Func<CatalogItem, bool>> predicate = x => other.Category == "Cars";

        var actual = client.Where(predicate)
            .Select(x => x.Item!.Title)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Items.Select(x => x.Title).Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void Where_IndexedConstantFormatting_UsesInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("sv-SE");

            using var temp = new TempDb();
            using var db = temp.Open();
            var client = new QvecClient<PriceItem>(db, new PriceItemExtractor());
            client.AddEntry(new VectorData<PriceItem>
            {
                Item = new PriceItem { Price = 1.5m, Name = "Invariant" },
                vector = Vec.Basis(4, 0)
            });

            var result = Assert.Single(client.Where(x => x.Price == 1.5m));
            Assert.Equal("Invariant", result.Item!.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Where_OrElseBailout_DoesNotLeavePartialIndexedLookups()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, new ManualCatalogItemExtractor());
        Expression<Func<CatalogItem, bool>> predicate =
            x => x.Category == "Boats" || x.Brand == "Contoso";

        var lookups = InvokeTryExtractLookups(client, predicate);

        Assert.Empty(lookups);
    }

    [Fact]
    public void Where_IndexedValueEvaluationFailure_FallsBackToScanAndSurfacesPredicateFailure()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, new ManualCatalogItemExtractor());
        AddCatalogItems(client);
        var throwing = new ThrowingValue();
        Expression<Func<CatalogItem, bool>> predicate =
            x => x.Category == "Boats" && x.Brand == throwing.Value;

        Assert.Throws<InvalidOperationException>(() => client.Where(predicate));
        Assert.Empty(InvokeTryExtractLookups(client, predicate));
    }

    [Fact]
    public void Where_FuncOverload_ReturnsSameResultsAsExpressionOverload()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, new ManualCatalogItemExtractor());
        AddCatalogItems(client);
        Expression<Func<CatalogItem, bool>> expression = x => x.Category == "Boats";
        Func<CatalogItem, bool> func = x => x.Category == "Boats";

        var expected = client.Where(expression).Select(x => x.Item!.Title).Order().ToArray();
        var actual = client.Where(func).Select(x => x.Item!.Title).Order().ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Search_FuncOverload_ReturnsSameResultsAsExpressionOverload()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, new ManualCatalogItemExtractor());
        AddCatalogItems(client);
        Expression<Func<CatalogItem, bool>> expression = x => x.Category == "Boats";
        Func<CatalogItem, bool> func = x => x.Category == "Boats";

        var expected = client.Search(Vec.Basis(4, 0), expression, topK: 2)
            .Select(x => x.Item!.Title)
            .Order()
            .ToArray();
        var actual = client.Search(Vec.Basis(4, 0), func, topK: 2)
            .Select(x => x.Item!.Title)
            .Order()
            .ToArray();

        Assert.Equal(expected, actual);
    }

    public static TheoryData<Expression<Func<CatalogItem, bool>>> Predicates()
    {
        var category = "Boats";

        return new TheoryData<Expression<Func<CatalogItem, bool>>>
        {
            x => x.Category == "Boats",
            x => x.Title == "Kayak",
            x => x.Category == "Boats" && x.Brand == "Contoso",
            x => x.Category == "Boats" || x.Brand == "Northwind",
            x => x.Category != "Boats",
            x => x.Title.Contains("a"),
            x => x.Title.StartsWith("S"),
            x => x.Category == category,
            x => x.Category == x.Brand,
            x => x.Category == "Cars" && x.Price > 45
        };
    }

    private static void AddCatalogItems(QvecClient<CatalogItem> client)
    {
        for (int i = 0; i < Items.Length; i++)
        {
            client.AddEntry(new VectorData<CatalogItem> { Item = Items[i], vector = Vec.Basis(4, i) });
        }
    }

    private static IEnumerable InvokeTryExtractLookups(
        QvecClient<CatalogItem> client,
        Expression<Func<CatalogItem, bool>> predicate)
    {
        var method = typeof(QvecClient<CatalogItem>).GetMethod(
            "TryExtractLookups",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        object?[] args = parameters.Length switch
        {
            2 => [predicate.Body, null],
            3 => [predicate.Body, predicate.Parameters[0], null],
            _ => throw new InvalidOperationException("Unexpected TryExtractLookups signature.")
        };

        Assert.False((bool)method.Invoke(client, args)!);
        return Assert.IsAssignableFrom<IEnumerable>(args[^1]);
    }

    private sealed class PriceItemExtractor : IQvecFieldExtractor<PriceItem>
    {
        private static readonly string[] s_indexedFields = [nameof(PriceItem.Price)];

        public ReadOnlySpan<string> IndexedFields => s_indexedFields;

        public IEnumerable<(string Field, string Value)> ExtractFields(PriceItem item)
        {
            yield return (nameof(PriceItem.Price), item.Price.ToString(CultureInfo.InvariantCulture));
        }
    }

    private sealed class ThrowingValue
    {
        public string Value => throw new InvalidOperationException("Value cannot be evaluated during lookup extraction.");
    }
}

public sealed record PriceItem
{
    [QvecIndexed]
    public decimal Price { get; init; }

    public string Name { get; init; } = string.Empty;
}
