using System.Text.Json.Serialization.Metadata;
using Qvec.Core;

namespace Qvec.Extensions.VectorData;

/// <summary>Options used when Qvec creates or opens a collection-backed database file.</summary>
public sealed class QvecVectorStoreOptions
{
    /// <summary>Maximum number of records for newly created Qvec collection files.</summary>
    public int MaxCount { get; set; } = 1000;

    /// <summary>Maximum HNSW neighbours for newly created Qvec collection files.</summary>
    public int MaxNeighbors { get; set; } = 32;

    /// <summary>Maximum HNSW layers for newly created Qvec collection files.</summary>
    public int MaxLayers { get; set; } = 5;

    /// <summary>Distance function for newly created Qvec collection files.</summary>
    public DistanceFunction DistanceFunction { get; set; } = DistanceFunction.DotProduct;

    /// <summary>Default HNSW search breadth passed to Qvec when vector search options do not provide one.</summary>
    public int EfSearch { get; set; } = QvecDatabase.DefaultEfSearch;

    /// <summary>Source-generated JSON metadata keyed by record CLR type. Required for AOT-safe collection creation through <c>VectorStore.GetCollection</c>.</summary>
    public IDictionary<Type, JsonTypeInfo> JsonTypeInfos { get; } = new Dictionary<Type, JsonTypeInfo>();
}
