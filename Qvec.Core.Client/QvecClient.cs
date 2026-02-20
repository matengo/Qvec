using System.Linq.Expressions;
using System.Reflection;
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

        public QvecClient(QvecDatabase db) : this(db, null, null) { }

        public QvecClient(QvecDatabase db, JsonTypeInfo<T>? jsonInfo) : this(db, jsonInfo, null) { }

        public QvecClient(QvecDatabase db, IQvecFieldExtractor<T>? extractor) : this(db, null, extractor) { }

        public QvecClient(QvecDatabase db, JsonTypeInfo<T>? jsonInfo, IQvecFieldExtractor<T>? extractor)
        {
            _db = db;
            _jsonInfo = jsonInfo;
            _extractor = extractor;

            _indexedFields = new HashSet<string>(
                typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetCustomAttribute<QvecIndexedAttribute>() != null)
                    .Select(p => p.Name));

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

        private string Serialize(T item) =>
            _jsonInfo is not null
                ? JsonSerializer.Serialize(item, _jsonInfo)
                : JsonSerializer.Serialize(item);

        private T? Deserialize(string json) =>
            _jsonInfo is not null
                ? JsonSerializer.Deserialize(json, _jsonInfo)
                : JsonSerializer.Deserialize<T>(json);

        public Guid AddEntry(float[] vector, T item, Guid? externalId = null)
        {
            string metadata = Serialize(item);
            Guid id = _db.AddEntry(vector, metadata, externalId);

            if (_extractor != null)
            {
                int index = _db.GetCount() - 1;
                _db.AddFieldIndex(index, _extractor.ExtractFields(item));
            }

            return id;
        }

        /// <summary>
        /// Hybridsökning: vektor + filter.
        /// Om filtret är ==‐jämförelser på [QvecIndexed]-properties används det inverterade indexet
        /// för att förfiltrera kandidater, sedan beräknas vektorsimilaritet enbart på matchande poster.
        /// Allt annat faller tillbaka till HNSW + post-filter.
        /// </summary>
        public List<TypedSearchResult<T>> Search(float[] query, Expression<Func<T, bool>> filter, int topK = 5)
        {
            // Försök använda inverterat index för pre-filtrering
            if (_extractor != null && TryExtractLookups(filter.Body, out var lookups))
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

            // Fallback: HNSW + post-filter
            var compiled = filter.Compile();
            var fallbackResults = _db.Search(query, meta =>
            {
                var obj = Deserialize(meta);
                return obj != null && compiled(obj);
            }, topK);

            return fallbackResults.Select(r => new TypedSearchResult<T>
            {
                Id = r.Id,
                Score = r.Score,
                Item = Deserialize(r.Metadata)
            }).ToList();
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
        /// Filtrerar poster med en expression. Om uttrycket är enkla ==‐jämförelser
        /// på [QvecIndexed]-properties används det inverterade indexet (O(1)).
        /// Allt annat faller tillbaka till full scan.
        /// </summary>
        public List<TypedWhereResult<T>> Where(Expression<Func<T, bool>> predicate, int maxResults = 100)
        {
            if (_extractor != null && TryExtractLookups(predicate.Body, out var lookups))
            {
                var rawResults = _db.WhereIndexed(lookups, maxResults);

                return rawResults.Select(r => new TypedWhereResult<T>
                {
                    Id = r.Id,
                    Item = Deserialize(r.Metadata)
                }).ToList();
            }

            var compiled = predicate.Compile();
            var scanResults = _db.Where(meta =>
            {
                var obj = Deserialize(meta);
                return obj != null && compiled(obj);
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
            return _db.UpdateMetadata(id, metadata);
        }

        // --- Expression tree-analys ---

        private bool TryExtractLookups(Expression body, out List<(string Field, string Value)> lookups)
        {
            lookups = new List<(string, string)>();
            return ExtractFromExpression(body, lookups);
        }

        private bool ExtractFromExpression(Expression expr, List<(string Field, string Value)> lookups)
        {
            if (expr is BinaryExpression binary)
            {
                // p.Category == "x" && p.Brand == "y"
                if (binary.NodeType == ExpressionType.AndAlso)
                    return ExtractFromExpression(binary.Left, lookups)
                        && ExtractFromExpression(binary.Right, lookups);

                // p.Category == "x" eller "x" == p.Category
                if (binary.NodeType == ExpressionType.Equal)
                {
                    if (TryExtractFieldValue(binary.Left, binary.Right, out var field, out var value)
                     || TryExtractFieldValue(binary.Right, binary.Left, out field, out value))
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

        private static bool TryExtractFieldValue(
            Expression memberSide, Expression valueSide,
            out string field, out string value)
        {
            field = null!;
            value = null!;

            if (memberSide is MemberExpression member && member.Member is PropertyInfo prop)
            {
                field = prop.Name;
                var resolved = EvaluateExpression(valueSide);
                if (resolved != null)
                {
                    value = resolved.ToString()!;
                    return true;
                }
            }

            return false;
        }

        private static object? EvaluateExpression(Expression expr)
        {
            if (expr is ConstantExpression constant)
                return constant.Value;

            // Hanterar captured variables: () => capturedVar
            try
            {
                var lambda = Expression.Lambda(expr);
                return lambda.Compile().DynamicInvoke();
            }
            catch
            {
                return null;
            }
        }
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
