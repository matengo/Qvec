using System.Text.Json.Serialization;
using Qvec.Core;
using Qvec.Core.Client;

ReadmeSamples.InitializeAndAddData();
ReadmeSamples.HnswVectorSearch();
ReadmeSamples.MetadataFilteredSearch();
ReadmeSamples.TypedClient();
ReadmeSamples.TypedClientSerialization();
ReadmeSamples.IndexedFilteringExtractor();
ReadmeSamples.IndexedWhere();
ReadmeSamples.HybridSearchWithIndexedFiltering();
ReadmeSamples.CombiningJsonTypeInfoAndIndexedFiltering();

internal static class ReadmeSamples
{
    public static void InitializeAndAddData()
    {
        using var db = NewDatabase("vectors.qvec", dim: 1536, max: 10_000);

        float[] embedding = GetEmbedding("Hello World", 1536);
        Guid id = db.AddEntry(embedding, "{\"id\":1,\"category\":\"text\"}");
    }

    public static void HnswVectorSearch()
    {
        using var db = NewDatabase("search.qvec", dim: 1536, max: 10_000);
        float[] queryVector = GetEmbedding("query", 1536);
        db.AddEntry(GetEmbedding("Hello World", 1536), "{\"id\":1,\"category\":\"text\"}");

        var results = db.Search(queryVector, topK: 5);

        foreach (var r in results)
        {
            Console.WriteLine($"Found match: {r.Id} with score {r.Score}");
        }
    }

    public static void MetadataFilteredSearch()
    {
        using var db = NewDatabase("filtered.qvec", dim: 1536, max: 10_000);
        float[] queryVector = GetEmbedding("query", 1536);
        db.AddEntry(GetEmbedding("Hello World", 1536), "{\"id\":1,\"category\":\"text\"}");

        var results = db.Search(
            queryVector,
            meta => meta.Contains("\"category\":\"text\""),
            topK: 5);

        foreach (var r in results)
        {
            Console.WriteLine($"Found match: {r.Id} with score {r.Score}");
        }
    }

    public static void TypedClient()
    {
        using var db = NewDatabase("products.qvec", dim: 1536, max: 10_000);
        var client = new QvecClient<Product>(db);
        float[] myVector = GetEmbedding("Laptop", 1536);
        float[] queryVector = GetEmbedding("query", 1536);

        Guid id = client.AddEntry(new VectorData<Product>
        {
            vector = myVector,
            Item = new Product(1, "Laptop", 12_000, true)
        });

        var results = client.Search(queryVector, p => p.Price < 15_000 && p.InStock);

        foreach (var r in results)
        {
            Console.WriteLine($"{r.Item?.Name}: {r.Score}");
        }
    }

    public static void TypedClientSerialization()
    {
        using var db = NewDatabase("products-json.qvec", dim: 1536, max: 10_000);
        var client = new QvecClient<Product>(db, ProductJsonContext.Default.Product);
        float[] myVector = GetEmbedding("Laptop", 1536);
        float[] queryVector = GetEmbedding("query", 1536);

        Guid id = client.AddEntry(new VectorData<Product>
        {
            vector = myVector,
            Item = new Product(1, "Laptop", 12_000, true)
        });

        var results = client.Search(queryVector, p => p.Price < 15_000 && p.InStock);
    }

    public static void IndexedFilteringExtractor()
    {
        using var db = NewDatabase("products-index.qvec", dim: 1536, max: 10_000);
        var client = new QvecClient<Product>(
            db,
            new Qvec.Generated.ProductFieldExtractor());
    }

    public static void IndexedWhere()
    {
        using var db = NewDatabase("products-where.qvec", dim: 1536, max: 10_000);
        var client = new QvecClient<Product>(db, new Qvec.Generated.ProductFieldExtractor());
        float[] myVector = GetEmbedding("Science by Acme", 1536);
        client.AddEntry(new VectorData<Product>
        {
            vector = myVector,
            Item = new Product(2, "Book", 42, true)
            {
                Category = "Science",
                Brand = "Acme",
                Description = "quantum field guide"
            }
        });

        var science = client.Where(p => p.Category == "Science");

        var acmeScience = client.Where(
            p => p.Category == "Science" && p.Brand == "Acme");

        string cat = "Science";
        var results = client.Where(p => p.Category == cat);

        var cheap = client.Where(p => p.Description.Contains("quantum"));
    }

    public static void HybridSearchWithIndexedFiltering()
    {
        using var db = NewDatabase("products-hybrid.qvec", dim: 1536, max: 10_000);
        var client = new QvecClient<Product>(db, new Qvec.Generated.ProductFieldExtractor());
        float[] queryVector = GetEmbedding("query", 1536);

        var results = client.Search(queryVector, p => p.Category == "Science", topK: 5);

        var acmeResults = client.Search(
            queryVector,
            p => p.Category == "Science" && p.Brand == "Acme",
            topK: 5);

        var postFiltered = client.Search(queryVector, p => p.Price < 100, topK: 5);
    }

    public static void CombiningJsonTypeInfoAndIndexedFiltering()
    {
        using var db = NewDatabase("products-combined.qvec", dim: 1536, max: 10_000);
        var client = new QvecClient<Product>(
            db,
            ProductJsonContext.Default.Product,
            new Qvec.Generated.ProductFieldExtractor());
    }

    private static QvecDatabase NewDatabase(string fileName, int dim, int max)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "sample-data", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return new QvecDatabase(path, dim, max);
    }

    private static float[] GetEmbedding(string text, int dim)
    {
        var vector = new float[dim];
        int seed = text.GetHashCode(StringComparison.Ordinal);
        for (int i = 0; i < vector.Length; i++)
        {
            seed = unchecked(seed * 1_664_525 + 1_013_904_223);
            vector[i] = (seed & 0xFFFF) / 65_535f;
        }

        return vector;
    }
}

public class Product
{
    public Product(int id, string name, double price, bool inStock)
    {
        Id = id;
        Name = name;
        Price = price;
        InStock = inStock;
    }

    public int Id { get; set; }
    public string Name { get; set; }
    public double Price { get; set; }
    public bool InStock { get; set; }

    [QvecIndexed]
    public string Category { get; set; } = "";

    [QvecIndexed]
    public string Brand { get; set; } = "";

    public string Description { get; set; } = "";
}

[JsonSerializable(typeof(Product))]
internal partial class ProductJsonContext : JsonSerializerContext { }

namespace Qvec.Generated
{
    public sealed class ProductFieldExtractor : IQvecFieldExtractor<Product>
    {
        public IEnumerable<(string Field, string Value)> ExtractFields(Product item)
        {
            yield return (nameof(Product.Category), item.Category);
            yield return (nameof(Product.Brand), item.Brand);
        }
    }
}
