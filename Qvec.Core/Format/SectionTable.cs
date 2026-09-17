using System;

namespace Qvec.Core.Format;

[Flags]
public enum SectionFlags : uint
{
    None = 0,
    Present = 1 << 0,
    Required = 1 << 1,
    Mutable = 1 << 2,
    AppendOnly = 1 << 3,
    MayMoveOnGrow = 1 << 4,
}

public static class V4SectionIds
{
    public const uint Unused = 0;
    public const uint Vectors = 1;
    public const uint Graph = 2;
    public const uint MetadataDescriptors = 3;
    public const uint MetadataHeap = 4;
    public const uint Guids = 5;
    public const uint Tombstones = 6;
    public const uint FreeList = 7;
    public const uint QuantizedVectors = 8;
    public const uint QuantizationDatasetParameters = 9;
    public const uint QuantizationVectorParameters = 10;
    /// <summary>Per-row (Hlc, Origin) version, 24 bytes each. Present only when the header has <see cref="V4HeaderFlags.HasChangeTracking"/>.</summary>
    public const uint EntryVersions = 11;
    /// <summary>Ring buffer of 64-byte change records. Present only when the header has <see cref="V4HeaderFlags.HasChangeTracking"/>.</summary>
    public const uint ChangeLog = 12;
}

public sealed class SectionTableEntry
{
    public const int Size = 32;

    public const int SectionIdOffset = 0;
    public const int SectionFlagsOffset = 4;
    public const int OffsetOffset = 8;
    public const int LengthOffset = 16;
    public const int ElementSizeOffset = 24;
    public const int ReservedOffset = 28;

    public uint SectionId { get; set; }
    public SectionFlags SectionFlags { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public uint ElementSize { get; set; }
    public uint Reserved { get; set; }

    public SectionTableEntry Clone() => new()
    {
        SectionId = SectionId,
        SectionFlags = SectionFlags,
        Offset = Offset,
        Length = Length,
        ElementSize = ElementSize,
        Reserved = Reserved,
    };
}

public readonly record struct SectionExtent(uint SectionId, long Offset, long Length, uint ElementSize);
