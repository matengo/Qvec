using QvecSharp;

public class PartitionedQvecDatabase : IDisposable
{
    private readonly List<QvecDatabase> _partitions = new();
    private readonly int _dim;
    private readonly int _partitionSize;
    private readonly string _basePath;

    public PartitionedQvecDatabase(string basePath, int dim, int partitionSize)
    {
        _basePath = basePath;
        _dim = dim;
        _partitionSize = partitionSize;

        // Ladda existerande partitioner från disk
        int i = 0;
        while (File.Exists(GetPath(i)))
        {
            _partitions.Add(new QvecDatabase(GetPath(i), dim, partitionSize));
            i++;
        }
    }

    private string GetPath(int index) => $"{_basePath}_part_{index}.zvec";

    public Guid AddEntry(float[] vector, string metadata, Guid? externalId = null)
    {
        // Om senaste partitionen är full, skapa en ny
        var last = _partitions.LastOrDefault();

        if (last == null)
        {
            var newPart = new QvecDatabase(GetPath(_partitions.Count), _dim, _partitionSize);
            _partitions.Add(newPart);
            last = newPart;
        }

        return last.AddEntry(vector, metadata, externalId);
    }
    public List<(Guid Id, float Score, string Metadata)> Search(float[] query, int topK)
    {
        return _partitions
            .AsParallel()
            .SelectMany(p => p.Search(query, topK))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }
    public List<(Guid Id, float Score, string Metadata)> Search(float[] query, Func<string, bool> filter, int topK)
    {
        return _partitions
            .AsParallel()
            .SelectMany(p => p.Search(query, filter, topK))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }
    public bool Delete(Guid id)
    {
        foreach (var partition in _partitions)
        {
            if (partition.Delete(id))
                return true;
        }
        return false;
    }
    public bool UpdateMetadata(Guid id, string newMetadata)
    {
        foreach (var partition in _partitions)
        {
            if (partition.UpdateMetadata(id, newMetadata))
                return true;
        }
        return false;
    }
    public bool UpdateVector(Guid id, float[] newVector)
    {
        foreach (var partition in _partitions)
        {
            if (partition.UpdateVector(id, newVector))
                return true;
        }
        return false;
    }
    public bool Update(Guid id, float[] newVector, string newMetadata)
    {
        foreach (var partition in _partitions)
        {
            if (partition.Update(id, newVector, newMetadata))
                return true;
        }
        return false;
    }
    public void Dispose() => _partitions.ForEach(p => p.Dispose());
}
