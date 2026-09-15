using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.VectorData;
using Qvec.Core;

namespace Qvec.Extensions.VectorData;

/// <summary>Options for <see cref="QvecVectorStoreCollection{TKey,TRecord}"/>.</summary>
public sealed class QvecVectorStoreCollectionOptions<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    /// <summary>
    /// Source-generated JSON metadata for <typeparamref name="TRecord"/>. Required: the adapter never falls back to reflection serialization.
    /// </summary>
    public required JsonTypeInfo<TRecord> JsonTypeInfo { get; init; }

    /// <summary>Vector Data schema definition. When supplied, it identifies the key and vector properties.</summary>
    public VectorStoreCollectionDefinition? Definition { get; init; }

    /// <summary>Record property containing the Qvec Guid key. Used when <see cref="Definition"/> is not supplied.</summary>
    public string? KeyPropertyName { get; init; }

    /// <summary>Record property containing the single stored vector. Used when <see cref="Definition"/> is not supplied.</summary>
    public string? VectorPropertyName { get; init; }

    /// <summary>Maximum number of records for newly created databases.</summary>
    public int MaxCount { get; init; } = 1000;

    /// <summary>Maximum HNSW neighbours for newly created databases.</summary>
    public int MaxNeighbors { get; init; } = 32;

    /// <summary>Maximum HNSW layers for newly created databases.</summary>
    public int MaxLayers { get; init; } = 5;

    /// <summary>Distance function for newly created databases.</summary>
    public Qvec.Core.DistanceFunction DistanceFunction { get; init; } = Qvec.Core.DistanceFunction.DotProduct;

    /// <summary>HNSW search breadth passed to <see cref="QvecDatabase.Search(float[], int, int)"/>.</summary>
    public int EfSearch { get; init; } = QvecDatabase.DefaultEfSearch;
}
