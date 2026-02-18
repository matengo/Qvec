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
        public void AddEntry(float[] vector, T item)
        {
            // Serialisera objektet till JSON-metadata via Source Generator
            string metadata = JsonSerializer.Serialize(item, _jsonInfo);
            _db.AddEntry(vector, metadata);
        }
        public List<TypedSearchResult<T>> Search(float[] query, Func<T, bool> filter, int topK = 5)
        {
            // Använd HybridSearchHNSW med inbyggd deserialisering
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
    }
    public class TypedSearchResult<T>
    {
        public int Id { get; set; }
        public float Score { get; set; }
        public T? Item { get; set; }
    }
}
