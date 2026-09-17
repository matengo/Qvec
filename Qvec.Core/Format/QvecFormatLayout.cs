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
        DistanceFunction distanceFunction = DistanceFunction.DotProduct,
        VectorQuantization quantization = VectorQuantization.None,
        bool trackChanges = false,
        long changeLogCapacity = 0)
    {
        ValidateCreateArguments(vectorDimension, maxCount, maxNeighbors, maxLayers, metadataHeapCapacity, distanceFunction);
        if (quantization is not VectorQuantization.None and not VectorQuantization.Int8 and not VectorQuantization.Int8Rescored)
        {
            throw new ArgumentOutOfRangeException(nameof(quantization), "Quantization must be None, Int8 or Int8Rescored.");
        }
        changeLogCapacity = ResolveChangeLogCapacity(trackChanges, changeLogCapacity, maxCount);

        // Rescoring is not a header mode of its own: the file is an int8 file that also carries
        // the float section, so QuantizationMode stays 2 and older readers open it as plain int8.
        bool storeFloats = quantization == VectorQuantization.Int8Rescored;
        int headerMode = quantization == VectorQuantization.None ? 0 : (int)VectorQuantization.Int8;

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
            QuantizationMode = headerMode,
            QuantizationSectionId = headerMode == 0 ? 0 : (int)V4SectionIds.QuantizedVectors,
            HeaderFlags = storeFloats ? V4HeaderFlags.HasOptionalSections : V4HeaderFlags.None,
            MetadataHeapCapacity = metadataHeapCapacity,
            CreatedUnixTimeSeconds = now,
            UpdatedUnixTimeSeconds = now,
        };

        if (trackChanges) MarkTracked(header, now);

        var offset = (long)V4Header.HeaderSizeValue;
        LayOutSections(header, maxCount, vectorDimension, maxNeighbors, maxLayers, metadataHeapCapacity, storeFloats, changeLogCapacity, ref offset);

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

        // The log grows in step with the rows so a batch that fills the file never rotates its
        // own records out. A caller-chosen capacity keeps its ratio to MaxCount.
        long newLogCapacity = 0;
        if (current.HasChangeTracking)
        {
            long oldCapacity = current.ChangeLogCapacity;
            newLogCapacity = current.MaxCountRaw == 0
                ? Math.Max(oldCapacity, newMaxCount)
                : Math.Max(oldCapacity, checked(oldCapacity * newMaxCount / current.MaxCountRaw));
        }

        return Relayout(current, newMaxCount, newMetadataHeapCapacity, newLogCapacity);
    }

    /// <summary>
    /// The header an untracked database gets when change tracking is enabled on it: identical
    /// geometry, plus the <see cref="V4SectionIds.EntryVersions"/> and
    /// <see cref="V4SectionIds.ChangeLog"/> sections appended after the existing ones, the
    /// <see cref="V4HeaderFlags.HasChangeTracking"/> flag, a fresh <c>ReplicaId</c> and format
    /// version 6. Existing sections keep their offsets, so no data has to move.
    /// </summary>
    /// <param name="changeLogCapacity">Records in the ring; 0 means "as many as MaxCount".</param>
    /// <exception cref="InvalidOperationException">The header already has change tracking.</exception>
    public static V4Header CreateTracked(V4Header current, long changeLogCapacity = 0)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.HasChangeTracking)
        {
            throw new InvalidOperationException("The database already has change tracking enabled.");
        }

        changeLogCapacity = ResolveChangeLogCapacity(trackChanges: true, changeLogCapacity, current.MaxCountRaw);

        var tracked = Relayout(current, current.MaxCountRaw, current.MetadataHeapCapacity, changeLogCapacity, markTracked: true);
        return tracked;
    }

    private static V4Header Relayout(
        V4Header current, long newMaxCount, long newMetadataHeapCapacity, long changeLogCapacity, bool markTracked = false)
    {
        var grown = current.CloneState();
        grown.MaxCountRaw = newMaxCount;
        grown.MetadataHeapCapacity = newMetadataHeapCapacity;
        grown.UpdatedUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (markTracked) MarkTracked(grown, grown.UpdatedUnixTimeSeconds);

        // CloneState copies the scalar state, not the section table, so whether the file carries
        // the optional float section has to be read off the current table.
        bool storeFloats = HasRescoringFloats(current);

        var offset = (long)V4Header.HeaderSizeValue;
        LayOutSections(
            grown, newMaxCount, current.VectorDimension, current.MaxNeighbors, current.MaxLayers,
            newMetadataHeapCapacity, storeFloats, changeLogCapacity, ref offset);

        grown.NextSectionDataOffset = offset;
        grown.FileLength = offset;

        return grown;
    }

    private static void MarkTracked(V4Header header, long nowUnixSeconds)
    {
        header.Version = V4Header.ChangeTrackingFormatVersion;
        header.HeaderFlags |= V4HeaderFlags.HasChangeTracking;
        header.ReplicaId = Guid.NewGuid();
        header.TrackingEnabledUnixSeconds = nowUnixSeconds;
    }

    private static long ResolveChangeLogCapacity(bool trackChanges, long changeLogCapacity, long maxCount)
    {
        if (!trackChanges)
        {
            if (changeLogCapacity != 0)
            {
                throw new ArgumentException("A change-log capacity is only meaningful with change tracking.", nameof(changeLogCapacity));
            }
            return 0;
        }

        if (changeLogCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(changeLogCapacity), "Change-log capacity must be non-negative.");
        }

        // Every file needs at least one record slot, or the ring's head index has nowhere valid to point.
        return changeLogCapacity == 0 ? Math.Max(1, maxCount) : changeLogCapacity;
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

    /// <summary>
    /// True when an int8 header also carries the optional float <see cref="V4SectionIds.Vectors"/>
    /// section, i.e. the file was created with <see cref="VectorQuantization.Int8Rescored"/>.
    /// </summary>
    public static bool HasRescoringFloats(V4Header header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return header.QuantizationMode != 0 && header.TryGetSection(V4SectionIds.Vectors, out _);
    }

    /// <summary>
    /// The API-level quantization a header describes: the header mode, promoted to
    /// <see cref="VectorQuantization.Int8Rescored"/> when the optional float section is present.
    /// </summary>
    public static VectorQuantization EffectiveQuantization(V4Header header)
        => HasRescoringFloats(header) ? VectorQuantization.Int8Rescored : (VectorQuantization)header.QuantizationMode;

    /// <summary>
    /// Sections come in a fixed order. Slot 0 is vector storage in whichever form the header's
    /// quantization mode calls for; slots 1–6 are identical in both modes so code that walks the
    /// graph and metadata never has to care. Int8 files add the parameter section in slot 7, and
    /// rescored int8 files add the float vectors as an optional (non-Required) section in slot 8.
    /// Tracked files add per-row versions in slot 9 and the change-log ring in slot 10; they come
    /// last so enabling tracking on an existing file appends rather than moves.
    /// </summary>
    private static void LayOutSections(
        V4Header header,
        long maxCount,
        int vectorDimension,
        int maxNeighbors,
        int maxLayers,
        long metadataHeapCapacity,
        bool storeFloats,
        long changeLogCapacity,
        ref long offset)
    {
        bool quantized = header.QuantizationMode != 0;
        uint floatElementSize = checked((uint)(vectorDimension * sizeof(float)));
        if (quantized)
        {
            AddSection(header.Sections[0], V4SectionIds.QuantizedVectors, ref offset, maxCount, checked((uint)vectorDimension));
        }
        else
        {
            AddSection(header.Sections[0], V4SectionIds.Vectors, ref offset, maxCount, floatElementSize);
        }
        // Layer 0 gets 2 * maxNeighbors slots (M0 = 2 * M) and every layer above it maxNeighbors,
        // which sums to (maxLayers + 1) * maxNeighbors slots per node.
        AddSection(header.Sections[1], V4SectionIds.Graph, ref offset, maxCount, checked((uint)((maxLayers + 1) * maxNeighbors * sizeof(int))));
        AddSection(header.Sections[2], V4SectionIds.MetadataDescriptors, ref offset, maxCount, 16);
        AddSection(header.Sections[3], V4SectionIds.Guids, ref offset, maxCount, 16);
        AddSection(header.Sections[4], V4SectionIds.Tombstones, ref offset, maxCount, 1);
        AddSection(header.Sections[5], V4SectionIds.FreeList, ref offset, maxCount, 8);
        AddByteSection(header.Sections[6], V4SectionIds.MetadataHeap, ref offset, metadataHeapCapacity, SectionFlags.AppendOnly);
        if (quantized)
        {
            AddSection(header.Sections[7], V4SectionIds.QuantizationVectorParameters, ref offset, maxCount, 16);
        }
        if (storeFloats)
        {
            if (!quantized)
            {
                throw new ArgumentException("Float vectors are already the primary storage of an unquantized file.", nameof(storeFloats));
            }
            AddSection(header.Sections[8], V4SectionIds.Vectors, ref offset, maxCount, floatElementSize, required: false);
        }
        if (header.HasChangeTracking)
        {
            AddSection(header.Sections[9], V4SectionIds.EntryVersions, ref offset, maxCount, V4Header.EntryVersionSize);
            AddSection(header.Sections[10], V4SectionIds.ChangeLog, ref offset, changeLogCapacity, V4Header.ChangeRecordSize);
        }
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
        SectionFlags additionalFlags = SectionFlags.None,
        bool required = true)
    {
        AddByteSection(entry, sectionId, ref offset, checked(elementCount * (long)elementSize), additionalFlags, elementSize, required);
    }

    private static void AddByteSection(
        SectionTableEntry entry,
        uint sectionId,
        ref long offset,
        long length,
        SectionFlags additionalFlags,
        uint elementSize = 1,
        bool required = true)
    {
        offset = Align(offset);
        entry.SectionId = sectionId;
        entry.SectionFlags = SectionFlags.Present | SectionFlags.Mutable | SectionFlags.MayMoveOnGrow | additionalFlags;
        if (required) entry.SectionFlags |= SectionFlags.Required;
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

        if (distanceFunction is not DistanceFunction.DotProduct
            and not DistanceFunction.Cosine
            and not DistanceFunction.Euclidean)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceFunction), "Distance function must be DotProduct, Cosine or Euclidean.");
        }
    }
}
