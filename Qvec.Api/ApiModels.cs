public sealed record SearchRequest(float[]? Vector, int TopK = 5);
public sealed record AddVectorRequest(float[]? Vector, string? Metadata, Guid? ExternalId = null);
public sealed record UpdateVectorRequest(float[]? Vector, string? Metadata);
public sealed record VectorResponse(Guid Id, float[] Vector, string Metadata);
public sealed record StatsResponse(
    int TotalVectors,
    int LiveVectors,
    int DeletedVectors,
    int MaxCount,
    int VectorDimension,
    int EntryPointIndex,
    List<LayerStat> LayerDistribution,
    double FileSizeMb);
public sealed record LayerStat(int Layer, int Count);
public sealed record AddVectorResponse(Guid Id);
public sealed record DeleteVectorResponse(bool Deleted, Guid Id);
public sealed record UpdateVectorResponse(bool Updated, Guid Id);
public sealed record SearchResponse(Guid Id, float Score, string Metadata);