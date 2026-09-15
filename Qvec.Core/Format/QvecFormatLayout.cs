using System;

namespace Qvec.Core.Format;

public static class QvecFormatLayout
{
    private const int Alignment = 64;
    private const long MinMetadataHeapCapacity = 64L * 1024L;
    private const long MaxInitialMetadataHeapCapacity = 64L * 1024L * 1024L;

    public static V4Header CreateInitial(
        int vectorDimension,
        long maxCount,
        int maxNeighbors,
        int maxLayers,
        long metadataHeapCapacity,
        DistanceFunction distanceFunction = DistanceFunction.DotProduct)
    {
        ValidateCreateArguments(vectorDimension, maxCount, maxNeighbors, maxLayers, metadataHeapCapacity, distanceFunction);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new V4Header
        {
            VectorDimension = vectorDimension,
            CurrentCount = 0,
            MaxCountRaw = maxCount,
            MaxNeighbors = maxNeighbors,
            MaxLayers = maxLayers,
            LayerProbability = 1.0 / Math.Log(maxNeighbors),
            EntryPoint = -1,
            EntryPointLevel = 0,
            DeletedCount = 0,
            DistanceFunction = distanceFunction,
            MetadataHeapUsed = 0,
            FreeListHead = -1,
            FreeListCount = 0,
            QuantizationMode = 0,
            QuantizationSectionId = 0,
            MetadataHeapCapacity = metadataHeapCapacity,
            CreatedUnixTimeSeconds = now,
            UpdatedUnixTimeSeconds = now,
        };

        var offset = (long)V4Header.HeaderSizeValue;
        AddSection(header.Sections[0], V4SectionIds.Vectors, ref offset, maxCount, checked((uint)(vectorDimension * sizeof(float))));
        AddSection(header.Sections[1], V4SectionIds.Graph, ref offset, maxCount, checked((uint)(maxLayers * maxNeighbors * sizeof(int))));
        AddSection(header.Sections[2], V4SectionIds.MetadataDescriptors, ref offset, maxCount, 16);
        AddSection(header.Sections[3], V4SectionIds.Guids, ref offset, maxCount, 16);
        AddSection(header.Sections[4], V4SectionIds.Tombstones, ref offset, maxCount, 1);
        AddSection(header.Sections[5], V4SectionIds.FreeList, ref offset, maxCount, 8);
        AddByteSection(header.Sections[6], V4SectionIds.MetadataHeap, ref offset, metadataHeapCapacity, SectionFlags.AppendOnly);

        header.NextSectionDataOffset = offset;
        header.FileLength = offset;

        return header;
    }

    public static long RecommendMetadataHeapCapacity(long maxCount)
    {
        if (maxCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), "MaxCount must be non-negative.");
        }

        return Math.Max(MinMetadataHeapCapacity, Math.Min(MaxInitialMetadataHeapCapacity, checked(maxCount * 128L)));
    }

    private static void AddSection(
        SectionTableEntry entry,
        uint sectionId,
        ref long offset,
        long elementCount,
        uint elementSize,
        SectionFlags additionalFlags = SectionFlags.None)
    {
        AddByteSection(entry, sectionId, ref offset, checked(elementCount * (long)elementSize), additionalFlags, elementSize);
    }

    private static void AddByteSection(
        SectionTableEntry entry,
        uint sectionId,
        ref long offset,
        long length,
        SectionFlags additionalFlags,
        uint elementSize = 1)
    {
        offset = Align(offset);
        entry.SectionId = sectionId;
        entry.SectionFlags = SectionFlags.Present | SectionFlags.Required | SectionFlags.Mutable | SectionFlags.MayMoveOnGrow | additionalFlags;
        entry.Offset = offset;
        entry.Length = length;
        entry.ElementSize = elementSize;
        entry.Reserved = 0;
        offset = checked(offset + length);
    }

    private static long Align(long value)
    {
        var remainder = value % Alignment;
        return remainder == 0 ? value : checked(value + Alignment - remainder);
    }

    private static void ValidateCreateArguments(
        int vectorDimension,
        long maxCount,
        int maxNeighbors,
        int maxLayers,
        long metadataHeapCapacity,
        DistanceFunction distanceFunction)
    {
        if (vectorDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vectorDimension), "Vector dimension must be greater than zero.");
        }

        if (maxCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), "Max count must be non-negative.");
        }

        if (maxNeighbors < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNeighbors), "Max neighbors must be at least 2.");
        }

        if (maxLayers <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLayers), "Max layers must be greater than zero.");
        }

        if (metadataHeapCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadataHeapCapacity), "Metadata heap capacity must be non-negative.");
        }

        if (distanceFunction is not DistanceFunction.DotProduct and not DistanceFunction.Cosine)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceFunction), "Distance function must be DotProduct or Cosine.");
        }
    }
}
