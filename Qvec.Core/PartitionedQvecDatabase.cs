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

    public void AddEntry(float[] vector, string metadata)
    {
        // Om senaste partitionen är full, skapa en ny
        var last = _partitions.LastOrDefault();
        // (Här skulle vi i en riktig app kolla header.CurrentCount via en publik property)

        if (last == null) // Förenklat för demo: lägg alltid i första eller skapa
        {
            var newPart = new QvecDatabase(GetPath(_partitions.Count), _dim, _partitionSize);
            _partitions.Add(newPart);
            last = newPart;
        }

        last.AddEntry(vector, metadata);
    }
    public List<(int Id, float Score, string Metadata)> Search(float[] query, int topK)
    {
        // Sök i alla partitioner samtidigt på olika trådar
        return _partitions
            .AsParallel() // PLINQ för att söka i alla filer parallellt
            .SelectMany(p => p.Search(query, topK))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }
    public List<(int Id, float Score, string Metadata)> Search(float[] query, Func<string, bool> filter, int topK)
    {
        // Sök i alla partitioner samtidigt på olika trådar
        return _partitions
            .AsParallel() // PLINQ för att söka i alla filer parallellt
            .SelectMany(p => p.Search(query, filter, topK))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }
    public void Dispose() => _partitions.ForEach(p => p.Dispose());
}
