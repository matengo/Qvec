using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.VectorData;
using Qvec.Core;

namespace Qvec.Extensions.VectorData;

/// <summary>
/// A <see cref="VectorStoreCollection{TKey,TRecord}"/> backed by a single <see cref="QvecDatabase"/>.
/// </summary>
/// <remarks>
/// Qvec has one Guid key, one vector, and one 512-byte metadata string per row. This collection therefore supports
/// <typeparamref name="TKey"/> values that are Guid or Guid-formatted strings, exactly one vector property of type
/// <c>float[]</c> or <see cref="ReadOnlyMemory{T}"/> of <see cref="float"/>, and serializes the whole record to JSON
/// metadata using caller-supplied source-generated <see cref="JsonTypeInfo{T}"/>. Filtered retrieval, server-side vector
/// filters, hybrid search, and additional vector properties throw <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class QvecVectorStoreCollection<TKey, TRecord> : VectorStoreCollection<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    private readonly string? _path;
    private readonly QvecVectorStoreCollectionOptions<TKey, TRecord> _options;
    private readonly JsonTypeInfo<TRecord> _jsonTypeInfo;
    private readonly JsonPropertyInfo _keyProperty;
    private readonly JsonPropertyInfo _vectorProperty;
    private readonly bool _ownsDatabase;
    private readonly object _gate = new();
    private QvecDatabase? _database;
    private bool _disposed;

    /// <summary>Creates a file-backed collection. The Qvec database is created by <see cref="EnsureCollectionExistsAsync"/> or first use.</summary>
    public QvecVectorStoreCollection(string path, string name, QvecVectorStoreCollectionOptions<TKey, TRecord> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.JsonTypeInfo);

        _path = path;
        Name = name;
        _options = options;
        _jsonTypeInfo = options.JsonTypeInfo;
        (_keyProperty, _vectorProperty) = ResolveProperties(options);
        _ownsDatabase = true;
    }

    /// <summary>Creates a collection over an already-open Qvec database. The caller remains responsible for disposing the database.</summary>
    public QvecVectorStoreCollection(QvecDatabase database, string name, QvecVectorStoreCollectionOptions<TKey, TRecord> options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.JsonTypeInfo);

        _database = database;
        Name = name;
        _options = options;
        _jsonTypeInfo = options.JsonTypeInfo;
        (_keyProperty, _vectorProperty) = ResolveProperties(options);
    }

    public override string Name { get; }

    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Task.FromResult(_database is not null || (_path is not null && File.Exists(_path)));
    }

    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetOrCreateDatabase();
        return Task.CompletedTask;
    }

    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (_path is null)
            throw new NotSupportedException("A Qvec collection constructed over an existing QvecDatabase cannot delete the caller-owned database file.");

        lock (_gate)
        {
            _database?.Dispose();
            _database = null;
            if (File.Exists(_path)) File.Delete(_path);
        }

        return Task.CompletedTask;
    }

    public override Task<TRecord?> GetAsync(TKey key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = GetExistingDatabaseOrNull();
        if (db is null) return Task.FromResult<TRecord?>(null);

        Guid id = ConvertKeyToGuid(key);
        var stored = db.GetByGuid(id);
        return Task.FromResult(stored is null ? null : DeserializeRecord(stored.Value.Metadata));
    }

    public override async IAsyncEnumerable<TRecord> GetAsync(IEnumerable<TKey> keys, RecordRetrievalOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (TKey key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TRecord? record = await GetAsync(key, options, cancellationToken).ConfigureAwait(false);
            if (record is not null) yield return record;
        }
    }

    public override IAsyncEnumerable<TRecord> GetAsync(
        System.Linq.Expressions.Expression<Func<TRecord, bool>> filter,
        int top,
        FilteredRecordRetrievalOptions<TRecord>? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("QvecVectorStoreCollection does not support filtered record retrieval because Qvec stores only opaque JSON metadata and cannot evaluate VectorData filter semantics server-side.");
    }

    public override Task DeleteAsync(TKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = GetExistingDatabaseOrNull();
        if (db is not null) db.Delete(ConvertKeyToGuid(key));
        return Task.CompletedTask;
    }

    public override Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);

        var db = GetOrCreateDatabase();
        Guid id = GetOrCreateRecordGuid(record);
        float[] vector = ExtractVector(record);
        string metadata = JsonSerializer.Serialize(record, _jsonTypeInfo);

        if (db.GetByGuid(id) is null)
            db.AddEntry(vector, metadata, id);
        else
            db.Update(id, vector, metadata);

        return Task.CompletedTask;
    }

    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (TRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(
        TInput vector,
        int top,
        VectorSearchOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top), top, "top must be greater than zero.");
        if (options?.Filter is not null)
            throw new NotSupportedException("QvecVectorStoreCollection does not support VectorSearchOptions.Filter because Qvec cannot evaluate VectorData filter expressions server-side.");
        if (options?.VectorProperty is not null && !ExpressionTargetsVectorProperty(options.VectorProperty))
            throw new NotSupportedException("Qvec supports exactly one vector property per collection; searching another vector property is not supported.");

        var db = GetExistingDatabaseOrNull();
        if (db is null) yield break;

        int skip = Math.Max(0, options?.Skip ?? 0);
        double? threshold = options?.ScoreThreshold;
        float[] query = ConvertVectorInput(vector);
        var results = db.Search(query, checked(top + skip), _options.EfSearch)
            .Skip(skip);

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (threshold is not null && result.Score < threshold.Value) continue;
            TRecord? record = DeserializeRecord(result.Metadata);
            if (record is not null) yield return new VectorSearchResult<TRecord>(record, result.Score);
            await Task.Yield();
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType == typeof(VectorStoreCollectionMetadata))
        {
            return new VectorStoreCollectionMetadata
            {
                VectorStoreSystemName = "Qvec",
                VectorStoreName = _path,
                CollectionName = Name
            };
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    private QvecDatabase GetOrCreateDatabase()
    {
        ThrowIfDisposed();
        if (_database is not null) return _database;
        if (_path is null) throw new InvalidOperationException("No Qvec database path was supplied.");

        lock (_gate)
        {
            if (_database is not null) return _database;

            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            _database = File.Exists(_path)
                ? QvecDatabase.Open(_path)
                : new QvecDatabase(_path, GetVectorDimensions(), _options.MaxCount, _options.MaxNeighbors, _options.MaxLayers, _options.DistanceFunction);
            return _database;
        }
    }

    private QvecDatabase? GetExistingDatabaseOrNull()
    {
        ThrowIfDisposed();
        if (_database is not null) return _database;
        if (_path is null || !File.Exists(_path)) return null;
        return GetOrCreateDatabase();
    }

    private int GetVectorDimensions()
    {
        if (_options.Definition?.Properties is not null)
        {
            var vectorProperties = _options.Definition.Properties.OfType<VectorStoreVectorProperty>().ToArray();
            if (vectorProperties.Length == 1) return vectorProperties[0].Dimensions;
        }

        Type type = Nullable.GetUnderlyingType(_vectorProperty.PropertyType) ?? _vectorProperty.PropertyType;
        if (type == typeof(float[])) return 0; // not knowable until runtime; handled below
        throw new NotSupportedException("Vector dimensions must be supplied through VectorStoreCollectionDefinition for Qvec to create a database.");
    }

    private static (JsonPropertyInfo Key, JsonPropertyInfo Vector) ResolveProperties(QvecVectorStoreCollectionOptions<TKey, TRecord> options)
    {
        string? keyName = options.KeyPropertyName;
        string? vectorName = options.VectorPropertyName;

        if (options.Definition?.Properties is { Count: > 0 } properties)
        {
            var keys = properties.OfType<VectorStoreKeyProperty>().ToArray();
            if (keys.Length != 1) throw new NotSupportedException("Qvec requires exactly one VectorStoreKeyProperty.");
            keyName ??= keys[0].Name;

            var vectors = properties.OfType<VectorStoreVectorProperty>().ToArray();
            if (vectors.Length != 1) throw new NotSupportedException("Qvec supports exactly one VectorStoreVectorProperty.");
            vectorName ??= vectors[0].Name;

            if (!IsSupportedDistanceFunction(vectors[0].DistanceFunction))
                throw new NotSupportedException($"Qvec supports only {Microsoft.Extensions.VectorData.DistanceFunction.DotProductSimilarity} and {Microsoft.Extensions.VectorData.DistanceFunction.CosineSimilarity} vector distance functions.");
            if (!string.IsNullOrWhiteSpace(vectors[0].IndexKind) && vectors[0].IndexKind != IndexKind.Hnsw)
                throw new NotSupportedException("Qvec uses an HNSW index and cannot honor another VectorStore vector index kind.");
        }

        keyName ??= "Id";
        vectorName ??= "Vector";

        JsonPropertyInfo key = FindJsonProperty(options.JsonTypeInfo, keyName)
            ?? throw new NotSupportedException($"Record type '{typeof(TRecord).FullName}' does not expose key property '{keyName}' in its JsonTypeInfo.");
        JsonPropertyInfo vector = FindJsonProperty(options.JsonTypeInfo, vectorName)
            ?? throw new NotSupportedException($"Record type '{typeof(TRecord).FullName}' does not expose vector property '{vectorName}' in its JsonTypeInfo.");

        if (!IsSupportedKeyType(key.PropertyType))
            throw new NotSupportedException("Qvec row identifiers are Guid values. The record key property must be Guid, Guid?, or string containing a Guid.");
        if (!IsSupportedKeyType(typeof(TKey)))
            throw new NotSupportedException("QvecVectorStoreCollection supports TKey values that are Guid, Guid?, or Guid-formatted string.");
        if (!IsSupportedVectorType(vector.PropertyType))
            throw new NotSupportedException("Qvec supports a single vector property of type float[] or ReadOnlyMemory<float>.");

        return (key, vector);
    }

    private static JsonPropertyInfo? FindJsonProperty(JsonTypeInfo<TRecord> jsonTypeInfo, string name)
    {
        foreach (JsonPropertyInfo property in jsonTypeInfo.Properties)
        {
            if (property.Name == name) return property;
        }

        foreach (JsonPropertyInfo property in jsonTypeInfo.Properties)
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property;
        }

        return null;
    }

    private static bool IsSupportedKeyType(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(Guid) || t == typeof(string);
    }

    private static bool IsSupportedVectorType(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(float[]) || t == typeof(ReadOnlyMemory<float>);
    }

    private static bool IsSupportedDistanceFunction(string? distanceFunction)
    {
        return string.IsNullOrWhiteSpace(distanceFunction)
            || distanceFunction == Microsoft.Extensions.VectorData.DistanceFunction.DotProductSimilarity
            || distanceFunction == Microsoft.Extensions.VectorData.DistanceFunction.CosineSimilarity;
    }

    private Guid GetOrCreateRecordGuid(TRecord record)
    {
        object? value = _keyProperty.Get?.Invoke(record);
        if (TryConvertObjectToGuid(value, out Guid id) && id != Guid.Empty) return id;

        id = Guid.NewGuid();
        if (_keyProperty.Set is null)
            throw new NotSupportedException("The record key property is empty and cannot be set. Supply a Guid key before upsert or make the key property settable.");

        if ((_keyProperty.PropertyType == typeof(Guid)) || (_keyProperty.PropertyType == typeof(Guid?)))
            _keyProperty.Set(record, id);
        else if (_keyProperty.PropertyType == typeof(string))
            _keyProperty.Set(record, id.ToString("D"));
        else
            throw new NotSupportedException("Qvec row identifiers are Guid values. The record key property must be Guid, Guid?, or string containing a Guid.");

        return id;
    }

    private Guid ConvertKeyToGuid(TKey key)
    {
        if (TryConvertObjectToGuid(key, out Guid id) && id != Guid.Empty) return id;
        throw new ArgumentException("Qvec keys must be Guid values or Guid-formatted strings.", nameof(key));
    }

    private static bool TryConvertObjectToGuid(object? value, out Guid id)
    {
        switch (value)
        {
            case Guid guid:
                id = guid;
                return true;
            case string text when Guid.TryParse(text, out Guid guid):
                id = guid;
                return true;
            default:
                id = Guid.Empty;
                return false;
        }
    }

    private float[] ExtractVector(TRecord record)
    {
        object? value = _vectorProperty.Get?.Invoke(record);
        return ConvertVectorObject(value);
    }

    private static float[] ConvertVectorInput<TInput>(TInput vector)
    {
        return ConvertVectorObject(vector);
    }

    private static float[] ConvertVectorObject(object? value)
    {
        return value switch
        {
            float[] array => array,
            ReadOnlyMemory<float> memory => memory.ToArray(),
            Memory<float> memory => memory.ToArray(),
            _ => throw new NotSupportedException("Qvec vector search supports float[] or ReadOnlyMemory<float> vectors only.")
        };
    }

    private TRecord? DeserializeRecord(string metadata) => JsonSerializer.Deserialize(metadata, _jsonTypeInfo);

    private bool ExpressionTargetsVectorProperty(System.Linq.Expressions.Expression<Func<TRecord, object?>> expression)
    {
        System.Linq.Expressions.Expression body = expression.Body;
        if (body is System.Linq.Expressions.UnaryExpression unary) body = unary.Operand;
        return body is System.Linq.Expressions.MemberExpression member
            && string.Equals(member.Member.Name, _vectorProperty.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing && _ownsDatabase)
        {
            lock (_gate)
            {
                _database?.Dispose();
                _database = null;
            }
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
