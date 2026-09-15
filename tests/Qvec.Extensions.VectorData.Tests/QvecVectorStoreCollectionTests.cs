using Microsoft.Extensions.VectorData;

namespace Qvec.Extensions.VectorData.Tests;

public sealed class QvecVectorStoreCollectionTests
{
    [Fact]
    public async Task UpsertThenGetRoundTripsRecord()
    {
        using var temp = new TempDb();
        using var collection = CreateCollection(temp);
        var record = new TestRecord { Id = Guid.NewGuid(), Text = "first", Vector = Vec.Basis(3, 0) };

        await collection.UpsertAsync(record);
        TestRecord? loaded = await collection.GetAsync(record.Id);

        Assert.NotNull(loaded);
        Assert.Equal(record.Id, loaded.Id);
        Assert.Equal("first", loaded.Text);
        Assert.Equal(record.Vector, loaded.Vector);
    }

    [Fact]
    public async Task BatchUpsertStoresAllRecords()
    {
        using var temp = new TempDb();
        using var collection = CreateCollection(temp);
        var records = new[]
        {
            new TestRecord { Id = Guid.NewGuid(), Text = "a", Vector = Vec.Basis(3, 0) },
            new TestRecord { Id = Guid.NewGuid(), Text = "b", Vector = Vec.Basis(3, 1) },
            new TestRecord { Id = Guid.NewGuid(), Text = "c", Vector = Vec.Basis(3, 2) }
        };

        await collection.UpsertAsync(records);
        List<TestRecord> loaded = await collection.GetAsync(records.Select(r => r.Id)).ToListAsync();

        Assert.Equal(["a", "b", "c"], loaded.Select(r => r.Text).OrderBy(t => t).ToArray());
    }

    [Fact]
    public async Task DeleteRemovesRecord()
    {
        using var temp = new TempDb();
        using var collection = CreateCollection(temp);
        var record = new TestRecord { Id = Guid.NewGuid(), Text = "delete", Vector = Vec.Basis(3, 0) };

        await collection.UpsertAsync(record);
        await collection.DeleteAsync(record.Id);

        Assert.Null(await collection.GetAsync(record.Id));
    }

    [Fact]
    public async Task VectorSearchReturnsNearestRecord()
    {
        using var temp = new TempDb();
        using var collection = CreateCollection(temp);
        var far = new TestRecord { Id = Guid.NewGuid(), Text = "far", Vector = Vec.Basis(3, 0) };
        var near = new TestRecord { Id = Guid.NewGuid(), Text = "near", Vector = Vec.Basis(3, 1) };
        await collection.UpsertAsync([far, near]);

        List<VectorSearchResult<TestRecord>> results = await collection.SearchAsync(Vec.Basis(3, 1), top: 1).ToListAsync();

        VectorSearchResult<TestRecord> result = Assert.Single(results);
        Assert.Equal(near.Id, result.Record.Id);
        Assert.True(result.Score >= 0.99, $"Expected near dot-product score, got {result.Score}");
    }

    [Fact]
    public async Task GetMissingKeyReturnsNull()
    {
        using var temp = new TempDb();
        using var collection = CreateCollection(temp);
        await collection.EnsureCollectionExistsAsync();

        Assert.Null(await collection.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task UnsupportedFilteredOperationsThrowNotSupportedException()
    {
        using var temp = new TempDb();
        using var collection = CreateCollection(temp);
        var record = new TestRecord { Id = Guid.NewGuid(), Text = "filter", Vector = Vec.Basis(3, 0) };
        await collection.UpsertAsync(record);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (TestRecord _ in collection.GetAsync(r => r.Text == "filter", top: 1)) { }
        });

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (VectorSearchResult<TestRecord> _ in collection.SearchAsync(Vec.Basis(3, 0), top: 1, new VectorSearchOptions<TestRecord> { Filter = r => r.Text == "filter" })) { }
        });
    }

    [Fact]
    public async Task StringKeysMapToQvecGuids()
    {
        using var temp = new TempDb();
        using var collection = new QvecVectorStoreCollection<string, StringKeyRecord>(
            temp.Path,
            "records",
            new QvecVectorStoreCollectionOptions<string, StringKeyRecord>
            {
                JsonTypeInfo = TestJsonContext.Default.StringKeyRecord,
                Definition = new VectorStoreCollectionDefinition
                {
                    Properties =
                    [
                        new VectorStoreKeyProperty("Id", typeof(string)),
                        new VectorStoreDataProperty("Text", typeof(string)),
                        new VectorStoreVectorProperty("Vector", 3)
                    ]
                },
                MaxNeighbors = 8,
                MaxLayers = 3
            });
        var id = Guid.NewGuid().ToString("D");
        var record = new StringKeyRecord { Id = id, Text = "string-key", Vector = Vec.Basis(3, 0) };

        await collection.UpsertAsync(record);
        StringKeyRecord? loaded = await collection.GetAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Id);
    }

    [Fact]
    public void UnsupportedKeyTypeThrowsNotSupportedException()
    {
        using var temp = new TempDb();

        Assert.Throws<NotSupportedException>(() =>
            new QvecVectorStoreCollection<int, UnsupportedKeyRecord>(
                temp.Path,
                "bad",
                new QvecVectorStoreCollectionOptions<int, UnsupportedKeyRecord>
                {
                    JsonTypeInfo = TestJsonContext.Default.UnsupportedKeyRecord,
                    Definition = new VectorStoreCollectionDefinition
                    {
                        Properties =
                        [
                            new VectorStoreKeyProperty("Id", typeof(int)),
                            new VectorStoreVectorProperty("Vector", 3)
                        ]
                    }
                }));
    }

    private static QvecVectorStoreCollection<Guid, TestRecord> CreateCollection(TempDb temp)
    {
        return new QvecVectorStoreCollection<Guid, TestRecord>(
            temp.Path,
            "records",
            new QvecVectorStoreCollectionOptions<Guid, TestRecord>
            {
                JsonTypeInfo = TestJsonContext.Default.TestRecord,
                Definition = new VectorStoreCollectionDefinition
                {
                    Properties =
                    [
                        new VectorStoreKeyProperty("Id", typeof(Guid)),
                        new VectorStoreDataProperty("Text", typeof(string)),
                        new VectorStoreVectorProperty("Vector", 3)
                    ]
                },
                MaxCount = 100,
                MaxNeighbors = 8,
                MaxLayers = 3
            });
    }
}
