using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Qvec.Core.Client
{
    public class QvecClient<T> where T : class
    {
        private readonly QvecDatabase _db;
        private readonly JsonTypeInfo<T>? _jsonInfo;
        private readonly IQvecFieldExtractor<T>? _extractor;
        private readonly HashSet<string> _indexedFields;

        private const string JsonReflectionMessage =
            "This constructor uses System.Text.Json reflection-based metadata when serializing or deserializing. " +
            "Use the JsonTypeInfo overload to provide source-generated metadata for trimming and Native AOT.";

        private const string ExpressionDynamicCodeMessage =
            "Expression-based filtering may compile expression trees at runtime. Use the Func<T, bool> overload for a scan-only path that does not require dynamic code.";

        [RequiresUnreferencedCode(JsonReflectionMessage)]
        [RequiresDynamicCode(JsonReflectionMessage)]
        public QvecClient(QvecDatabase db) : this(db, null, null, usesReflectionJson: true) { }

        public QvecClient(QvecDatabase db, JsonTypeInfo<T> jsonInfo)
            : this(db, RequireJsonInfo(jsonInfo), null, usesReflectionJson: false) { }

        [RequiresUnreferencedCode(JsonReflectionMessage)]
        [RequiresDynamicCode(JsonReflectionMessage)]
        public QvecClient(QvecDatabase db, IQvecFieldExtractor<T>? extractor)
            : this(db, null, extractor, usesReflectionJson: true) { }

        public QvecClient(QvecDatabase db, JsonTypeInfo<T> jsonInfo, IQvecFieldExtractor<T>? extractor)
            : this(db, RequireJsonInfo(jsonInfo), extractor, usesReflectionJson: false) { }

        private QvecClient(QvecDatabase db, JsonTypeInfo<T>? jsonInfo, IQvecFieldExtractor<T>? extractor, bool usesReflectionJson)
        {
            _db = db;
            _jsonInfo = jsonInfo;
            _extractor = extractor;

            _indexedFields = new HashSet<string>(StringComparer.Ordinal);
            if (_extractor != null)
            {
                foreach (var field in _extractor.IndexedFields)
                {
                    _indexedFields.Add(field);
                }
            }

            if (_extractor != null)
            {
                _db.RebuildFieldIndex(meta =>
                {
                    var obj = Deserialize(meta);
                    return obj != null
                        ? _extractor.ExtractFields(obj)
                        : Array.Empty<(string Field, string Value)>();
                });
            }
        }

        private static JsonTypeInfo<T> RequireJsonInfo(JsonTypeInfo<T> jsonInfo)
        {
            ArgumentNullException.ThrowIfNull(jsonInfo);
            return jsonInfo;
        }
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Callers opt into this fallback through constructors annotated with RequiresUnreferencedCode; JsonTypeInfo callers use the source-generated branch.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Callers opt into this fallback through constructors annotated with RequiresDynamicCode; JsonTypeInfo callers use the source-generated branch.")]
        private string Serialize(T item) =>
            _jsonInfo is not null
                ? JsonSerializer.Serialize(item, _jsonInfo)
                : SerializeWithReflection(item);

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Callers opt into this fallback through constructors annotated with RequiresUnreferencedCode; JsonTypeInfo callers use the source-generated branch.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Callers opt into this fallback through constructors annotated with RequiresDynamicCode; JsonTypeInfo callers use the source-generated branch.")]
        private T? Deserialize(string json) =>
            _jsonInfo is not null
                ? JsonSerializer.Deserialize(json, _jsonInfo)
                : DeserializeWithReflection(json);

        [RequiresUnreferencedCode(JsonReflectionMessage)]
        [RequiresDynamicCode(JsonReflectionMessage)]
        private static string SerializeWithReflection(T item) => JsonSerializer.Serialize(item);

        [RequiresUnreferencedCode(JsonReflectionMessage)]
        [RequiresDynamicCode(JsonReflectionMessage)]
        private static T? DeserializeWithReflection(string json) => JsonSerializer.Deserialize<T>(json);

        public Guid AddEntry(VectorData<T> data)
        {
            string metadata = Serialize(data.Item);

            // A duplicate external id is a no-op in the core, so the new object's terms must
            // not be attached to the existing row — that made an unrelated entry queryable
            // under this object's field values.
            bool alreadyPresent = data.externalId is Guid existing && _db.GetByGuid(existing) is not null;

            Guid id = _db.AddEntry(data.vector, metadata, data.externalId);

            if (_extractor != null && !alreadyPresent)
            {
                // Resolve the row by Guid. Deriving it from GetCount() - 1 pointed at the wrong
                // row whenever the insert was deduplicated by external id or reused a tombstoned
                // slot, which silently indexed one entry's fields against another entry's data.
                _db.AddFieldIndex(id, _extractor.ExtractFields(data.Item));
            }

            return id;
        }

        /// <summary>
        /// Hybrid search: vector + expression filter.
        /// Simple == comparisons on [QvecIndexed] properties of the lambda parameter use the inverted
        /// index as a fast candidate prefilter. Other expressions fall back to HNSW + post-filter and
        /// may require runtime expression compilation; use the Func overload for an AOT-safe scan-only path.
        /// </summary>
        [RequiresDynamicCode(ExpressionDynamicCodeMessage)]
        public List<TypedSearchResult<T>> Search(float[] query, Expression<Func<T, bool>> filter, int topK = 5)
        {
            // Försök använda inverterat index för pre-filtrering
            if (_extractor != null && TryExtractLookups(filter.Body, filter.Parameters[0], out var lookups))
            {
                var candidates = _db.GetIndexedCandidates(lookups);
                if (candidates != null)
                {
                    var rawResults = _db.SearchWithCandidates(query, candidates, topK);

                    return rawResults.Select(r => new TypedSearchResult<T>
                    {
                        Id = r.Id,
                        Score = r.Score,
                        Item = Deserialize(r.Metadata)
                    }).ToList();
                }
            }

            return SearchScan(query, filter.Compile(), topK);
        }

        public List<TypedSearchResult<T>> Search(float[] query, int topK = 5)
        {
            var rawResults = _db.Search(query, topK);

            return rawResults.Select(r => new TypedSearchResult<T>
            {
                Id = r.Id,
                Score = r.Score,
                Item = Deserialize(r.Metadata)
            }).ToList();
        }

        /// <summary>
        /// Filters entries with an expression. Simple == comparisons on [QvecIndexed] properties of
        /// the lambda parameter use the inverted index (O(1)); all other expressions fall back to a
        /// full scan and may require runtime expression compilation. Use the Func overload for an
        /// AOT-safe scan-only path.
        /// </summary>
        [RequiresDynamicCode(ExpressionDynamicCodeMessage)]
        public List<TypedWhereResult<T>> Where(Expression<Func<T, bool>> predicate, int maxResults = 100)
        {
            if (_extractor != null && TryExtractLookups(predicate.Body, predicate.Parameters[0], out var lookups))
            {
                var rawResults = _db.WhereIndexed(lookups, maxResults);

                return rawResults.Select(r => new TypedWhereResult<T>
                {
                    Id = r.Id,
                    Item = Deserialize(r.Metadata)
                }).ToList();
            }

            return WhereScan(predicate.Compile(), maxResults);
        }

        internal List<TypedSearchResult<T>> SearchScan(float[] query, Func<T, bool> filter, int topK)
        {
            var fallbackResults = _db.Search(query, meta =>
            {
                var obj = Deserialize(meta);
                return obj != null && filter(obj);
            }, topK);

            return fallbackResults.Select(r => new TypedSearchResult<T>
            {
                Id = r.Id,
                Score = r.Score,
                Item = Deserialize(r.Metadata)
            }).ToList();
        }

        internal List<TypedWhereResult<T>> WhereScan(Func<T, bool> predicate, int maxResults)
        {
            var scanResults = _db.Where(meta =>
            {
                var obj = Deserialize(meta);
                return obj != null && predicate(obj);
            }, maxResults);

            return scanResults.Select(r => new TypedWhereResult<T>
            {
                Id = r.Id,
                Item = Deserialize(r.Metadata)
            }).ToList();
        }

        public bool DeleteEntry(Guid id)
        {
            return _db.Delete(id);
        }

        public bool UpdateEntry(Guid id, float[] newVector, T newItem)
        {
            string metadata = Serialize(newItem);
            return _db.Update(id, newVector, metadata);
        }

        public bool UpdateMetadata(Guid id, T newItem)
        {
            string metadata = Serialize(newItem);
            if (!_db.UpdateMetadata(id, metadata)) return false;

            // The indexed terms describe the old metadata. Leaving them in place kept the
            // entry queryable under values it no longer has.
            if (_extractor != null)
                _db.ReindexFields(id, _extractor.ExtractFields(newItem));

            return true;
        }

        // --- Expression tree-analys ---

        [RequiresDynamicCode(ExpressionDynamicCodeMessage)]
        private bool TryExtractLookups(Expression body, ParameterExpression parameter, out List<(string Field, string Value)> lookups)
        {
            var extracted = new List<(string, string)>();
            if (ExtractFromExpression(body, parameter, extracted))
            {
                lookups = extracted;
                return true;
            }

            lookups = new List<(string, string)>();
            return false;
        }

        [RequiresDynamicCode(ExpressionDynamicCodeMessage)]
        private bool ExtractFromExpression(Expression expr, ParameterExpression parameter, List<(string Field, string Value)> lookups)
        {
            if (expr is BinaryExpression binary)
            {
                // p.Category == "x" && p.Brand == "y"
                if (binary.NodeType == ExpressionType.AndAlso)
                    return ExtractFromExpression(binary.Left, parameter, lookups)
                        && ExtractFromExpression(binary.Right, parameter, lookups);

                if (binary.NodeType == ExpressionType.OrElse)
                {
                    lookups.Clear();
                    return false;
                }

                // p.Category == "x" eller "x" == p.Category
                if (binary.NodeType == ExpressionType.Equal)
                {
                    if (TryExtractFieldValue(binary.Left, binary.Right, parameter, out var field, out var value)
                     || TryExtractFieldValue(binary.Right, binary.Left, parameter, out field, out value))
                    {
                        if (_indexedFields.Contains(field))
                        {
                            lookups.Add((field, value));
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        [RequiresDynamicCode(ExpressionDynamicCodeMessage)]
        private static bool TryExtractFieldValue(
            Expression memberSide, Expression valueSide,
            ParameterExpression parameter,
            out string field, out string value)
        {
            field = null!;
            value = null!;
            if (memberSide is MemberExpression { Expression: var owner } member
                && owner == parameter)
            {
                field = member.Member.Name;
                // If evaluating the value side fails while probing for the indexed fast path,
                // abandon extraction and let the normal scan path run the original predicate.
                if (TryEvaluateExpression(valueSide, out var resolved) && resolved != null)
                {
                    value = ConvertToInvariantString(resolved);
                    return true;
                }
            }

            return false;
        }

        [RequiresDynamicCode(ExpressionDynamicCodeMessage)]
        private static bool TryEvaluateExpression(Expression expr, out object? value)
        {
            if (expr is ConstantExpression constant)
            {
                value = constant.Value;
                return true;
            }

            // Hanterar captured variables: () => capturedVar
            try
            {
                var lambda = Expression.Lambda(expr);
                value = lambda.Compile().DynamicInvoke();
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private static string ConvertToInvariantString(object value) =>
            value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString()!;
    }

    public static class QvecClientFuncExtensions
    {
        /// <summary>
        /// Hybrid search with an AOT-safe Func filter. This overload performs a scan-only post-filter
        /// and does not use the indexed expression fast path.
        /// </summary>
        public static List<TypedSearchResult<T>> Search<T>(
            this QvecClient<T> client,
            float[] query,
            Func<T, bool> filter,
            int topK = 5) where T : class
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(filter);
            return client.SearchScan(query, filter, topK);
        }

        /// <summary>
        /// Filters entries with an AOT-safe Func predicate. This overload always performs a full scan
        /// and does not use the indexed expression fast path.
        /// </summary>
        public static List<TypedWhereResult<T>> Where<T>(
            this QvecClient<T> client,
            Func<T, bool> predicate,
            int maxResults = 100) where T : class
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(predicate);
            return client.WhereScan(predicate, maxResults);
        }
    }
    public class VectorData<T>
    {
        public T Item { get; set; } = default!;
        public float[] vector { get; set; } = Array.Empty<float>();
        public Guid? externalId { get; set; }
    }
    public class TypedSearchResult<T>
    {
        public Guid Id { get; set; }
        public float Score { get; set; }
        public T? Item { get; set; }
    }

    public class TypedWhereResult<T>
    {
        public Guid Id { get; set; }
        public T? Item { get; set; }
    }
}
