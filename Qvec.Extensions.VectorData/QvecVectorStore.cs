using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.VectorData;
using Qvec.Core;

namespace Qvec.Extensions.VectorData;

/// <summary>
/// A Microsoft.Extensions.VectorData store backed by one Qvec file per collection.
/// </summary>
/// <remarks>
/// Qvec stores Guid row identifiers, one vector, and one 512-byte UTF-8 metadata string per row. This adapter maps each
/// collection to a <c>.qvec</c> file, maps record keys to Guid values, and stores the full record JSON as metadata using
/// caller-supplied source-generated <see cref="JsonTypeInfo"/>. Server-side filtering, hybrid search, and multiple vector
/// properties are not supported.
/// </remarks>
public sealed class QvecVectorStore : VectorStore
{
    private readonly string _directoryPath;
    private readonly QvecVectorStoreOptions _options;
    private readonly ConcurrentDictionary<string, object> _collections = new(StringComparer.Ordinal);

    public QvecVectorStore(string directoryPath, QvecVectorStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _directoryPath = directoryPath;
        _options = options ?? new QvecVectorStoreOptions();
    }

    [RequiresDynamicCode("This API is not compatible with NativeAOT. Construct QvecVectorStoreCollection directly with source-generated JsonTypeInfo for AOT-safe use.")]
    [RequiresUnreferencedCode("This API is not compatible with trimming. Construct QvecVectorStoreCollection directly with source-generated JsonTypeInfo for AOT-safe use.")]
    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_options.JsonTypeInfos.TryGetValue(typeof(TRecord), out JsonTypeInfo? jsonTypeInfo) || jsonTypeInfo is not JsonTypeInfo<TRecord> typedJsonTypeInfo)
        {
            throw new NotSupportedException($"Qvec requires source-generated JsonTypeInfo for record type '{typeof(TRecord).FullName}'. Add it to QvecVectorStoreOptions.JsonTypeInfos or construct QvecVectorStoreCollection directly.");
        }

        return (VectorStoreCollection<TKey, TRecord>)_collections.GetOrAdd(CollectionKey<TKey, TRecord>(name), _ =>
            new QvecVectorStoreCollection<TKey, TRecord>(
                CollectionPath(name),
                name,
                new QvecVectorStoreCollectionOptions<TKey, TRecord>
                {
                    JsonTypeInfo = typedJsonTypeInfo,
                    Definition = definition,
                    MaxCount = _options.MaxCount,
                    MaxNeighbors = _options.MaxNeighbors,
                    MaxLayers = _options.MaxLayers,
                    DistanceFunction = _options.DistanceFunction,
                    EfSearch = _options.EfSearch
                }));
    }

    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition definition)
    {
        throw new NotSupportedException("Qvec does not support dynamic dictionary records because it requires source-generated JsonTypeInfo for AOT-safe metadata serialization.");
    }

    public override async IAsyncEnumerable<string> ListCollectionNamesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directoryPath)) yield break;

        foreach (string file in Directory.EnumerateFiles(_directoryPath, "*.qvec", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Path.GetFileNameWithoutExtension(file);
            await Task.Yield();
        }
    }

    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(CollectionPath(name)));
    }

    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (IDisposable disposable in _collections.RemovePrefix(name).OfType<IDisposable>())
        {
            disposable.Dispose();
        }

        string path = CollectionPath(name);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType == typeof(VectorStoreMetadata))
        {
            return new VectorStoreMetadata { VectorStoreSystemName = "Qvec", VectorStoreName = _directoryPath };
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    private string CollectionPath(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Collection names must be valid file names.", nameof(name));

        return Path.Combine(_directoryPath, name + ".qvec");
    }

    private static string CollectionKey<TKey, TRecord>(string name) where TKey : notnull where TRecord : class => $"{name}|{typeof(TKey).AssemblyQualifiedName}|{typeof(TRecord).AssemblyQualifiedName}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (object collection in _collections.Values)
            {
                if (collection is IDisposable disposable) disposable.Dispose();
            }
            _collections.Clear();
        }

        base.Dispose(disposing);
    }
}

internal static class ConcurrentDictionaryExtensions
{
    public static IEnumerable<object> RemovePrefix(this ConcurrentDictionary<string, object> dictionary, string collectionName)
    {
        string prefix = collectionName + "|";
        foreach (string key in dictionary.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && dictionary.TryRemove(key, out object? value))
                yield return value;
        }
    }
}

