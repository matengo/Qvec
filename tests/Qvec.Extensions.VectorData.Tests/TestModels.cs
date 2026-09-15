using System.Text.Json.Serialization;

namespace Qvec.Extensions.VectorData.Tests;

public sealed class TestRecord
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public float[] Vector { get; set; } = [];
}

public sealed class StringKeyRecord
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public float[] Vector { get; set; } = [];
}

public sealed class UnsupportedKeyRecord
{
    public int Id { get; set; }
    public float[] Vector { get; set; } = [];
}

[JsonSerializable(typeof(TestRecord))]
[JsonSerializable(typeof(StringKeyRecord))]
[JsonSerializable(typeof(UnsupportedKeyRecord))]
internal partial class TestJsonContext : JsonSerializerContext;
