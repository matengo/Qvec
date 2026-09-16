using System.Buffers.Binary;
using System.IO.Hashing;
using Qvec.Core;
using Qvec.Core.Format;

namespace Qvec.Core.Tests.Format;

public class V4HeaderTests
{
    public static TheoryData<string, Action<V4Header>> HeaderConstantViolations => new()
    {
        { "HeaderSize", h => h.HeaderSize = 2048 },
        { "PrimaryHeaderSize", h => h.PrimaryHeaderSize = 256 },
        { "SectionTableOffset", h => h.SectionTableOffset = 1024 },
        { "SectionTableEntrySize", h => h.SectionTableEntrySize = 16 },
        { "SectionTableEntryCount", h => h.SectionTableEntryCount = 32 },
    };

    public static TheoryData<string, Action<V4Header>> HeaderFieldViolations => new()
    {
        { "VectorDimension", h => h.VectorDimension = 0 },
        { "MaxNeighbors", h => h.MaxNeighbors = 1 },
        { "MaxLayers", h => h.MaxLayers = 0 },
        { "CurrentCount", h => h.CurrentCount = -1 },
        { "MaxCount", h => h.MaxCount = -1 },
        { "CurrentCount", h => h.CurrentCount = h.MaxCount + 1 },
        { "DeletedCount", h => h.DeletedCount = -1 },
        { "DeletedCount", h => h.DeletedCount = h.CurrentCount + 1 },
        { "MetadataHeapUsed", h => h.MetadataHeapUsed = -1 },
        { "FreeListCount", h => h.FreeListCount = -1 },
        { "FreeListCount", h => h.FreeListCount = h.DeletedCount + 1 },
        { "MetadataHeapCapacity", h => h.MetadataHeapCapacity = -1 },
        { "FileLength", h => h.FileLength = V4Header.HeaderSizeValue - 1 },
        { "NextSectionDataOffset", h => h.NextSectionDataOffset = V4Header.HeaderSizeValue - 1 },
        { "NextSectionDataOffset", h => h.NextSectionDataOffset = h.FileLength + 1 },
        { "WriteInProgress", h => h.WriteInProgress = 2 },
        { "EntryPoint", h => h.EntryPoint = -2 },
        { "EntryPoint", h => h.EntryPoint = h.CurrentCount },
        { "EntryPointLevel", h => h.EntryPointLevel = -1 },
        { "EntryPointLevel", h => h.EntryPointLevel = h.MaxLayers },
        { "DistanceFunction", h => h.DistanceFunctionRaw = 3 },
        { "QuantizationMode", h => h.QuantizationMode = 1 },
        { "QuantizationSectionId", h => h.QuantizationSectionId = 8 },
    };

    public static TheoryData<string, Action<V4Header>> KnownSectionShapeViolations => new()
    {
        { "Vectors ElementSize", h => h.GetEntry(V4SectionIds.Vectors).ElementSize = 1 },
        { "Vectors length", h => h.GetEntry(V4SectionIds.Vectors).Length = 1 },
        { "Graph ElementSize", h => h.GetEntry(V4SectionIds.Graph).ElementSize = 1 },
        { "Graph length", h => h.GetEntry(V4SectionIds.Graph).Length = 1 },
        { "MetadataDescriptors ElementSize", h => h.GetEntry(V4SectionIds.MetadataDescriptors).ElementSize = 1 },
        { "MetadataDescriptors length", h => h.GetEntry(V4SectionIds.MetadataDescriptors).Length = 1 },
        { "MetadataHeap ElementSize", h => h.GetEntry(V4SectionIds.MetadataHeap).ElementSize = 2 },
        { "MetadataHeapUsed", h => h.MetadataHeapUsed = h.GetEntry(V4SectionIds.MetadataHeap).Length + 1 },
        { "MetadataHeapCapacity", h => h.MetadataHeapCapacity = h.GetEntry(V4SectionIds.MetadataHeap).Length + 1 },
        { "Guids ElementSize", h => h.GetEntry(V4SectionIds.Guids).ElementSize = 1 },
        { "Guids length", h => h.GetEntry(V4SectionIds.Guids).Length = 1 },
        { "Tombstones ElementSize", h => h.GetEntry(V4SectionIds.Tombstones).ElementSize = 2 },
        { "Tombstones length", h => h.GetEntry(V4SectionIds.Tombstones).Length = 1 },
        { "FreeList ElementSize", h => h.GetEntry(V4SectionIds.FreeList).ElementSize = 1 },
        { "FreeList length", h => h.GetEntry(V4SectionIds.FreeList).Length = 1 },
    };

    [Fact]
    public void PrimaryHeaderLayoutConstants_MatchDesignOffsets()
    {
        Assert.Equal(0, V4Header.MagicNumberOffset);
        Assert.Equal(4, V4Header.VersionOffset);
        Assert.Equal(8, V4Header.HeaderSizeOffset);
        Assert.Equal(12, V4Header.PrimaryHeaderSizeOffset);
        Assert.Equal(16, V4Header.SectionTableOffsetOffset);
        Assert.Equal(20, V4Header.SectionTableEntrySizeOffset);
        Assert.Equal(24, V4Header.SectionTableEntryCountOffset);
        Assert.Equal(28, V4Header.HeaderCrc32Offset);
        Assert.Equal(32, V4Header.GenerationOffset);
        Assert.Equal(40, V4Header.WriteInProgressOffset);
        Assert.Equal(44, V4Header.HeaderFlagsOffset);
        Assert.Equal(48, V4Header.VectorDimensionOffset);
        Assert.Equal(52, V4Header.CurrentCountOffset);
        Assert.Equal(60, V4Header.MaxCountOffset);
        Assert.Equal(68, V4Header.MaxNeighborsOffset);
        Assert.Equal(72, V4Header.MaxLayersOffset);
        Assert.Equal(76, V4Header.LayerProbabilityOffset);
        Assert.Equal(84, V4Header.EntryPointOffset);
        Assert.Equal(92, V4Header.EntryPointLevelOffset);
        Assert.Equal(96, V4Header.DeletedCountOffset);
        Assert.Equal(104, V4Header.DistanceFunctionOffset);
        Assert.Equal(108, V4Header.MetadataHeapUsedOffset);
        Assert.Equal(116, V4Header.FreeListHeadOffset);
        Assert.Equal(124, V4Header.FreeListCountOffset);
        Assert.Equal(132, V4Header.FormatOptionsOffset);
        Assert.Equal(136, V4Header.QuantizationModeOffset);
        Assert.Equal(140, V4Header.QuantizationSectionIdOffset);
        Assert.Equal(144, V4Header.FileLengthOffset);
        Assert.Equal(152, V4Header.MetadataHeapCapacityOffset);
        Assert.Equal(160, V4Header.NextSectionDataOffsetOffset);
        Assert.Equal(168, V4Header.CreatedUnixTimeSecondsOffset);
        Assert.Equal(176, V4Header.UpdatedUnixTimeSecondsOffset);
        Assert.Equal(184, V4Header.ReservedOffset);
    }

    [Fact]
    public void SectionEntryLayoutConstants_MatchDesignOffsets()
    {
        Assert.Equal(0, SectionTableEntry.SectionIdOffset);
        Assert.Equal(4, SectionTableEntry.SectionFlagsOffset);
        Assert.Equal(8, SectionTableEntry.OffsetOffset);
        Assert.Equal(16, SectionTableEntry.LengthOffset);
        Assert.Equal(24, SectionTableEntry.ElementSizeOffset);
        Assert.Equal(28, SectionTableEntry.ReservedOffset);
        Assert.Equal(32, SectionTableEntry.Size);
    }

    [Fact]
    public void WriteRead_RoundTripsEveryHeaderFieldAndSection()
    {
        var header = CreateValidHeader();
        header.Generation = 123;
        header.WriteInProgress = 1;
        header.HeaderFlags = V4HeaderFlags.SparseRequested | V4HeaderFlags.SparseConfirmed | V4HeaderFlags.HasOptionalSections;
        header.CurrentCount = 3;
        header.DeletedCount = 1;
        header.DistanceFunction = DistanceFunction.Cosine;
        header.MetadataHeapUsed = 321;
        header.FreeListHead = 2;
        header.FreeListCount = 1;
        header.CreatedUnixTimeSeconds = 1_700_000_001;
        header.UpdatedUnixTimeSeconds = 1_700_000_002;

        var buffer = Write(header);
        var read = V4Header.Read(buffer, header.FileLength);

        Assert.Equal(header.MagicNumber, read.MagicNumber);
        Assert.Equal(header.Version, read.Version);
        Assert.Equal(header.HeaderSize, read.HeaderSize);
        Assert.Equal(header.PrimaryHeaderSize, read.PrimaryHeaderSize);
        Assert.Equal(header.SectionTableOffset, read.SectionTableOffset);
        Assert.Equal(header.SectionTableEntrySize, read.SectionTableEntrySize);
        Assert.Equal(header.SectionTableEntryCount, read.SectionTableEntryCount);
        Assert.Equal(header.HeaderCrc32, read.HeaderCrc32);
        Assert.Equal(header.Generation, read.Generation);
        Assert.Equal(header.WriteInProgress, read.WriteInProgress);
        Assert.Equal(header.HeaderFlags, read.HeaderFlags);
        Assert.Equal(header.VectorDimension, read.VectorDimension);
        Assert.Equal(header.CurrentCountRaw, read.CurrentCountRaw);
        Assert.Equal(header.CurrentCount, read.CurrentCount);
        Assert.Equal(header.MaxCountRaw, read.MaxCountRaw);
        Assert.Equal(header.MaxCount, read.MaxCount);
        Assert.Equal(header.MaxNeighbors, read.MaxNeighbors);
        Assert.Equal(header.MaxLayers, read.MaxLayers);
        Assert.Equal(header.LayerProbability, read.LayerProbability);
        Assert.Equal(header.EntryPointRaw, read.EntryPointRaw);
        Assert.Equal(header.EntryPoint, read.EntryPoint);
        Assert.Equal(header.EntryPointLevel, read.EntryPointLevel);
        Assert.Equal(header.DeletedCountRaw, read.DeletedCountRaw);
        Assert.Equal(header.DeletedCount, read.DeletedCount);
        Assert.Equal(header.DistanceFunctionRaw, read.DistanceFunctionRaw);
        Assert.Equal(header.DistanceFunction, read.DistanceFunction);
        Assert.Equal(header.MetadataHeapUsed, read.MetadataHeapUsed);
        Assert.Equal(header.FreeListHead, read.FreeListHead);
        Assert.Equal(header.FreeListCount, read.FreeListCount);
        Assert.Equal(header.FormatOptions, read.FormatOptions);
        Assert.Equal(header.QuantizationMode, read.QuantizationMode);
        Assert.Equal(header.QuantizationSectionId, read.QuantizationSectionId);
        Assert.Equal(header.FileLength, read.FileLength);
        Assert.Equal(header.MetadataHeapCapacity, read.MetadataHeapCapacity);
        Assert.Equal(header.NextSectionDataOffset, read.NextSectionDataOffset);
        Assert.Equal(header.CreatedUnixTimeSeconds, read.CreatedUnixTimeSeconds);
        Assert.Equal(header.UpdatedUnixTimeSeconds, read.UpdatedUnixTimeSeconds);

        for (var i = 0; i < V4Header.SectionTableEntryCountValue; i++)
        {
            Assert.Equal(header.Sections[i].SectionId, read.Sections[i].SectionId);
            Assert.Equal(header.Sections[i].SectionFlags, read.Sections[i].SectionFlags);
            Assert.Equal(header.Sections[i].Offset, read.Sections[i].Offset);
            Assert.Equal(header.Sections[i].Length, read.Sections[i].Length);
            Assert.Equal(header.Sections[i].ElementSize, read.Sections[i].ElementSize);
            Assert.Equal(header.Sections[i].Reserved, read.Sections[i].Reserved);
        }
    }

    [Fact]
    public void WriteTo_ProducesExactly4096BytesAndWritesMagicNumber()
    {
        var buffer = Write(CreateValidHeader());

        Assert.Equal(V4Header.HeaderSizeValue, buffer.Length);
        Assert.Equal(V4Header.MagicNumberValue, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, 4)));
        Assert.Equal(0x43, buffer[0]);
        Assert.Equal(0x45, buffer[1]);
        Assert.Equal(0x56, buffer[2]);
        Assert.Equal(0x5A, buffer[3]);
    }

    [Fact]
    public void Read_WithBadMagic_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.MagicNumber = 0;

        AssertThrows(header, "magic number");
    }

    [Fact]
    public void Read_WithWrongVersion_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.Version = 3;

        AssertThrows(header, "format version");
    }

    [Theory]
    [MemberData(nameof(HeaderConstantViolations))]
    public void Read_WithWrongHeaderConstant_ThrowsFormatException(string expectedMessagePart, Action<V4Header> corrupt)
    {
        var header = CreateValidHeader();
        corrupt(header);

        AssertThrows(header, expectedMessagePart);
    }

    [Fact]
    public void Read_WithCrcMismatchFromReservedAreaFlip_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        var buffer = Write(header);
        buffer[V4Header.ReservedOffset] = 1;

        Assert.Throws<QvecFormatException>(() => V4Header.Read(buffer, header.FileLength));
    }

    [Fact]
    public void Read_WithCrcMismatchFromSectionTableFlip_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        var buffer = Write(header);
        buffer[V4Header.SectionTableOffsetValue + SectionTableEntry.OffsetOffset] ^= 0x40;

        Assert.Throws<QvecFormatException>(() => V4Header.Read(buffer, header.FileLength));
    }

    [Fact]
    public void Read_WithNonZeroPrimaryReservedBytes_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        var buffer = Write(header);
        buffer[V4Header.ReservedOffset] = 1;
        RewriteCrc(buffer);

        Assert.Throws<QvecFormatException>(() => V4Header.Read(buffer, header.FileLength));
    }

    [Fact]
    public void Read_WithNonZeroReservedHeaderArea_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        var buffer = Write(header);
        buffer[V4Header.ReservedHeaderAreaOffset] = 1;
        RewriteCrc(buffer);

        Assert.Throws<QvecFormatException>(() => V4Header.Read(buffer, header.FileLength));
    }

    [Theory]
    [MemberData(nameof(HeaderFieldViolations))]
    public void Read_WithInvalidHeaderFieldRange_ThrowsFormatException(string expectedMessagePart, Action<V4Header> corrupt)
    {
        var header = CreateValidHeader();
        corrupt(header);

        AssertThrows(header, expectedMessagePart);
    }

    [Fact]
    public void Read_WithFileLengthGreaterThanActualLength_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        var buffer = Write(header);

        Assert.Throws<QvecFormatException>(() => V4Header.Read(buffer, header.FileLength - 1));
    }

    [Fact]
    public void Read_WithTruncatedHeaderBytes_ThrowsFormatException()
    {
        var buffer = new byte[V4Header.HeaderSizeValue - 1];

        Assert.Throws<QvecFormatException>(() => V4Header.Read(buffer, V4Header.HeaderSizeValue));
    }

    [Fact]
    public void Read_WithActualLengthSmallerThanHeader_ThrowsFormatException()
    {
        var buffer = Write(CreateValidHeader());

        Assert.Throws<QvecFormatException>(() => V4Header.Read(buffer, V4Header.HeaderSizeValue - 1));
    }

    [Fact]
    public void Read_WithNonZeroEmptySlotField_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.Sections[10].ElementSize = 1;

        AssertThrows(header, "Empty section-table slots");
    }

    [Fact]
    public void Read_WithSectionMissingPresentFlag_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.GetEntry(V4SectionIds.Vectors).SectionFlags &= ~SectionFlags.Present;

        AssertThrows(header, "Present");
    }

    [Fact]
    public void Read_WithSectionReservedFlags_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.GetEntry(V4SectionIds.Vectors).SectionFlags |= (SectionFlags)(1u << 31);

        AssertThrows(header, "reserved SectionFlags");
    }

    [Fact]
    public void Read_WithSectionReservedField_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.GetEntry(V4SectionIds.Vectors).Reserved = 1;

        AssertThrows(header, "reserved");
    }

    [Fact]
    public void Read_WithSectionOffsetBeforeHeader_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.GetEntry(V4SectionIds.Vectors).Offset = V4Header.HeaderSizeValue - 1;

        AssertThrows(header, "offset");
    }

    [Fact]
    public void Read_WithNegativeSectionLength_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.GetEntry(V4SectionIds.Vectors).Length = -1;

        AssertThrows(header, "length");
    }

    [Fact]
    public void Read_WithZeroPresentSectionElementSize_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.GetEntry(V4SectionIds.Vectors).ElementSize = 0;

        AssertThrows(header, "ElementSize");
    }

    [Fact]
    public void Read_WithSectionPastFileLength_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.GetEntry(V4SectionIds.MetadataHeap).Length += 1;

        AssertThrows(header, "FileLength");
    }

    [Fact]
    public void Read_WithOverlappingSections_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        var vectors = header.GetEntry(V4SectionIds.Vectors);
        header.GetEntry(V4SectionIds.Graph).Offset = vectors.Offset + 4;

        AssertThrows(header, "overlaps");
    }

    [Fact]
    public void Read_WithDuplicateSectionIds_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        var copy = header.GetEntry(V4SectionIds.Vectors).Clone();
        copy.Offset = Align(header.FileLength);
        header.Sections[7] = copy;
        header.FileLength = copy.Offset + copy.Length;
        header.NextSectionDataOffset = header.FileLength;

        AssertThrows(header, "Duplicate");
    }

    [Fact]
    public void Read_WithMissingRequiredSectionId_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.Sections[0] = new SectionTableEntry();

        AssertThrows(header, "missing");
    }

    [Fact]
    public void Read_WithKnownSectionNotRequired_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        header.GetEntry(V4SectionIds.Vectors).SectionFlags &= ~SectionFlags.Required;

        AssertThrows(header, "Required");
    }

    [Fact]
    public void Read_WithUnknownRequiredSectionId_ThrowsFormatException()
    {
        var header = CreateValidHeader();
        AddOptionalSection(header, sectionId: 1024, SectionFlags.Required);

        AssertThrows(header, "Required section id 1024");
    }

    [Fact]
    public void Read_WithUnknownNonRequiredSectionId_AcceptsSection()
    {
        var header = CreateValidHeader();
        AddOptionalSection(header, sectionId: 1024, SectionFlags.None);
        var buffer = Write(header);

        var read = V4Header.Read(buffer, header.FileLength);

        Assert.True(read.TryGetSection(1024, out var section));
        Assert.Equal(1024u, section.SectionId);
    }

    [Theory]
    [MemberData(nameof(KnownSectionShapeViolations))]
    public void Read_WithInvalidKnownSectionShape_ThrowsFormatException(string expectedMessagePart, Action<V4Header> corrupt)
    {
        var header = CreateValidHeader();
        corrupt(header);

        AssertThrows(header, expectedMessagePart);
    }

    [Fact]
    public void LayoutCalculator_CreatesOrderedAlignedNonOverlappingSections()
    {
        var header = QvecFormatLayout.CreateInitial(
            vectorDimension: 7,
            maxCount: 11,
            maxNeighbors: 5,
            maxLayers: 3,
            metadataHeapCapacity: 12345);

        var sections = header.Sections
            .Where(s => s.SectionId != V4SectionIds.Unused)
            .OrderBy(s => s.Offset)
            .ToArray();

        Assert.Equal(7, sections.Length);
        Assert.Equal(header.FileLength, header.NextSectionDataOffset);

        long previousEnd = V4Header.HeaderSizeValue;
        foreach (var section in sections)
        {
            Assert.True(section.Offset >= V4Header.HeaderSizeValue);
            Assert.Equal(0, section.Offset % 64);
            Assert.True(previousEnd <= section.Offset);
            previousEnd = section.Offset + section.Length;
        }

        Assert.Equal(previousEnd, header.FileLength);
        for (uint id = V4SectionIds.Vectors; id <= V4SectionIds.FreeList; id++)
        {
            Assert.True(header.TryGetSection(id, out var section));
            Assert.Equal(id, section.SectionId);
            Assert.True((header.GetEntry(id).SectionFlags & SectionFlags.Required) != 0);
        }
    }

    [Fact]
    public void LayoutCalculator_WithInt8Quantization_StoresCodesAndParametersInsteadOfFloats()
    {
        var header = QvecFormatLayout.CreateInitial(
            vectorDimension: 7,
            maxCount: 11,
            maxNeighbors: 5,
            maxLayers: 3,
            metadataHeapCapacity: 12345,
            quantization: VectorQuantization.Int8);

        Assert.Equal(2, header.QuantizationMode);
        Assert.Equal((int)V4SectionIds.QuantizedVectors, header.QuantizationSectionId);
        Assert.False(header.TryGetSection(V4SectionIds.Vectors, out _));

        var codes = header.GetRequiredSection(V4SectionIds.QuantizedVectors);
        Assert.Equal(7u, codes.ElementSize);
        Assert.True(codes.Length >= 11 * 7);

        var parameters = header.GetRequiredSection(V4SectionIds.QuantizationVectorParameters);
        Assert.Equal(16u, parameters.ElementSize);
        Assert.True(parameters.Length >= 11 * 16);

        // Same shape rules as the float layout: ordered, 64-byte aligned, non-overlapping.
        var sections = header.Sections
            .Where(s => s.SectionId != V4SectionIds.Unused)
            .OrderBy(s => s.Offset)
            .ToArray();
        Assert.Equal(8, sections.Length);
        long previousEnd = V4Header.HeaderSizeValue;
        foreach (var section in sections)
        {
            Assert.Equal(0, section.Offset % 64);
            Assert.True(previousEnd <= section.Offset);
            previousEnd = section.Offset + section.Length;
        }
        Assert.Equal(previousEnd, header.FileLength);

        // And it must survive a write/read cycle through the validator.
        var read = V4Header.Read(Write(header), header.FileLength);
        Assert.Equal(2, read.QuantizationMode);
        Assert.True(read.TryGetSection(V4SectionIds.QuantizedVectors, out _));
    }

    [Fact]
    public void Read_QuantizedHeader_WithModeZero_IsRejectedBecauseFloatVectorsAreMissing()
    {
        var header = QvecFormatLayout.CreateInitial(4, 8, 3, 2, 4096, quantization: VectorQuantization.Int8);
        header.QuantizationMode = 0;
        header.QuantizationSectionId = 0;

        // Mode 0 makes section 8 an unknown Required section and section 1 a missing one;
        // whichever check fires first, the file must not open as a float database.
        AssertThrows(header, "section id");
    }

    [Fact]
    public void Read_QuantizedHeader_WithPerDatasetMode_IsRejectedAsReserved()
    {
        var header = QvecFormatLayout.CreateInitial(4, 8, 3, 2, 4096, quantization: VectorQuantization.Int8);
        header.QuantizationMode = 1;

        AssertThrows(header, "QuantizationMode");
    }

    [Theory]
    [InlineData("QuantizedVectors ElementSize", V4SectionIds.QuantizedVectors, 1u, -1L)]
    [InlineData("QuantizedVectors length", V4SectionIds.QuantizedVectors, 4u, 1L)]
    [InlineData("QuantizationVectorParameters ElementSize", V4SectionIds.QuantizationVectorParameters, 8u, -1L)]
    [InlineData("QuantizationVectorParameters length", V4SectionIds.QuantizationVectorParameters, 16u, 1L)]
    public void Read_QuantizedHeader_WithInvalidSectionShape_ThrowsFormatException(
        string expectedMessagePart, uint sectionId, uint elementSize, long length)
    {
        var header = QvecFormatLayout.CreateInitial(4, 8, 3, 2, 4096, quantization: VectorQuantization.Int8);
        var entry = header.GetEntry(sectionId);
        entry.ElementSize = elementSize;
        if (length >= 0) entry.Length = length;

        AssertThrows(header, expectedMessagePart);
    }

    [Fact]
    public void DirtyHeader_IsReadableAndReportsWriteInProgress()
    {
        var header = CreateValidHeader();
        header.WriteInProgress = 1;
        header.Generation = 99;
        var buffer = Write(header);

        var read = V4Header.Read(buffer, header.FileLength);

        Assert.True(read.IsWriteInProgress);
        Assert.Equal(1u, read.WriteInProgress);
        var ex = Assert.Throws<QvecFormatException>(() => read.EnsureCleanOpen("dirty.zvec"));
        Assert.Contains("was not closed cleanly", ex.Message);
        Assert.Contains("generation 99", ex.Message);
    }

    [Fact]
    public void NarrowRowIndexProjection_WhenRawValueExceedsInt32Range_ThrowsFormatException()
    {
        const string expected = "32-bit indices";

        var current = CreateValidHeader();
        current.CurrentCountRaw = (long)int.MaxValue + 1;
        Assert.Contains(expected, Assert.Throws<QvecFormatException>(() => current.CurrentCount).Message);

        var max = CreateValidHeader();
        max.MaxCountRaw = (long)int.MaxValue + 1;
        Assert.Contains(expected, Assert.Throws<QvecFormatException>(() => max.MaxCount).Message);

        var entryPoint = CreateValidHeader();
        entryPoint.EntryPointRaw = (long)int.MaxValue + 1;
        Assert.Contains(expected, Assert.Throws<QvecFormatException>(() => entryPoint.EntryPoint).Message);

        var deleted = CreateValidHeader();
        deleted.DeletedCountRaw = (long)int.MaxValue + 1;
        Assert.Contains(expected, Assert.Throws<QvecFormatException>(() => deleted.DeletedCount).Message);
    }

    [Fact]
    public void GetRequiredSection_WhenAbsent_ThrowsFormatException()
    {
        var header = CreateValidHeader();

        Assert.Throws<QvecFormatException>(() => header.GetRequiredSection(1234));
    }

    private static V4Header CreateValidHeader()
    {
        var header = QvecFormatLayout.CreateInitial(
            vectorDimension: 4,
            maxCount: 8,
            maxNeighbors: 3,
            maxLayers: 2,
            metadataHeapCapacity: 4096,
            distanceFunction: DistanceFunction.Cosine);

        header.Generation = 42;
        header.CurrentCount = 3;
        header.EntryPoint = 1;
        header.EntryPointLevel = 1;
        header.DeletedCount = 1;
        header.MetadataHeapUsed = 100;
        header.FreeListHead = 2;
        header.FreeListCount = 1;
        header.CreatedUnixTimeSeconds = 1_700_000_000;
        header.UpdatedUnixTimeSeconds = 1_700_000_010;

        return header;
    }

    private static byte[] Write(V4Header header)
    {
        var buffer = new byte[V4Header.HeaderSizeValue];
        header.WriteTo(buffer);
        return buffer;
    }

    private static void AssertThrows(V4Header header, string messagePart)
    {
        var ex = Assert.Throws<QvecFormatException>(() =>
            V4Header.Read(Write(header), Math.Max(header.FileLength, V4Header.HeaderSizeValue)));
        Assert.Contains(messagePart, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void RewriteCrc(byte[] buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(V4Header.HeaderCrc32Offset, sizeof(uint)), 0);
        var crc = Crc32.HashToUInt32(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(V4Header.HeaderCrc32Offset, sizeof(uint)), crc);
    }

    private static void AddOptionalSection(V4Header header, uint sectionId, SectionFlags additionalFlags)
    {
        var offset = Align(header.FileLength);
        header.Sections[7] = new SectionTableEntry
        {
            SectionId = sectionId,
            SectionFlags = SectionFlags.Present | additionalFlags,
            Offset = offset,
            Length = 64,
            ElementSize = 1,
        };
        header.FileLength = offset + 64;
        header.NextSectionDataOffset = header.FileLength;
        header.HeaderFlags |= V4HeaderFlags.HasOptionalSections;
    }

    private static long Align(long value)
    {
        var remainder = value % 64;
        return remainder == 0 ? value : value + 64 - remainder;
    }
}

file static class V4HeaderTestExtensions
{
    public static SectionTableEntry GetEntry(this V4Header header, uint sectionId)
    {
        foreach (var entry in header.Sections)
        {
            if (entry.SectionId == sectionId)
            {
                return entry;
            }
        }

        throw new InvalidOperationException($"Section {sectionId} not found.");
    }
}
