using QvecSharp;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Qvec.Core.Client
{
    public class QvecClient<T> where T : class
    {
        private readonly QvecDatabase _db;
        private readonly JsonTypeInfo<T>? _jsonInfo;

        public QvecClient(QvecDatabase db) : this(db, null) { }

        public QvecClient(QvecDatabase db, JsonTypeInfo<T>? jsonInfo)
        {
            _db = db;
            _jsonInfo = jsonInfo;
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
            return _db.AddEntry(vector, metadata, externalId);
        }
        public List<TypedSearchResult<T>> Search(float[] query, Func<T, bool> filter, int topK = 5)
        {
            var rawResults = _db.Search(query, meta =>
            {
                var obj = Deserialize(meta);
                return obj != null && filter(obj);
            }, topK);

            return rawResults.Select(r => new TypedSearchResult<T>
            {
                Id = r.Id,
                Score = r.Score,
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
    }
    public class TypedSearchResult<T>
    {
        public Guid Id { get; set; }
        public float Score { get; set; }
        public T? Item { get; set; }
    }
}
