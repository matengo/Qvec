using System;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace Qvec.Core.Format;

[Flags]
public enum V4HeaderFlags : uint
{
    None = 0,
    SparseRequested = 1 << 0,
    SparseConfirmed = 1 << 1,
    HasOptionalSections = 1 << 2,
}

[Flags]
public enum V4FormatOptions : uint
{
    None = 0,
    AllowGrow = 1 << 0,
    RequireCleanOpen = 1 << 1,
    HasMetadataHeap = 1 << 2,
    HasPersistentFreeList = 1 << 3,
    ReservedQuantizationHooks = 1 << 4,
}

public sealed class V4Header
{
    public const int MagicNumberValue = 0x5A564543;
    public const int CurrentFormatVersion = 5;
    public const int HeaderSizeValue = 4096;
    public const int PrimaryHeaderSizeValue = 512;
    public const int SectionTableOffsetValue = 512;
    public const int SectionTableEntrySizeValue = 32;
    public const int SectionTableEntryCountValue = 64;
    public const int ReservedHeaderAreaOffset = 2560;

    public const int MagicNumberOffset = 0;
    public const int VersionOffset = 4;
    public const int HeaderSizeOffset = 8;
    public const int PrimaryHeaderSizeOffset = 12;
    public const int SectionTableOffsetOffset = 16;
    public const int SectionTableEntrySizeOffset = 20;
    public const int SectionTableEntryCountOffset = 24;
    public const int HeaderCrc32Offset = 28;
    public const int GenerationOffset = 32;
    public const int WriteInProgressOffset = 40;
    public const int HeaderFlagsOffset = 44;
    public const int VectorDimensionOffset = 48;
    public const int CurrentCountOffset = 52;
    public const int MaxCountOffset = 60;
    public const int MaxNeighborsOffset = 68;
    public const int MaxLayersOffset = 72;
    public const int LayerProbabilityOffset = 76;
    public const int EntryPointOffset = 84;
    public const int EntryPointLevelOffset = 92;
    public const int DeletedCountOffset = 96;
    public const int DistanceFunctionOffset = 104;
    public const int MetadataHeapUsedOffset = 108;
    public const int FreeListHeadOffset = 116;
    public const int FreeListCountOffset = 124;
    public const int FormatOptionsOffset = 132;
    public const int QuantizationModeOffset = 136;
    public const int QuantizationSectionIdOffset = 140;
    public const int FileLengthOffset = 144;
    public const int MetadataHeapCapacityOffset = 152;
    public const int NextSectionDataOffsetOffset = 160;
    public const int CreatedUnixTimeSecondsOffset = 168;
    public const int UpdatedUnixTimeSecondsOffset = 176;
    public const int ReservedOffset = 184;
    public const int ReservedLength = 328;

    private const SectionFlags KnownSectionFlags =
        SectionFlags.Present |
        SectionFlags.Required |
        SectionFlags.Mutable |
        SectionFlags.AppendOnly |
        SectionFlags.MayMoveOnGrow;

    public int MagicNumber { get; set; } = MagicNumberValue;
    public int Version { get; set; } = CurrentFormatVersion;
    public int HeaderSize { get; set; } = HeaderSizeValue;
    public int PrimaryHeaderSize { get; set; } = PrimaryHeaderSizeValue;
    public int SectionTableOffset { get; set; } = SectionTableOffsetValue;
    public int SectionTableEntrySize { get; set; } = SectionTableEntrySizeValue;
    public int SectionTableEntryCount { get; set; } = SectionTableEntryCountValue;
    public uint HeaderCrc32 { get; private set; }
    public ulong Generation { get; set; }
    public uint WriteInProgress { get; set; }
    public V4HeaderFlags HeaderFlags { get; set; }
    public int VectorDimension { get; set; }

    /// <summary>
    /// Full-width persisted value for <see cref="CurrentCount"/>. Use the narrow
    /// projection in current <c>QvecDatabase</c> code because this build addresses
    /// rows with 32-bit indices.
    /// </summary>
    public long CurrentCountRaw { get; set; }

    /// <summary>
    /// Current 32-bit row-addressing projection over <see cref="CurrentCountRaw"/>.
    /// This is the intended property for <c>QvecDatabase</c> counter reads/writes.
    /// </summary>
    public int CurrentCount
    {
        get => NarrowRowIndex(CurrentCountRaw, nameof(CurrentCount));
        set => CurrentCountRaw = value;
    }

    /// <summary>
    /// Full-width persisted value for <see cref="MaxCount"/>. Use the narrow
    /// projection in current <c>QvecDatabase</c> code because this build addresses
    /// rows with 32-bit indices.
    /// </summary>
    public long MaxCountRaw { get; set; }

    /// <summary>
    /// Current 32-bit row-addressing projection over <see cref="MaxCountRaw"/>.
    /// This is the intended property for <c>QvecDatabase</c> counter reads/writes.
    /// </summary>
    public int MaxCount
    {
        get => NarrowRowIndex(MaxCountRaw, nameof(MaxCount));
        set => MaxCountRaw = value;
    }

    public int MaxNeighbors { get; set; }
    public int MaxLayers { get; set; }
    public double LayerProbability { get; set; }

    /// <summary>
    /// Full-width persisted value for <see cref="EntryPoint"/>. Use the narrow
    /// projection in current <c>QvecDatabase</c> code because this build addresses
    /// rows with 32-bit indices.
    /// </summary>
    public long EntryPointRaw { get; set; } = -1;

    /// <summary>
    /// Current 32-bit row-addressing projection over <see cref="EntryPointRaw"/>.
    /// This is the intended property for <c>QvecDatabase</c> entry-point reads/writes.
    /// </summary>
    public int EntryPoint
    {
        get => NarrowRowIndex(EntryPointRaw, nameof(EntryPoint));
        set => EntryPointRaw = value;
    }

    public int EntryPointLevel { get; set; }

    /// <summary>
    /// Full-width persisted value for <see cref="DeletedCount"/>. Use the narrow
    /// projection in current <c>QvecDatabase</c> code because this build addresses
    /// rows with 32-bit indices.
    /// </summary>
    public long DeletedCountRaw { get; set; }

    /// <summary>
    /// Current 32-bit row-addressing projection over <see cref="DeletedCountRaw"/>.
    /// This is the intended property for <c>QvecDatabase</c> counter reads/writes.
    /// </summary>
    public int DeletedCount
    {
        get => NarrowRowIndex(DeletedCountRaw, nameof(DeletedCount));
        set => DeletedCountRaw = value;
    }

    public int DistanceFunctionRaw { get; set; }

    public DistanceFunction DistanceFunction
    {
        get => DistanceFunctionRaw switch
        {
            0 => DistanceFunction.DotProduct,
            1 => DistanceFunction.Cosine,
            2 => DistanceFunction.Euclidean,
            _ => throw new QvecFormatException(
                $"DistanceFunction must be 0 (DotProduct), 1 (Cosine) or 2 (Euclidean) but was {DistanceFunctionRaw}."),
        };
        set => DistanceFunctionRaw = (int)value;
    }

    public long MetadataHeapUsed { get; set; }
    public long FreeListHead { get; set; } = -1;
    public long FreeListCount { get; set; }
    public V4FormatOptions FormatOptions { get; set; } =
        V4FormatOptions.AllowGrow |
        V4FormatOptions.RequireCleanOpen |
        V4FormatOptions.HasMetadataHeap |
        V4FormatOptions.HasPersistentFreeList |
        V4FormatOptions.ReservedQuantizationHooks;
    public int QuantizationMode { get; set; }
    public int QuantizationSectionId { get; set; }
    public long FileLength { get; set; }
    public long MetadataHeapCapacity { get; set; }
    public long NextSectionDataOffset { get; set; }
    public long CreatedUnixTimeSeconds { get; set; }
    public long UpdatedUnixTimeSeconds { get; set; }

    public SectionTableEntry[] Sections { get; } =
        InitializeSectionTable();

    public bool IsWriteInProgress => WriteInProgress != 0;

    /// <summary>
    /// A copy of every header field except the section table, which the caller is expected to lay
    /// out afresh. Used when growing a database: the mutable state (row count, entry point,
    /// tombstones, heap usage) carries over unchanged while the geometry is recomputed.
    /// </summary>
    public V4Header CloneState() => new()
    {
        MagicNumber = MagicNumber,
        Version = Version,
        HeaderSize = HeaderSize,
        PrimaryHeaderSize = PrimaryHeaderSize,
        SectionTableOffset = SectionTableOffset,
        SectionTableEntrySize = SectionTableEntrySize,
        SectionTableEntryCount = SectionTableEntryCount,
        WriteInProgress = WriteInProgress,
        Generation = Generation,
        VectorDimension = VectorDimension,
        CurrentCountRaw = CurrentCountRaw,
        MaxCountRaw = MaxCountRaw,
        MaxNeighbors = MaxNeighbors,
        MaxLayers = MaxLayers,
        LayerProbability = LayerProbability,
        EntryPointRaw = EntryPointRaw,
        EntryPointLevel = EntryPointLevel,
        DeletedCountRaw = DeletedCountRaw,
        DistanceFunction = DistanceFunction,
        MetadataHeapUsed = MetadataHeapUsed,
        FreeListHead = FreeListHead,
        FreeListCount = FreeListCount,
        HeaderFlags = HeaderFlags,
        FormatOptions = FormatOptions,
        QuantizationMode = QuantizationMode,
        QuantizationSectionId = QuantizationSectionId,
        FileLength = FileLength,
        MetadataHeapCapacity = MetadataHeapCapacity,
        NextSectionDataOffset = NextSectionDataOffset,
        CreatedUnixTimeSeconds = CreatedUnixTimeSeconds,
        UpdatedUnixTimeSeconds = UpdatedUnixTimeSeconds,
    };

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != HeaderSizeValue)
        {
            throw new ArgumentException($"The v4 header image must be exactly {HeaderSizeValue} bytes.", nameof(destination));
        }

        destination.Clear();

        WritePrimaryHeader(destination);
        WriteSectionTable(destination);

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination.Slice(HeaderCrc32Offset, sizeof(uint)),
            0);

        HeaderCrc32 = Crc32.HashToUInt32(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination.Slice(HeaderCrc32Offset, sizeof(uint)),
            HeaderCrc32);
    }

    public static V4Header Read(ReadOnlySpan<byte> source, long actualFileLength)
    {
        if (source.Length < HeaderSizeValue)
        {
            throw new QvecFormatException(
                $"The Qvec v4 header is truncated: expected {HeaderSizeValue} bytes but only {source.Length} bytes were supplied.");
        }

        if (actualFileLength < HeaderSizeValue)
        {
            throw new QvecFormatException(
                $"The Qvec file is truncated: expected at least {HeaderSizeValue} bytes for the v4 header but actual length is {actualFileLength}.");
        }

        source = source[..HeaderSizeValue];

        var header = ReadPrimaryHeader(source);
        ValidateHeaderConstants(header);
        ValidateChecksum(source, header.HeaderCrc32);
        ValidateReservedHeaderBytes(source);

        ReadSectionTable(source, header);
        ValidateFields(header, actualFileLength);
        ValidateSections(header, actualFileLength);

        return header;
    }

    /// <summary>
    /// Enforces the v4 clean-open policy after <see cref="Read"/> has parsed the
    /// header. Reading intentionally accepts dirty headers so callers can report
    /// the dirty generation, inspect diagnostics, or implement policies other
    /// than <see cref="V4FormatOptions.RequireCleanOpen"/>.
    /// </summary>
    public void EnsureCleanOpen(string path)
    {
        if (WriteInProgress != 0)
        {
            throw new QvecFormatException(
                $"'{path}' was not closed cleanly: WriteInProgress is set for generation {Generation}. The file may contain a torn write.");
        }
    }

    public bool TryGetSection(uint sectionId, out SectionExtent section)
    {
        foreach (var entry in Sections)
        {
            if (entry.SectionId == sectionId)
            {
                section = new SectionExtent(sectionId, entry.Offset, entry.Length, entry.ElementSize);
                return true;
            }
        }

        section = default;
        return false;
    }

    public SectionExtent GetRequiredSection(uint sectionId)
    {
        if (TryGetSection(sectionId, out var section))
        {
            return section;
        }

        throw new QvecFormatException($"Required section id {sectionId} is missing from the v4 section table.");
    }

    private void WritePrimaryHeader(Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(MagicNumberOffset, sizeof(int)), MagicNumber);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(VersionOffset, sizeof(int)), Version);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(HeaderSizeOffset, sizeof(int)), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(PrimaryHeaderSizeOffset, sizeof(int)), PrimaryHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(SectionTableOffsetOffset, sizeof(int)), SectionTableOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(SectionTableEntrySizeOffset, sizeof(int)), SectionTableEntrySize);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(SectionTableEntryCountOffset, sizeof(int)), SectionTableEntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(HeaderCrc32Offset, sizeof(uint)), HeaderCrc32);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(GenerationOffset, sizeof(ulong)), Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(WriteInProgressOffset, sizeof(uint)), WriteInProgress);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(HeaderFlagsOffset, sizeof(uint)), (uint)HeaderFlags);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(VectorDimensionOffset, sizeof(int)), VectorDimension);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(CurrentCountOffset, sizeof(long)), CurrentCountRaw);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(MaxCountOffset, sizeof(long)), MaxCountRaw);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(MaxNeighborsOffset, sizeof(int)), MaxNeighbors);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(MaxLayersOffset, sizeof(int)), MaxLayers);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(LayerProbabilityOffset, sizeof(long)), BitConverter.DoubleToInt64Bits(LayerProbability));
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(EntryPointOffset, sizeof(long)), EntryPointRaw);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(EntryPointLevelOffset, sizeof(int)), EntryPointLevel);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(DeletedCountOffset, sizeof(long)), DeletedCountRaw);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(DistanceFunctionOffset, sizeof(int)), DistanceFunctionRaw);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(MetadataHeapUsedOffset, sizeof(long)), MetadataHeapUsed);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(FreeListHeadOffset, sizeof(long)), FreeListHead);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(FreeListCountOffset, sizeof(long)), FreeListCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(FormatOptionsOffset, sizeof(uint)), (uint)FormatOptions);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(QuantizationModeOffset, sizeof(int)), QuantizationMode);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(QuantizationSectionIdOffset, sizeof(int)), QuantizationSectionId);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(FileLengthOffset, sizeof(long)), FileLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(MetadataHeapCapacityOffset, sizeof(long)), MetadataHeapCapacity);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(NextSectionDataOffsetOffset, sizeof(long)), NextSectionDataOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(CreatedUnixTimeSecondsOffset, sizeof(long)), CreatedUnixTimeSeconds);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(UpdatedUnixTimeSecondsOffset, sizeof(long)), UpdatedUnixTimeSeconds);
    }

    private void WriteSectionTable(Span<byte> destination)
    {
        for (var i = 0; i < Sections.Length; i++)
        {
            var entry = Sections[i];
            var slot = destination.Slice(SectionTableOffsetValue + (i * SectionTableEntrySizeValue), SectionTableEntrySizeValue);
            BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(SectionTableEntry.SectionIdOffset, sizeof(uint)), entry.SectionId);
            BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(SectionTableEntry.SectionFlagsOffset, sizeof(uint)), (uint)entry.SectionFlags);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(SectionTableEntry.OffsetOffset, sizeof(long)), entry.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(SectionTableEntry.LengthOffset, sizeof(long)), entry.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(SectionTableEntry.ElementSizeOffset, sizeof(uint)), entry.ElementSize);
            BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(SectionTableEntry.ReservedOffset, sizeof(uint)), entry.Reserved);
        }
    }

    private static V4Header ReadPrimaryHeader(ReadOnlySpan<byte> source)
    {
        return new V4Header
        {
            MagicNumber = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(MagicNumberOffset, sizeof(int))),
            Version = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(VersionOffset, sizeof(int))),
            HeaderSize = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(HeaderSizeOffset, sizeof(int))),
            PrimaryHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(PrimaryHeaderSizeOffset, sizeof(int))),
            SectionTableOffset = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(SectionTableOffsetOffset, sizeof(int))),
            SectionTableEntrySize = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(SectionTableEntrySizeOffset, sizeof(int))),
            SectionTableEntryCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(SectionTableEntryCountOffset, sizeof(int))),
            HeaderCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(HeaderCrc32Offset, sizeof(uint))),
            Generation = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(GenerationOffset, sizeof(ulong))),
            WriteInProgress = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(WriteInProgressOffset, sizeof(uint))),
            HeaderFlags = (V4HeaderFlags)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(HeaderFlagsOffset, sizeof(uint))),
            VectorDimension = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(VectorDimensionOffset, sizeof(int))),
            CurrentCountRaw = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(CurrentCountOffset, sizeof(long))),
            MaxCountRaw = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(MaxCountOffset, sizeof(long))),
            MaxNeighbors = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(MaxNeighborsOffset, sizeof(int))),
            MaxLayers = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(MaxLayersOffset, sizeof(int))),
            LayerProbability = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source.Slice(LayerProbabilityOffset, sizeof(long)))),
            EntryPointRaw = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(EntryPointOffset, sizeof(long))),
            EntryPointLevel = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(EntryPointLevelOffset, sizeof(int))),
            DeletedCountRaw = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(DeletedCountOffset, sizeof(long))),
            DistanceFunctionRaw = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(DistanceFunctionOffset, sizeof(int))),
            MetadataHeapUsed = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(MetadataHeapUsedOffset, sizeof(long))),
            FreeListHead = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(FreeListHeadOffset, sizeof(long))),
            FreeListCount = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(FreeListCountOffset, sizeof(long))),
            FormatOptions = (V4FormatOptions)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(FormatOptionsOffset, sizeof(uint))),
            QuantizationMode = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(QuantizationModeOffset, sizeof(int))),
            QuantizationSectionId = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(QuantizationSectionIdOffset, sizeof(int))),
            FileLength = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(FileLengthOffset, sizeof(long))),
            MetadataHeapCapacity = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(MetadataHeapCapacityOffset, sizeof(long))),
            NextSectionDataOffset = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(NextSectionDataOffsetOffset, sizeof(long))),
            CreatedUnixTimeSeconds = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(CreatedUnixTimeSecondsOffset, sizeof(long))),
            UpdatedUnixTimeSeconds = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(UpdatedUnixTimeSecondsOffset, sizeof(long))),
        };
    }

    private static void ReadSectionTable(ReadOnlySpan<byte> source, V4Header header)
    {
        for (var i = 0; i < SectionTableEntryCountValue; i++)
        {
            var slot = source.Slice(SectionTableOffsetValue + (i * SectionTableEntrySizeValue), SectionTableEntrySizeValue);
            header.Sections[i].SectionId = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(SectionTableEntry.SectionIdOffset, sizeof(uint)));
            header.Sections[i].SectionFlags = (SectionFlags)BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(SectionTableEntry.SectionFlagsOffset, sizeof(uint)));
            header.Sections[i].Offset = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(SectionTableEntry.OffsetOffset, sizeof(long)));
            header.Sections[i].Length = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(SectionTableEntry.LengthOffset, sizeof(long)));
            header.Sections[i].ElementSize = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(SectionTableEntry.ElementSizeOffset, sizeof(uint)));
            header.Sections[i].Reserved = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(SectionTableEntry.ReservedOffset, sizeof(uint)));
        }
    }

    private static void ValidateHeaderConstants(V4Header header)
    {
        if (header.MagicNumber != MagicNumberValue)
        {
            throw new QvecFormatException(
                $"The file is not a Qvec database: expected magic number 0x{MagicNumberValue:X8} but found 0x{header.MagicNumber:X8}.");
        }

        if (header.Version != CurrentFormatVersion)
        {
            throw new QvecFormatException(
                $"The file has Qvec format version {header.Version}. This build only supports format version {CurrentFormatVersion}.");
        }

        Require(header.HeaderSize == HeaderSizeValue, $"HeaderSize must be {HeaderSizeValue} bytes but was {header.HeaderSize}.");
        Require(header.PrimaryHeaderSize == PrimaryHeaderSizeValue, $"PrimaryHeaderSize must be {PrimaryHeaderSizeValue} bytes but was {header.PrimaryHeaderSize}.");
        Require(header.SectionTableOffset == SectionTableOffsetValue, $"SectionTableOffset must be {SectionTableOffsetValue} but was {header.SectionTableOffset}.");
        Require(header.SectionTableEntrySize == SectionTableEntrySizeValue, $"SectionTableEntrySize must be {SectionTableEntrySizeValue} but was {header.SectionTableEntrySize}.");
        Require(header.SectionTableEntryCount == SectionTableEntryCountValue, $"SectionTableEntryCount must be {SectionTableEntryCountValue} but was {header.SectionTableEntryCount}.");
    }

    private static void ValidateChecksum(ReadOnlySpan<byte> source, uint stored)
    {
        Span<byte> copy = stackalloc byte[HeaderSizeValue];
        source.CopyTo(copy);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.Slice(HeaderCrc32Offset, sizeof(uint)), 0);
        var computed = Crc32.HashToUInt32(copy);
        if (stored != computed)
        {
            throw new QvecFormatException(
                $"The file has a corrupt Qvec header: CRC-32 mismatch (stored 0x{stored:X8}, computed 0x{computed:X8}).");
        }
    }

    private static void ValidateReservedHeaderBytes(ReadOnlySpan<byte> source)
    {
        if (ContainsNonZero(source.Slice(ReservedOffset, ReservedLength)))
        {
            throw new QvecFormatException("The v4 primary header reserved bytes must be zero.");
        }

        if (ContainsNonZero(source[ReservedHeaderAreaOffset..HeaderSizeValue]))
        {
            throw new QvecFormatException("The v4 reserved header area must be zero.");
        }
    }

    private static void ValidateFields(V4Header header, long actualFileLength)
    {
        Require(header.VectorDimension > 0, $"VectorDimension must be greater than zero but was {header.VectorDimension}.");
        Require(header.MaxNeighbors >= 2, $"MaxNeighbors must be at least 2 but was {header.MaxNeighbors}.");
        Require(header.MaxLayers > 0, $"MaxLayers must be greater than zero but was {header.MaxLayers}.");
        Require(header.CurrentCountRaw >= 0, $"CurrentCount must be non-negative but was {header.CurrentCountRaw}.");
        Require(header.MaxCountRaw >= 0, $"MaxCount must be non-negative but was {header.MaxCountRaw}.");
        Require(header.CurrentCountRaw <= header.MaxCountRaw, $"CurrentCount ({header.CurrentCountRaw}) cannot exceed MaxCount ({header.MaxCountRaw}).");
        Require(header.DeletedCountRaw >= 0, $"DeletedCount must be non-negative but was {header.DeletedCountRaw}.");
        Require(header.DeletedCountRaw <= header.CurrentCountRaw, $"DeletedCount ({header.DeletedCountRaw}) cannot exceed CurrentCount ({header.CurrentCountRaw}).");
        Require(header.MetadataHeapUsed >= 0, $"MetadataHeapUsed must be non-negative but was {header.MetadataHeapUsed}.");
        Require(header.FreeListCount >= 0, $"FreeListCount must be non-negative but was {header.FreeListCount}.");
        Require(header.FreeListCount <= header.DeletedCountRaw, $"FreeListCount ({header.FreeListCount}) cannot exceed DeletedCount ({header.DeletedCountRaw}).");
        Require(header.MetadataHeapCapacity >= 0, $"MetadataHeapCapacity must be non-negative but was {header.MetadataHeapCapacity}.");
        Require(header.FileLength >= HeaderSizeValue, $"FileLength must be at least {HeaderSizeValue} but was {header.FileLength}.");
        Require(header.FileLength <= actualFileLength, $"FileLength ({header.FileLength}) cannot exceed the actual file length ({actualFileLength}).");
        Require(header.NextSectionDataOffset >= HeaderSizeValue, $"NextSectionDataOffset must be at least {HeaderSizeValue} but was {header.NextSectionDataOffset}.");
        Require(header.NextSectionDataOffset <= header.FileLength, $"NextSectionDataOffset ({header.NextSectionDataOffset}) cannot exceed FileLength ({header.FileLength}).");
        Require(header.WriteInProgress is 0 or 1, $"WriteInProgress must be 0 or 1 but was {header.WriteInProgress}.");
        Require(header.EntryPointRaw == -1 || (header.EntryPointRaw >= 0 && header.EntryPointRaw < header.CurrentCountRaw),
            $"EntryPoint must be -1 or a row index below CurrentCount ({header.CurrentCountRaw}) but was {header.EntryPointRaw}.");
        Require(header.EntryPointLevel >= 0 && header.EntryPointLevel < header.MaxLayers,
            $"EntryPointLevel must be in the range 0..{header.MaxLayers - 1} but was {header.EntryPointLevel}.");
        Require(header.DistanceFunctionRaw is 0 or 1 or 2, $"DistanceFunction must be 0 (DotProduct), 1 (Cosine) or 2 (Euclidean) but was {header.DistanceFunctionRaw}.");
        Require(header.QuantizationMode is 0 or 2,
            header.QuantizationMode == 1
                ? "QuantizationMode 1 (per-dataset int8) is reserved by the format but not implemented in this build."
                : $"QuantizationMode is {header.QuantizationMode}; this build supports 0 (none) and 2 (per-vector int8).");
        if (header.QuantizationMode == 0)
        {
            Require(header.QuantizationSectionId == 0,
                $"QuantizationSectionId must be 0 when quantization is disabled but was {header.QuantizationSectionId}.");
        }
        else
        {
            Require(header.QuantizationSectionId == V4SectionIds.QuantizedVectors,
                $"QuantizationSectionId must be {V4SectionIds.QuantizedVectors} for int8 quantization but was {header.QuantizationSectionId}.");
        }
    }

    private static void ValidateSections(V4Header header, long actualFileLength)
    {
        var seenIds = new HashSet<uint>();
        var extents = new List<SectionExtent>();

        foreach (var entry in header.Sections)
        {
            if (entry.SectionId == V4SectionIds.Unused)
            {
                Require(entry.SectionFlags == SectionFlags.None &&
                    entry.Offset == 0 &&
                    entry.Length == 0 &&
                    entry.ElementSize == 0 &&
                    entry.Reserved == 0,
                    "Empty section-table slots must have all fields set to zero.");
                continue;
            }

            Require((entry.SectionFlags & SectionFlags.Present) != 0,
                $"Section id {entry.SectionId} must set the Present flag.");
            Require((entry.SectionFlags & ~KnownSectionFlags) == 0,
                $"Section id {entry.SectionId} uses reserved SectionFlags bits: 0x{((uint)(entry.SectionFlags & ~KnownSectionFlags)):X8}.");
            Require(entry.Reserved == 0, $"Section id {entry.SectionId} has non-zero reserved bytes.");
            Require(entry.Offset >= header.HeaderSize, $"Section id {entry.SectionId} offset ({entry.Offset}) must be at least HeaderSize ({header.HeaderSize}).");
            Require(entry.Length >= 0, $"Section id {entry.SectionId} length must be non-negative but was {entry.Length}.");
            Require(entry.ElementSize > 0, $"Section id {entry.SectionId} ElementSize must be greater than zero.");

            var end = CheckedEnd(entry);
            Require(end <= header.FileLength, $"Section id {entry.SectionId} ends at {end}, beyond FileLength {header.FileLength}.");
            Require(end <= actualFileLength, $"Section id {entry.SectionId} ends at {end}, beyond actual file length {actualFileLength}.");

            Require(seenIds.Add(entry.SectionId), $"Duplicate section id {entry.SectionId} is not allowed.");

            if (!IsKnownRequiredSection(header, entry.SectionId) && (entry.SectionFlags & SectionFlags.Required) != 0)
            {
                throw new QvecFormatException($"Required section id {entry.SectionId} is not supported by the v4 reader.");
            }

            extents.Add(new SectionExtent(entry.SectionId, entry.Offset, entry.Length, entry.ElementSize));
        }

        ValidateRequiredSections(header);
        ValidateKnownSectionShapes(header);
        ValidateNoOverlaps(extents);
    }

    private static void ValidateRequiredSections(V4Header header)
    {
        foreach (var sectionId in RequiredSectionIds(header))
        {
            var found = false;
            foreach (var entry in header.Sections)
            {
                if (entry.SectionId == sectionId)
                {
                    found = true;
                    Require((entry.SectionFlags & SectionFlags.Required) != 0,
                        $"Known v4 section id {sectionId} must set the Required flag.");
                    break;
                }
            }

            Require(found, $"Required v4 section id {sectionId} is missing.");
        }
    }

    /// <summary>
    /// The sections every valid file must carry. Vector storage depends on the quantization
    /// mode: float files carry <see cref="V4SectionIds.Vectors"/>, int8 files carry the code and
    /// parameter sections instead.
    /// </summary>
    private static IEnumerable<uint> RequiredSectionIds(V4Header header)
    {
        if (header.QuantizationMode == 0)
        {
            yield return V4SectionIds.Vectors;
        }
        else
        {
            yield return V4SectionIds.QuantizedVectors;
            yield return V4SectionIds.QuantizationVectorParameters;
        }

        for (uint sectionId = V4SectionIds.Graph; sectionId <= V4SectionIds.FreeList; sectionId++)
        {
            yield return sectionId;
        }
    }

    private static void ValidateKnownSectionShapes(V4Header header)
    {
        var graph = header.GetRequiredSection(V4SectionIds.Graph);
        var descriptors = header.GetRequiredSection(V4SectionIds.MetadataDescriptors);
        var heap = header.GetRequiredSection(V4SectionIds.MetadataHeap);
        var guids = header.GetRequiredSection(V4SectionIds.Guids);
        var tombstones = header.GetRequiredSection(V4SectionIds.Tombstones);
        var freeList = header.GetRequiredSection(V4SectionIds.FreeList);

        if (header.QuantizationMode == 0)
        {
            var vectors = header.GetRequiredSection(V4SectionIds.Vectors);
            var vectorElementSize = checked((uint)(header.VectorDimension * sizeof(float)));
            Require(vectors.ElementSize == vectorElementSize,
                $"Vectors ElementSize must be VectorDimension * 4 ({vectorElementSize}) but was {vectors.ElementSize}.");
            Require(vectors.Length >= CheckedLength(header.MaxCountRaw, vectorElementSize, V4SectionIds.Vectors),
                "Vectors length is too small for MaxCount and VectorDimension.");
        }
        else
        {
            var codes = header.GetRequiredSection(V4SectionIds.QuantizedVectors);
            var codeElementSize = checked((uint)header.VectorDimension);
            Require(codes.ElementSize == codeElementSize,
                $"QuantizedVectors ElementSize must be VectorDimension ({codeElementSize}) but was {codes.ElementSize}.");
            Require(codes.Length >= CheckedLength(header.MaxCountRaw, codeElementSize, V4SectionIds.QuantizedVectors),
                "QuantizedVectors length is too small for MaxCount and VectorDimension.");

            var parameters = header.GetRequiredSection(V4SectionIds.QuantizationVectorParameters);
            Require(parameters.ElementSize == 16,
                $"QuantizationVectorParameters ElementSize must be 16 but was {parameters.ElementSize}.");
            Require(parameters.Length >= CheckedLength(header.MaxCountRaw, 16, V4SectionIds.QuantizationVectorParameters),
                "QuantizationVectorParameters length is too small for MaxCount.");
        }

        // Layer 0 is allocated 2 * MaxNeighbors slots (M0 = 2 * M, per Malkov & Yashunin) and
        // every layer above it MaxNeighbors, which sums to (MaxLayers + 1) * MaxNeighbors.
        var graphElementSize = checked((uint)((header.MaxLayers + 1) * header.MaxNeighbors * sizeof(int)));
        Require(graph.ElementSize == graphElementSize,
            $"Graph ElementSize must be (MaxLayers + 1) * MaxNeighbors * 4 ({graphElementSize}) but was {graph.ElementSize}.");
        Require(graph.Length >= CheckedLength(header.MaxCountRaw, graphElementSize, V4SectionIds.Graph),
            "Graph length is too small for MaxCount, MaxLayers, and MaxNeighbors.");

        Require(descriptors.ElementSize == 16, $"MetadataDescriptors ElementSize must be 16 but was {descriptors.ElementSize}.");
        Require(descriptors.Length >= CheckedLength(header.MaxCountRaw, 16, V4SectionIds.MetadataDescriptors),
            "MetadataDescriptors length is too small for MaxCount.");

        Require(heap.ElementSize == 1, $"MetadataHeap ElementSize must be 1 but was {heap.ElementSize}.");
        Require(header.MetadataHeapUsed <= heap.Length,
            $"MetadataHeapUsed ({header.MetadataHeapUsed}) cannot exceed MetadataHeap length ({heap.Length}).");
        Require(header.MetadataHeapCapacity == heap.Length,
            $"MetadataHeapCapacity ({header.MetadataHeapCapacity}) must match MetadataHeap length ({heap.Length}).");

        Require(guids.ElementSize == 16, $"Guids ElementSize must be 16 but was {guids.ElementSize}.");
        Require(guids.Length >= CheckedLength(header.MaxCountRaw, 16, V4SectionIds.Guids),
            "Guids length is too small for MaxCount.");

        Require(tombstones.ElementSize == 1, $"Tombstones ElementSize must be 1 but was {tombstones.ElementSize}.");
        Require(tombstones.Length >= header.MaxCountRaw, "Tombstones length is too small for MaxCount.");

        Require(freeList.ElementSize == 8, $"FreeList ElementSize must be 8 but was {freeList.ElementSize}.");
        Require(freeList.Length >= CheckedLength(header.MaxCountRaw, 8, V4SectionIds.FreeList),
            "FreeList length is too small for MaxCount.");
    }

    private static void ValidateNoOverlaps(List<SectionExtent> extents)
    {
        extents.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));

        for (var i = 1; i < extents.Count; i++)
        {
            var previous = extents[i - 1];
            var current = extents[i];
            var previousEnd = checked(previous.Offset + previous.Length);
            if (previousEnd > current.Offset)
            {
                throw new QvecFormatException(
                    $"Section id {previous.SectionId} overlaps section id {current.SectionId}.");
            }
        }
    }

    private static long CheckedEnd(SectionTableEntry entry)
    {
        try
        {
            return checked(entry.Offset + entry.Length);
        }
        catch (OverflowException ex)
        {
            throw new QvecFormatException($"Section id {entry.SectionId} offset plus length overflows Int64: {ex.Message}");
        }
    }

    private static long CheckedLength(long count, uint elementSize, uint sectionId)
    {
        try
        {
            return checked(count * (long)elementSize);
        }
        catch (OverflowException ex)
        {
            throw new QvecFormatException($"Section id {sectionId} required length overflows Int64: {ex.Message}");
        }
    }

    private static bool IsKnownRequiredSection(V4Header header, uint sectionId)
    {
        if (sectionId is >= V4SectionIds.Graph and <= V4SectionIds.FreeList) return true;
        return header.QuantizationMode == 0
            ? sectionId == V4SectionIds.Vectors
            : sectionId is V4SectionIds.QuantizedVectors or V4SectionIds.QuantizationVectorParameters;
    }

    private static bool ContainsNonZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static SectionTableEntry[] InitializeSectionTable()
    {
        var entries = new SectionTableEntry[SectionTableEntryCountValue];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new SectionTableEntry();
        }

        return entries;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new QvecFormatException(message);
        }
    }

    private static int NarrowRowIndex(long value, string propertyName)
    {
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new QvecFormatException(
                $"{propertyName} is {value}, which cannot be represented by this build because it addresses rows with 32-bit indices.");
        }

        return (int)value;
    }
}
