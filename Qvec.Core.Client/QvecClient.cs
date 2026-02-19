using QvecSharp;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Qvec.Core.Client
{
    public class QvecClient<T> where T : class
    {
        private readonly QvecDatabase _db;
        private readonly JsonTypeInfo<T> _jsonInfo;
        public QvecClient(QvecDatabase db, JsonTypeInfo<T> jsonInfo)
        {
            _db = db;
            _jsonInfo = jsonInfo;
        }
        public Guid AddEntry(float[] vector, T item, Guid? externalId = null)
        {
            string metadata = JsonSerializer.Serialize(item, _jsonInfo);
            return _db.AddEntry(vector, metadata, externalId);
        }
        public List<TypedSearchResult<T>> Search(float[] query, Func<T, bool> filter, int topK = 5)
        {
            var rawResults = _db.Search(query, meta =>
            {
                var obj = JsonSerializer.Deserialize(meta, _jsonInfo);
                return obj != null && filter(obj);
            }, topK);

            return rawResults.Select(r => new TypedSearchResult<T>
            {
                Id = r.Id,
                Score = r.Score,
                Item = JsonSerializer.Deserialize(r.Metadata, _jsonInfo)
            }).ToList();
        }
        public bool DeleteEntry(Guid id)
        {
            return _db.Delete(id);
        }
        public bool UpdateEntry(Guid id, float[] newVector, T newItem)
        {
            string metadata = JsonSerializer.Serialize(newItem, _jsonInfo);
            return _db.Update(id, newVector, metadata);
        }
        public bool UpdateMetadata(Guid id, T newItem)
        {
            string metadata = JsonSerializer.Serialize(newItem, _jsonInfo);
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
