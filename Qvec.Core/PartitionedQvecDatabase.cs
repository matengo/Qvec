using System.Text.RegularExpressions;

namespace Qvec.Core
{
    public class PartitionedQvecDatabase : IDisposable
    {
        private readonly List<QvecDatabase> _partitions = new();
        // Protects partition discovery, rollover, and disposal so concurrent adds cannot race into the same full shard.
        private readonly object _syncRoot = new();
        private readonly int _dim;
        private readonly int _partitionSize;
        private readonly string _basePath;
        private DistanceFunction _distanceFunction = DistanceFunction.DotProduct;
        private int _nextPartitionIndex;
        private bool _disposed;

        public PartitionedQvecDatabase(string basePath, int dim, int partitionSize)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
            if (dim <= 0)
                throw new ArgumentOutOfRangeException(nameof(dim), dim, "Vector dimension must be greater than zero.");
            if (partitionSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(partitionSize), partitionSize, "Partition size must be greater than zero.");

            _basePath = basePath;
            _dim = dim;
            _partitionSize = partitionSize;

            try
            {
                LoadExistingPartitions();
            }
            catch
            {
                DisposeLoadedPartitions();
                throw;
            }
        }

        private string GetPath(int index) => $"{_basePath}_part_{index}.zvec";

        public int VectorDimension => _dim;
        public int PartitionSize => _partitionSize;
        public int MaxCount { get { lock (_syncRoot) { ThrowIfDisposed(); return _partitions.Sum(p => p.MaxCount); } } }
        public int DeletedCount { get { lock (_syncRoot) { ThrowIfDisposed(); return _partitions.Sum(p => p.DeletedCount); } } }
        public int LiveCount { get { lock (_syncRoot) { ThrowIfDisposed(); return _partitions.Sum(p => p.LiveCount); } } }
        public const int DefaultEfSearch = QvecDatabase.DefaultEfSearch;
        public const int DefaultEfConstruction = QvecDatabase.DefaultEfConstruction;

        private void LoadExistingPartitions()
        {
            var existing = DiscoverPartitionFiles().OrderBy(p => p.Index).ToList();
            if (existing.Count == 0) return;

            DistanceFunction? expectedDistance = null;
            foreach (var (Index, Path) in existing)
            {
                QvecDatabase? partition = null;
                try
                {
                    partition = QvecDatabase.Open(Path);
                    DistanceFunction partitionDistance = partition.DistanceFunction;

                    if (partition.VectorDimension != _dim)
                        throw new QvecFormatException(
                            $"Partition '{Path}' has vector dimension {partition.VectorDimension}, but this partitioned database expects {_dim}.");

                    expectedDistance ??= partitionDistance;
                    if (partitionDistance != expectedDistance.Value)
                        throw new QvecFormatException(
                            $"Partition '{Path}' uses distance function {partitionDistance}, but earlier partitions use {expectedDistance.Value}.");

                    _partitions.Add(partition);
                    partition = null;
                    _nextPartitionIndex = Index + 1;
                }
                finally
                {
                    partition?.Dispose();
                }
            }

            _distanceFunction = expectedDistance!.Value;
        }

        private IEnumerable<(int Index, string Path)> DiscoverPartitionFiles()
        {
            string? directory = Path.GetDirectoryName(_basePath);
            if (string.IsNullOrEmpty(directory))
                directory = Directory.GetCurrentDirectory();

            string prefix = Path.GetFileName(_basePath);
            if (!Directory.Exists(directory))
                yield break;

            var regex = new Regex("^" + Regex.Escape(prefix) + @"_part_(\d+)\.zvec$", RegexOptions.CultureInvariant);

            foreach (string path in Directory.EnumerateFiles(directory, $"{prefix}_part_*.zvec"))
            {
                var match = regex.Match(Path.GetFileName(path));
                if (match.Success && int.TryParse(match.Groups[1].Value, out int index))
                    yield return (index, path);
            }
        }


        private QvecDatabase CreateNextPartition()
        {
            var partition = new QvecDatabase(GetPath(_nextPartitionIndex), _dim, _partitionSize, distanceFunction: _distanceFunction);
            _partitions.Add(partition);
            _nextPartitionIndex++;
            return partition;
        }

        private void ValidateVector(float[] vector, string paramName)
        {
            ArgumentNullException.ThrowIfNull(vector, paramName);
            if (vector.Length != _dim)
                throw new QvecDimensionException(_dim, vector.Length, paramName);
        }

        private static void ValidateTopK(int topK)
        {
            if (topK <= 0)
                throw new ArgumentOutOfRangeException(nameof(topK), topK, "topK must be greater than zero.");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public Guid AddEntry(float[] vector, string metadata, Guid? externalId = null)
        {
            ValidateVector(vector, nameof(vector));
            ArgumentNullException.ThrowIfNull(metadata);

            lock (_syncRoot)
            {
                ThrowIfDisposed();

                if (externalId.HasValue && GetByGuidCore(externalId.Value) is not null)
                    return externalId.Value;

                while (true)
                {
                    var last = _partitions.LastOrDefault();
                    if (last == null || last.LiveCount >= last.MaxCount)
                        last = CreateNextPartition();

                    try
                    {
                        return last.AddEntry(vector, metadata, externalId);
                    }
                    catch (QvecFullException) when (ReferenceEquals(last, _partitions.LastOrDefault()))
                    {
                        CreateNextPartition();
                    }
                }
            }
        }

        public List<(Guid Id, float Score, string Metadata)> Search(float[] query, int topK, int efSearch = DefaultEfSearch)
        {
            ValidateVector(query, nameof(query));
            ValidateTopK(topK);

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                return _partitions
                    .AsParallel()
                    .SelectMany(p => p.Search(query, topK, efSearch))
                    .OrderByDescending(r => r.Score)
                    .Take(topK)
                    .ToList();
            }
        }

        public List<(Guid Id, float Score, string Metadata)> Search(float[] query, Func<string, bool> filter, int topK, int efSearch = DefaultEfSearch)
        {
            ValidateVector(query, nameof(query));
            ArgumentNullException.ThrowIfNull(filter);
            ValidateTopK(topK);

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                return _partitions
                    .AsParallel()
                    .SelectMany(p => p.Search(query, filter, topK, efSearch))
                    .OrderByDescending(r => r.Score)
                    .Take(topK)
                    .ToList();
            }
        }

        public (float[] Vector, string Metadata)? GetByGuid(Guid id)
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                return GetByGuidCore(id);
            }
        }

        private (float[] Vector, string Metadata)? GetByGuidCore(Guid id)
        {
            foreach (var partition in _partitions)
            {
                var result = partition.GetByGuid(id);
                if (result is not null)
                    return result;
            }

            return null;
        }

        public bool Delete(Guid id)
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                foreach (var partition in _partitions)
                {
                    if (partition.Delete(id))
                        return true;
                }
                return false;
            }
        }

        public bool UpdateMetadata(Guid id, string newMetadata)
        {
            ArgumentNullException.ThrowIfNull(newMetadata);

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                foreach (var partition in _partitions)
                {
                    if (partition.UpdateMetadata(id, newMetadata))
                        return true;
                }
                return false;
            }
        }

        public bool UpdateVector(Guid id, float[] newVector)
        {
            ValidateVector(newVector, nameof(newVector));

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                foreach (var partition in _partitions)
                {
                    if (partition.UpdateVector(id, newVector))
                        return true;
                }
                return false;
            }
        }

        public bool Update(Guid id, float[]? newVector, string? newMetadata)
        {
            if (newVector is not null) ValidateVector(newVector, nameof(newVector));
            if (newVector is null && newMetadata is null)
                throw new ArgumentException("At least one of newVector or newMetadata must be supplied.");

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                foreach (var partition in _partitions)
                {
                    if (partition.Update(id, newVector, newMetadata))
                        return true;
                }
                return false;
            }
        }

        public void Dispose()
        {
            List<Exception>? exceptions = null;

            lock (_syncRoot)
            {
                if (_disposed) return;
                _disposed = true;

                foreach (var partition in _partitions)
                {
                    try
                    {
                        partition.Dispose();
                    }
                    catch (Exception ex)
                    {
                        exceptions ??= new List<Exception>();
                        exceptions.Add(ex);
                    }
                }

                _partitions.Clear();
            }

            if (exceptions is not null)
                throw new AggregateException("One or more partitions failed to dispose.", exceptions);
        }

        private void DisposeLoadedPartitions()
        {
            foreach (var partition in _partitions)
            {
                try
                {
                    partition.Dispose();
                }
                catch
                {
                }
            }

            _partitions.Clear();
        }
    }
}
