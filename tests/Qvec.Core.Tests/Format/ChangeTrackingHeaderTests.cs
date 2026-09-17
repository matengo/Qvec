using System.Buffers.Binary;
using System.IO.Hashing;
using Qvec.Core;
using Qvec.Core.Format;

namespace Qvec.Core.Tests.Format;

public class ChangeTrackingHeaderTests
{
    [Fact]
    public void LayoutConstants_MatchDesignOffsets()
    {
        Assert.Equal(184, V4Header.ReplicaIdOffset);
        Assert.Equal(200, V4Header.ChangeSeqOffset);
        Assert.Equal(208, V4Header.ChangeLogHeadOffset);
        Assert.Equal(216, V4Header.ChangeLogCountOffset);
        Assert.Equal(224, V4Header.LastHlcOffset);
        Assert.Equal(232, V4Header.TrackingEnabledUnixSecondsOffset);
        Assert.Equal(240, V4Header.ReservedOffset);
        Assert.Equal(512, V4Header.ReservedOffset + V4Header.ReservedLength);

        Assert.Equal(5, V4Header.CurrentFormatVersion);
        Assert.Equal(6, V4Header.ChangeTrackingFormatVersion);
        Assert.Equal(11u, V4SectionIds.EntryVersions);
        Assert.Equal(12u, V4SectionIds.ChangeLog);
        Assert.Equal(24u, V4Header.EntryVersionSize);
        Assert.Equal(64u, V4Header.ChangeRecordSize);
    }

    [Fact]
    public void CreateInitial_WithoutTracking_IsVersion5WithZeroTrackingFields()
    {
        var header = CreateUntracked();

        Assert.Equal(V4Header.CurrentFormatVersion, header.Version);
        Assert.False(header.HasChangeTracking);
        Assert.Equal(Guid.Empty, header.ReplicaId);
        Assert.False(header.TryGetSection(V4SectionIds.EntryVersions, out _));
        Assert.False(header.TryGetSection(V4SectionIds.ChangeLog, out _));

        var bytes = Write(header);
        // The whole former reserved area (184..511) is still zero, which is what keeps untracked
        // files byte-identical to those written by 2.0.0.
        Assert.All(bytes.AsSpan(184, 512 - 184).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void CreateInitial_WithTracking_IsVersion6WithBothSections()
    {
        var header = CreateTracked(logCapacity: 16);

        Assert.Equal(V4Header.ChangeTrackingFormatVersion, header.Version);
        Assert.True(header.HasChangeTracking);
        Assert.NotEqual(Guid.Empty, header.ReplicaId);
        Assert.True(header.TrackingEnabledUnixSeconds > 0);

        var versions = header.GetRequiredSection(V4SectionIds.EntryVersions);
        Assert.Equal(V4Header.EntryVersionSize, versions.ElementSize);
        Assert.Equal(8 * V4Header.EntryVersionSize, versions.Length);

        var log = header.GetRequiredSection(V4SectionIds.ChangeLog);
        Assert.Equal(V4Header.ChangeRecordSize, log.ElementSize);
        Assert.Equal(16 * V4Header.ChangeRecordSize, log.Length);
        Assert.Equal(16, header.ChangeLogCapacity);
    }

    [Fact]
    public void CreateInitial_WithTracking_DefaultsLogCapacityToMaxCount()
    {
        var header = QvecFormatLayout.CreateInitial(4, 8, 3, 2, 4096, trackChanges: true);

        Assert.Equal(8, header.ChangeLogCapacity);
    }

    [Fact]
    public void TrackingFields_RoundTripThroughWriteAndRead()
    {
        var header = CreateTracked(logCapacity: 16);
        header.ReplicaId = new Guid("11111111-2222-3333-4444-555555555555");
        header.ChangeSeq = 1234;
        header.ChangeLogHead = 5;
        header.ChangeLogCount = 16;
        header.LastHlc = 0x0000_0190_0000_0007;
        header.TrackingEnabledUnixSeconds = 1_700_000_000;

        var read = V4Header.Read(Write(header), header.FileLength);

        Assert.Equal(header.ReplicaId, read.ReplicaId);
        Assert.Equal(1234, read.ChangeSeq);
        Assert.Equal(5, read.ChangeLogHead);
        Assert.Equal(16, read.ChangeLogCount);
        Assert.Equal(0x0000_0190_0000_0007, read.LastHlc);
        Assert.Equal(1_700_000_000, read.TrackingEnabledUnixSeconds);
        Assert.True(read.HasChangeTracking);
        Assert.Equal(6, read.Version);
    }

    [Fact]
    public void CloneState_CarriesTrackingFields()
    {
        var header = CreateTracked(logCapacity: 16);
        header.ChangeSeq = 9;
        header.LastHlc = 77;

        var clone = header.CloneState();

        Assert.Equal(header.ReplicaId, clone.ReplicaId);
        Assert.Equal(9, clone.ChangeSeq);
        Assert.Equal(77, clone.LastHlc);
        Assert.Equal(header.TrackingEnabledUnixSeconds, clone.TrackingEnabledUnixSeconds);
        Assert.True(clone.HasChangeTracking);
    }

    [Fact]
    public void Read_TrackingFieldFlip_IsCaughtByCrc()
    {
        var bytes = Write(CreateTracked(logCapacity: 16));
        bytes[V4Header.ChangeSeqOffset] ^= 1;

        var ex = Assert.Throws<QvecFormatException>(() => V4Header.Read(bytes, V4Header.HeaderSizeValue * 64));
        Assert.Contains("CRC", ex.Message);
    }

    [Fact]
    public void Read_Version6WithoutTrackingFlag_IsRejected()
    {
        var header = CreateUntracked();
        header.Version = V4Header.ChangeTrackingFormatVersion;

        AssertThrows(header, "HasChangeTracking");
    }

    [Fact]
    public void Read_TrackedHeaderWrittenAsVersion5_IsRejected()
    {
        // A 2.0.0 reader only accepts version 5. Writing a tracked file as version 5 would let it
        // open the file and silently break replication, so the reader must refuse the combination.
        var header = CreateTracked(logCapacity: 16);
        header.Version = V4Header.CurrentFormatVersion;

        AssertThrows(header, "version 6");
    }

    [Fact]
    public void Read_Version7_IsRejectedAsUnsupported()
    {
        var header = CreateUntracked();
        header.Version = 7;

        AssertThrows(header, "format version 7");
    }

    [Fact]
    public void Read_UntrackedHeaderWithNonZeroTrackingFields_IsRejected()
    {
        var header = CreateUntracked();
        header.ChangeSeq = 1;
        AssertThrows(header, "ChangeSeq");

        header = CreateUntracked();
        header.ReplicaId = Guid.NewGuid();
        AssertThrows(header, "ReplicaId");

        header = CreateUntracked();
        header.LastHlc = 1;
        AssertThrows(header, "LastHlc");
    }

    [Fact]
    public void Read_TrackedHeaderWithEmptyReplicaId_IsRejected()
    {
        var header = CreateTracked(logCapacity: 16);
        header.ReplicaId = Guid.Empty;

        AssertThrows(header, "ReplicaId");
    }

    [Fact]
    public void Read_TrackedHeaderMissingSections_IsRejected()
    {
        var header = CreateTracked(logCapacity: 16);
        Remove(header, V4SectionIds.EntryVersions);
        AssertThrows(header, "11");

        header = CreateTracked(logCapacity: 16);
        Remove(header, V4SectionIds.ChangeLog);
        AssertThrows(header, "12");
    }

    [Fact]
    public void Read_TrackedHeaderWithBadShapes_IsRejected()
    {
        var header = CreateTracked(logCapacity: 16);
        header.GetEntry(V4SectionIds.EntryVersions).ElementSize = 8;
        AssertThrows(header, "EntryVersions ElementSize");

        header = CreateTracked(logCapacity: 16);
        header.GetEntry(V4SectionIds.EntryVersions).Length = 24;
        AssertThrows(header, "EntryVersions length");

        header = CreateTracked(logCapacity: 16);
        header.GetEntry(V4SectionIds.ChangeLog).ElementSize = 32;
        AssertThrows(header, "ChangeLog ElementSize");

        header = CreateTracked(logCapacity: 16);
        header.GetEntry(V4SectionIds.ChangeLog).Length = 0;
        AssertThrows(header, "ChangeLog length");
    }

    [Fact]
    public void Read_TrackedHeaderWithRingCountersOutOfRange_IsRejected()
    {
        var header = CreateTracked(logCapacity: 16);
        header.ChangeLogCount = 17;
        AssertThrows(header, "ChangeLogCount");

        header = CreateTracked(logCapacity: 16);
        header.ChangeLogHead = 16;
        AssertThrows(header, "ChangeLogHead");

        header = CreateTracked(logCapacity: 16);
        header.ChangeSeq = -1;
        AssertThrows(header, "ChangeSeq");
    }

    [Fact]
    public void Read_UntrackedHeaderWithTrackingSections_IsRejected()
    {
        var header = CreateUntracked();
        var offset = header.FileLength;
        header.Sections[9] = new SectionTableEntry
        {
            SectionId = V4SectionIds.EntryVersions,
            SectionFlags = SectionFlags.Present | SectionFlags.Mutable | SectionFlags.MayMoveOnGrow,
            Offset = offset,
            Length = 8 * 24,
            ElementSize = 24,
        };
        header.FileLength = offset + 8 * 24;
        header.NextSectionDataOffset = header.FileLength;
        header.HeaderFlags |= V4HeaderFlags.HasOptionalSections;

        AssertThrows(header, "HasChangeTracking");
    }

    [Fact]
    public void CreateGrown_PreservesTrackingAndScalesLogCapacity()
    {
        var header = CreateTracked(logCapacity: 16);
        header.ChangeSeq = 3;
        header.LastHlc = 99;

        var grown = QvecFormatLayout.CreateGrown(header, newMaxCount: 16, newMetadataHeapCapacity: 4096);

        Assert.True(grown.HasChangeTracking);
        Assert.Equal(6, grown.Version);
        Assert.Equal(header.ReplicaId, grown.ReplicaId);
        Assert.Equal(3, grown.ChangeSeq);
        Assert.Equal(99, grown.LastHlc);
        Assert.Equal(16 * V4Header.EntryVersionSize, grown.GetRequiredSection(V4SectionIds.EntryVersions).Length);
        // The log grows in step with the row capacity so a full-size batch never rotates itself out.
        Assert.Equal(32, grown.ChangeLogCapacity);

        V4Header.Read(Write(grown), grown.FileLength);
    }

    [Fact]
    public void CreateGrown_WithoutTracking_StaysUntracked()
    {
        var grown = QvecFormatLayout.CreateGrown(CreateUntracked(), 16, 4096);

        Assert.False(grown.HasChangeTracking);
        Assert.Equal(5, grown.Version);
        Assert.False(grown.TryGetSection(V4SectionIds.EntryVersions, out _));
    }

    [Fact]
    public void CreateTracked_AddsSectionsToExistingLayout()
    {
        var untracked = CreateUntracked();
        untracked.CurrentCount = 5;
        untracked.EntryPoint = 2;

        var tracked = QvecFormatLayout.CreateTracked(untracked, changeLogCapacity: 32);

        Assert.True(tracked.HasChangeTracking);
        Assert.Equal(6, tracked.Version);
        Assert.Equal(5, tracked.CurrentCount);
        Assert.Equal(2, tracked.EntryPoint);
        Assert.Equal(untracked.MaxCount, tracked.MaxCount);
        Assert.Equal(32, tracked.ChangeLogCapacity);
        Assert.True(tracked.FileLength > untracked.FileLength);

        // Existing sections keep their offsets: only the two new ones are appended.
        foreach (var entry in untracked.Sections)
        {
            if (entry.SectionId == V4SectionIds.Unused) continue;
            Assert.Equal(entry.Offset, tracked.GetRequiredSection(entry.SectionId).Offset);
        }

        Assert.NotEqual(Guid.Empty, tracked.ReplicaId);
        V4Header.Read(Write(tracked), tracked.FileLength);
    }

    [Fact]
    public void CreateTracked_OnTrackedHeader_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => QvecFormatLayout.CreateTracked(CreateTracked(16), 16));
    }

    private static V4Header CreateUntracked()
        => QvecFormatLayout.CreateInitial(4, 8, 3, 2, 4096, DistanceFunction.Cosine);

    private static V4Header CreateTracked(long logCapacity)
        => QvecFormatLayout.CreateInitial(4, 8, 3, 2, 4096, DistanceFunction.Cosine, trackChanges: true, changeLogCapacity: logCapacity);

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

    private static void Remove(V4Header header, uint sectionId)
    {
        for (int i = 0; i < header.Sections.Length; i++)
        {
            if (header.Sections[i].SectionId == sectionId)
            {
                header.Sections[i] = new SectionTableEntry();
                return;
            }
        }
    }
}

file static class Ext
{
    public static SectionTableEntry GetEntry(this V4Header header, uint sectionId)
    {
        foreach (var entry in header.Sections)
        {
            if (entry.SectionId == sectionId) return entry;
        }

        throw new InvalidOperationException($"Section {sectionId} not found.");
    }
}
