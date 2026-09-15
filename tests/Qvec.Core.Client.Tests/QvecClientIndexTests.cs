using System.Text;
using Qvec.Core.Client;

namespace Qvec.Core.Client.Tests;

public sealed class QvecClientIndexTests
{
    private static readonly ManualCatalogItemExtractor Extractor = new();

    /// <summary>
    /// A duplicate external id is a no-op in the core (first write wins, see
    /// CrudTests.AddEntry_WithDuplicateExternalId_IsIdempotentAndDoesNotOverwriteExistingRow).
    /// The client must therefore not index the rejected object's terms, which previously made
    /// the surviving row queryable under field values it does not have.
    /// </summary>
    [Fact]
    public void DuplicateExternalId_DoesNotAttachDuplicateIndexedTermsToExistingRow()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, Extractor);
        var externalId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var firstId = client.AddEntry(new VectorData<CatalogItem>
        {
            externalId = externalId,
            vector = Vec.Basis(4, 0),
            Item = Item("Cars", "Contoso", "Sedan", 10)
        });
        var duplicateId = client.AddEntry(new VectorData<CatalogItem>
        {
            externalId = externalId,
            vector = Vec.Basis(4, 1),
            Item = Item("Boats", "Northwind", "Dinghy", 20)
        });

        Assert.Equal(firstId, duplicateId);
        Assert.Equal(1, db.GetCount());

        // The rejected duplicate must not make the surviving row look like a Boat.
        Assert.Empty(client.Where(x => x.Category == "Boats"));

        var surviving = Assert.Single(client.Where(x => x.Category == "Cars"));
        Assert.Equal(firstId, surviving.Id);
        Assert.Equal("Cars", surviving.Item!.Category);
    }

    [Fact]
    public void UpdateMetadata_WhenIndexedFieldsAreUnchanged_LeavesIndexedQueryUsable()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, Extractor);
        var original = Item("Boats", "Northwind", "Dinghy", 20, "old metadata");
        var id = Add(client, original, Vec.Basis(4, 0));

        Assert.True(client.UpdateMetadata(id, original with { Description = "new metadata", Price = 25 }));

        var result = Assert.Single(client.Where(x => x.Category == "Boats"));
        Assert.Equal(id, result.Id);
        Assert.Equal("new metadata", result.Item!.Description);
        Assert.Equal(25, result.Item.Price);
    }

    /// <summary>
    /// Known defect: vector updates soft-delete and reinsert the row, but QvecClient never registers
    /// the reinserted row's field-index terms.
    /// </summary>
    [Fact]
    public void UpdateEntry_WhenIndexedFieldsAreUnchanged_ReindexesTheReinsertedRow()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, Extractor);
        var item = Item("Boats", "Northwind", "Dinghy", 20);
        var id = Add(client, item, Vec.Basis(4, 0));

        Assert.True(client.UpdateEntry(id, Vec.Basis(4, 1), item));

        var result = Assert.Single(client.Where(x => x.Category == "Boats"));
        Assert.Equal(id, result.Id);
        Assert.Equal("Dinghy", result.Item!.Title);
    }

    /// <summary>
    /// A metadata update that changes an indexed field must drop the stale term and add the
    /// new one, and indexed queries must be post-filtered against the predicate.
    /// </summary>
    [Fact]
    public void UpdateMetadata_WhenIndexedFieldChanges_RemovesOldTermAndAddsNewTerm()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, Extractor);
        var id = Add(client, Item("Boats", "Northwind", "Dinghy", 20), Vec.Basis(4, 0));

        Assert.True(client.UpdateMetadata(id, Item("Cars", "Northwind", "Sedan", 25)));

        Assert.Empty(client.Where(x => x.Category == "Boats"));
        var result = Assert.Single(client.Where(x => x.Category == "Cars"));
        Assert.Equal(id, result.Id);
        Assert.Equal("Cars", result.Item!.Category);
    }

    [Fact]
    public void Reopen_WithSameDatabaseShape_RoundTripsTypedObjectsAndRebuildsIndex()
    {
        using var temp = new TempDb();
        var boat = Item("Boats", "Northwind", "Dinghy", 20, "small payload");
        var car = Item("Cars", "Contoso", "Sedan", 30, "small payload");

        using (var db = temp.Open(dim: 4, max: 10))
        {
            var client = new QvecClient<CatalogItem>(db, Extractor);
            Add(client, boat, Vec.Basis(4, 0));
            Add(client, car, Vec.Basis(4, 1));
        }

        using (var db = temp.Open(dim: 4, max: 10))
        {
            var client = new QvecClient<CatalogItem>(db, Extractor);

            var boats = client.Where(x => x.Category == "Boats");
            var boatResult = Assert.Single(boats);
            Assert.Equal(boat, boatResult.Item);

            var nearest = Assert.Single(client.Search(Vec.Basis(4, 1), topK: 1));
            Assert.Equal(car, nearest.Item);
        }
    }

    /// <summary>
    /// Known defect: QvecClient accepts JSON metadata larger than the core's fixed 512-byte slot,
    /// after which the core truncates it and the typed object cannot be recovered intact.
    /// </summary>
    [Fact]
    public void AddEntry_WithMetadataOverCoreSlot_RoundTripsFullyOrFailsLoudlyOnWrite()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var client = new QvecClient<CatalogItem>(db, Extractor);
        var item = Item("Boats", "Northwind", "Large", 20, new string('x', 600));
        var jsonBytes = Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(item));

        var writeException = Record.Exception(() => Add(client, item, Vec.Basis(4, 0)));
        if (writeException is not null)
        {
            return;
        }

        List<TypedSearchResult<CatalogItem>>? results = null;
        var readException = Record.Exception(() => results = client.Search(Vec.Basis(4, 0), topK: 1));

        Assert.True(
            readException is null,
            $"Typed client accepted a {jsonBytes}-byte JSON payload for the 512-byte core metadata slot, but failed only when reading it back: {readException?.GetType().Name}: {readException?.Message}");

        var roundTripped = Assert.Single(results!);
        Assert.Equal(item.Description.Length, roundTripped.Item!.Description.Length);
        Assert.Equal(item.Description, roundTripped.Item.Description);
    }

    private static Guid Add(QvecClient<CatalogItem> client, CatalogItem item, float[] vector) =>
        client.AddEntry(new VectorData<CatalogItem> { Item = item, vector = vector });

    private static CatalogItem Item(string category, string brand, string title, int price, string description = "") =>
        new()
        {
            Category = category,
            Brand = brand,
            Title = title,
            Price = price,
            Description = description
        };
}
