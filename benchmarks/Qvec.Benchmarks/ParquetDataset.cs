using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Qvec.Benchmarks;

/// <summary>
/// Reader for the Parquet layout used by <c>VectorDBBench</c> (Zilliz), which is what Zvec,
/// Milvus and most vendor benchmarks publish against. A dataset is a directory holding
/// <c>train.parquet</c> (<c>id</c>, <c>emb</c>), <c>test.parquet</c> (same shape) and
/// <c>neighbors.parquet</c> (<c>id</c>, <c>neighbors_id</c>), where the ground truth refers to
/// train <em>ids</em>, not row positions. The ids are remapped to row positions here so the
/// rest of the benchmark can treat this exactly like a TexMex corpus.
/// </summary>
public static class ParquetDataset
{
    public const string TrainFile = "train.parquet";
    public const string TestFile = "test.parquet";
    public const string NeighborsFile = "neighbors.parquet";

    public static (VecBlock<float> Base, VecBlock<float> Queries, VecBlock<int> GroundTruth) Load(
        string directory,
        int maxBaseCount)
    {
        string Require(string file)
        {
            string path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Dataset file '{path}' was not found. Pass --download to fetch it, or point " +
                    "--data at a directory holding the VectorDBBench parquet files.",
                    path);
            }

            return path;
        }

        var (baseIds, baseVectors) = ReadVectors(Require(TrainFile), "id", "emb", maxBaseCount);
        var (queryIds, queries) = ReadVectors(Require(TestFile), "id", "emb", int.MaxValue);
        var (truthQueryIds, truthIds) = ReadIdLists(Require(NeighborsFile), "id", "neighbors_id");

        if (truthQueryIds.Length != queryIds.Length)
        {
            throw new InvalidDataException(
                $"Ground truth has {truthQueryIds.Length} rows but there are {queryIds.Length} queries.");
        }

        for (int q = 0; q < queryIds.Length; q++)
        {
            if (truthQueryIds[q] != queryIds[q])
            {
                throw new InvalidDataException(
                    $"Ground truth row {q} is for query id {truthQueryIds[q]} but test row {q} has id {queryIds[q]}. " +
                    "The files do not belong to the same dataset.");
            }
        }

        var position = new Dictionary<long, int>(baseIds.Length);
        for (int i = 0; i < baseIds.Length; i++) position[baseIds[i]] = i;

        // Ids that are not in the (possibly truncated) base set are mapped to -1, which can never
        // match a returned row, so a truncated base honestly counts as a miss; RecallBenchmark
        // refuses that case anyway.
        var truth = new int[truthIds.Count * truthIds.Dimension];
        for (int i = 0; i < truth.Length; i++)
        {
            truth[i] = position.TryGetValue(truthIds.Values[i], out int row) ? row : -1;
        }

        return (baseVectors, queries, new VecBlock<int>(truth, truthIds.Count, truthIds.Dimension));
    }

    private static (long[] Ids, VecBlock<float> Vectors) ReadVectors(
        string path, string idField, string vectorField, int maxCount)
    {
        using var stream = File.OpenRead(path);
        using var reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();

        var idColumn = FindField(reader.Schema, idField, path);
        var vectorColumn = FindField(reader.Schema, vectorField, path);

        var ids = new List<long>();
        var values = new List<float>();
        int dimension = -1;

        for (int g = 0; g < reader.RowGroupCount && ids.Count < maxCount; g++)
        {
            using var group = reader.OpenRowGroupReader(g);
            var idData = group.ReadColumnAsync(idColumn).GetAwaiter().GetResult();
            var vectorData = group.ReadColumnAsync(vectorColumn).GetAwaiter().GetResult();

            long[] groupIds = ToInt64(idData.Data, path, idField);
            float[] flat = ToSingle(vectorData.Data, path, vectorField);

            if (groupIds.Length == 0) continue;
            if (flat.Length % groupIds.Length != 0)
            {
                throw new InvalidDataException(
                    $"'{path}' row group {g}: {flat.Length} vector components for {groupIds.Length} rows is not a whole number per row.");
            }

            int groupDimension = flat.Length / groupIds.Length;
            if (dimension < 0) dimension = groupDimension;
            else if (dimension != groupDimension)
            {
                throw new InvalidDataException(
                    $"'{path}' row group {g} has dimension {groupDimension} but earlier groups had {dimension}.");
            }

            int take = (int)Math.Min(groupIds.Length, maxCount - ids.Count);
            ids.AddRange(groupIds.AsSpan(0, take));
            values.AddRange(flat.AsSpan(0, take * dimension));
        }

        if (dimension <= 0) throw new InvalidDataException($"'{path}' contains no vectors.");

        return (ids.ToArray(), new VecBlock<float>(values.ToArray(), ids.Count, dimension));
    }

    private static (long[] QueryIds, VecBlock<long> Neighbors) ReadIdLists(string path, string idField, string listField)
    {
        using var stream = File.OpenRead(path);
        using var reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();

        var idColumn = FindField(reader.Schema, idField, path);
        var listColumn = FindField(reader.Schema, listField, path);

        var ids = new List<long>();
        var values = new List<long>();
        int width = -1;

        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            using var group = reader.OpenRowGroupReader(g);
            long[] groupIds = ToInt64(group.ReadColumnAsync(idColumn).GetAwaiter().GetResult().Data, path, idField);
            long[] flat = ToInt64(group.ReadColumnAsync(listColumn).GetAwaiter().GetResult().Data, path, listField);

            if (groupIds.Length == 0) continue;
            if (flat.Length % groupIds.Length != 0)
            {
                throw new InvalidDataException(
                    $"'{path}' row group {g}: ragged neighbour lists are not supported.");
            }

            int groupWidth = flat.Length / groupIds.Length;
            if (width < 0) width = groupWidth;
            else if (width != groupWidth)
            {
                throw new InvalidDataException($"'{path}' row group {g} has width {groupWidth} but earlier groups had {width}.");
            }

            ids.AddRange(groupIds);
            values.AddRange(flat);
        }

        if (width <= 0) throw new InvalidDataException($"'{path}' contains no neighbour lists.");

        return (ids.ToArray(), new VecBlock<long>(values.ToArray(), ids.Count, width));
    }

    private static DataField FindField(ParquetSchema schema, string name, string path)
    {
        foreach (var field in schema.GetDataFields())
        {
            // List columns show up under their own name in Parquet.Net; older writers nest them
            // as list.element, so match on the outermost path segment too.
            if (field.Name == name || field.Path.FirstPart == name) return field;
        }

        throw new InvalidDataException(
            $"'{path}' has no column '{name}'. Columns: {string.Join(", ", schema.GetDataFields().Select(f => f.Path.ToString()))}.");
    }

    private static long[] ToInt64(Array data, string path, string field) => data switch
    {
        long[] longs => longs,
        int[] ints => Array.ConvertAll(ints, static i => (long)i),
        long?[] nullable => Array.ConvertAll(nullable, static v => v ?? throw new InvalidDataException("null id")),
        int?[] nullable => Array.ConvertAll(nullable, static v => (long)(v ?? throw new InvalidDataException("null id"))),
        _ => throw new InvalidDataException($"'{path}' column '{field}' is {data.GetType().GetElementType()}, expected an integer type."),
    };

    private static float[] ToSingle(Array data, string path, string field) => data switch
    {
        float[] floats => floats,
        double[] doubles => Array.ConvertAll(doubles, static d => (float)d),
        float?[] nullable => Array.ConvertAll(nullable, static v => v ?? throw new InvalidDataException("null component")),
        _ => throw new InvalidDataException($"'{path}' column '{field}' is {data.GetType().GetElementType()}, expected float."),
    };
}
