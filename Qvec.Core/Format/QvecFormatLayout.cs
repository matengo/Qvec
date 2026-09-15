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
        LayOutSections(header, maxCount, vectorDimension, maxNeighbors, maxLayers, metadataHeapCapacity, ref offset);

        header.NextSectionDataOffset = offset;
        header.FileLength = offset;

        return header;
    }

    /// <summary>
    /// Produces the header a database should have after growing to <paramref name="newMaxCount"/>
    /// rows and a metadata heap of <paramref name="newMetadataHeapCapacity"/> bytes.
    /// <para>
    /// Every section keeps its identity and its contents; only offsets and lengths change. The
    /// sections are laid out in the same fixed order as <see cref="CreateInitial"/>, so a grown
    /// file is indistinguishable from one created at the larger size. Because each section grows,
    /// every section after the first moves to a higher offset, which is why the caller must copy
    /// section data back to front.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The new capacities are smaller than the current ones. Growing is the only supported
    /// direction; shrinking is what <c>Vacuum</c> is for.
    /// </exception>
    public static V4Header CreateGrown(V4Header current, long newMaxCount, long newMetadataHeapCapacity)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (newMaxCount < current.MaxCountRaw)
        {
            throw new ArgumentOutOfRangeException(nameof(newMaxCount),
                $"Cannot shrink MaxCount from {current.MaxCountRaw} to {newMaxCount}.");
        }

        if (newMetadataHeapCapacity < current.MetadataHeapCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(newMetadataHeapCapacity),
                $"Cannot shrink the metadata heap from {current.MetadataHeapCapacity} to {newMetadataHeapCapacity}.");
        }

        ValidateCreateArguments(
            current.VectorDimension, newMaxCount, current.MaxNeighbors, current.MaxLayers,
            newMetadataHeapCapacity, current.DistanceFunction);

        var grown = current.CloneState();
        grown.MaxCountRaw = newMaxCount;
        grown.MetadataHeapCapacity = newMetadataHeapCapacity;
        grown.UpdatedUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var offset = (long)V4Header.HeaderSizeValue;
        LayOutSections(
            grown, newMaxCount, current.VectorDimension, current.MaxNeighbors, current.MaxLayers,
            newMetadataHeapCapacity, ref offset);

        grown.NextSectionDataOffset = offset;
        grown.FileLength = offset;

        return grown;
    }

    /// <summary>
    /// The next capacity to grow to. Geometric growth keeps the amortised cost of an insert
    /// constant: growing by a fixed amount would make filling a database quadratic in the number
    /// of grows, and each grow copies the whole file.
    /// </summary>
    public static long RecommendGrownMaxCount(long currentMaxCount)
    {
        if (currentMaxCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentMaxCount), "MaxCount must be non-negative.");
        }

        return Math.Max(currentMaxCount + 1, checked(currentMaxCount + currentMaxCount / 2));
    }

    /// <summary>
    /// The next metadata heap capacity, large enough to hold <paramref name="requiredEnd"/> bytes.
    /// A single blob can be larger than the whole current heap, so the required size has to be a
    /// floor rather than just a growth factor.
    /// </summary>
    public static long RecommendGrownMetadataHeapCapacity(long currentCapacity, long requiredEnd)
    {
        if (currentCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentCapacity), "Capacity must be non-negative.");
        }

        if (requiredEnd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredEnd), "Required end must be non-negative.");
        }

        long geometric = checked(currentCapacity + currentCapacity / 2);
        long stepped = checked(currentCapacity + MinMetadataHeapCapacity);
        return Math.Max(requiredEnd, Math.Max(geometric, stepped));
    }

    private static void LayOutSections(
        V4Header header,
        long maxCount,
        int vectorDimension,
        int maxNeighbors,
        int maxLayers,
        long metadataHeapCapacity,
        ref long offset)
    {
        AddSection(header.Sections[0], V4SectionIds.Vectors, ref offset, maxCount, checked((uint)(vectorDimension * sizeof(float))));
        // Layer 0 gets 2 * maxNeighbors slots (M0 = 2 * M) and every layer above it maxNeighbors,
        // which sums to (maxLayers + 1) * maxNeighbors slots per node.
        AddSection(header.Sections[1], V4SectionIds.Graph, ref offset, maxCount, checked((uint)((maxLayers + 1) * maxNeighbors * sizeof(int))));
        AddSection(header.Sections[2], V4SectionIds.MetadataDescriptors, ref offset, maxCount, 16);
        AddSection(header.Sections[3], V4SectionIds.Guids, ref offset, maxCount, 16);
        AddSection(header.Sections[4], V4SectionIds.Tombstones, ref offset, maxCount, 1);
        AddSection(header.Sections[5], V4SectionIds.FreeList, ref offset, maxCount, 8);
        AddByteSection(header.Sections[6], V4SectionIds.MetadataHeap, ref offset, metadataHeapCapacity, SectionFlags.AppendOnly);
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
