using Qvec.Core;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

public sealed class QvecOptions
{
    public const string SectionName = "Qvec";

    [Required(AllowEmptyStrings = false)]
    public string Path { get; set; } = "vectors.qvec";

    [Range(1, int.MaxValue)]
    public int Dimension { get; set; } = 1536;

    [Range(1, int.MaxValue)]
    public int MaxCount { get; set; } = 100000;

    [Range(1, int.MaxValue)]
    public int M { get; set; } = 32;

    [Range(1, int.MaxValue)]
    public int MaxLayers { get; set; } = 5;

    [EnumDataType(typeof(DistanceFunction))]
    public DistanceFunction DistanceFunction { get; set; } = DistanceFunction.DotProduct;

    [Range(1, int.MaxValue)]
    public int MaxTopK { get; set; } = 1000;

    public QvecAuthenticationOptions Authentication { get; set; } = new();

    [ValidateObjectMembers]
    public QvecRateLimitOptions RateLimit { get; set; } = new();
}

public sealed class QvecAuthenticationOptions
{
    public bool Enabled { get; set; } = true;

    public string? ApiKey { get; set; }
}

public sealed class QvecRateLimitOptions
{
    public bool Enabled { get; set; } = true;

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 100;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}