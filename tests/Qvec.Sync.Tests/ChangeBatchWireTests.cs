using Qvec.Core;
using Qvec.Core.Quantization;
using Qvec.Core.Sync;

namespace Qvec.Sync.Tests;

public class ChangeBatchWireTests
{
    private static readonly Guid Origin = Guid.NewGuid();

    private static ChangeBatch FloatBatch(int items, int metadataLength = 5, bool hasMore = false)
    {
        var rng = new Random(items);
        var list = new List<ChangeItem>();
        for (int i = 0; i < items; i++)
        {
            list.Add(new ChangeItem
            {
                Type = ChangeType.Upsert,
                DocumentId = Guid.NewGuid(),
                Version = new EntryVersion(1000 + i, Origin),
                Vector = Vec.Random(8, rng),
                Metadata = new string('m', metadataLength) + i,
            });
        }
        return new ChangeBatch
        {
            SourceReplicaId = Origin, Dimension = 8, DistanceFunction = DistanceFunction.Cosine,
            Payload = ChangePayloadKind.Float, FromSeq = 11, ToSeq = 10 + items, Items = list, HasMore = hasMore,
        };
    }

    private static void AssertEquivalent(ChangeBatch expected, ChangeBatch actual)
    {
        Assert.Equal(expected.SourceReplicaId, actual.SourceReplicaId);
        Assert.Equal(expected.Dimension, actual.Dimension);
        Assert.Equal(expected.DistanceFunction, actual.DistanceFunction);
        Assert.Equal(expected.Payload, actual.Payload);
        Assert.Equal(expected.FromSeq, actual.FromSeq);
        Assert.Equal(expected.ToSeq, actual.ToSeq);
        Assert.Equal(expected.HasMore, actual.HasMore);
        Assert.Equal(expected.Items.Count, actual.Items.Count);
        for (int i = 0; i < expected.Items.Count; i++)
        {
            var e = expected.Items[i];
            var a = actual.Items[i];
            Assert.Equal(e.Type, a.Type);
            Assert.Equal(e.DocumentId, a.DocumentId);
            Assert.Equal(e.Version, a.Version);
            Assert.Equal(e.Vector, a.Vector);
            Assert.Equal(e.Codes, a.Codes);
            Assert.Equal(e.Parameters.HasValue, a.Parameters.HasValue);
            if (e.Parameters.HasValue)
            {
                var eb = new byte[Int8VectorParameters.Size];
                var ab = new byte[Int8VectorParameters.Size];
                e.Parameters.Value.WriteTo(eb);
                a.Parameters!.Value.WriteTo(ab);
                Assert.Equal(eb, ab);
            }
            Assert.Equal(e.Metadata, a.Metadata);
        }
    }

    [Theory]
    [InlineData(SyncCompression.None)]
    [InlineData(SyncCompression.Brotli)]
    [InlineData(SyncCompression.Auto)]
    public void FloatUpserts_RoundTrip(SyncCompression compression)
    {
        var batch = FloatBatch(7, hasMore: true);
        var bytes = ChangeBatchWire.Write(batch, compression);
        AssertEquivalent(batch, ChangeBatchWire.Read(bytes));
    }

    [Fact]
    public void Int8Upserts_RoundTrip()
    {
        var rng = new Random(3);
        var items = new List<ChangeItem>();
        for (int i = 0; i < 4; i++)
        {
            var codes = new byte[16];
            rng.NextBytes(codes);
            var paramBytes = new byte[Int8VectorParameters.Size];
            rng.NextBytes(paramBytes);
            items.Add(new ChangeItem
            {
                Type = ChangeType.Upsert, DocumentId = Guid.NewGuid(), Version = new EntryVersion(5 + i, Origin),
                Codes = codes, Parameters = Int8VectorParameters.ReadFrom(paramBytes), Metadata = "{\"i\":" + i + "}",
            });
        }
        var batch = new ChangeBatch
        {
            SourceReplicaId = Origin, Dimension = 16, DistanceFunction = DistanceFunction.DotProduct,
            Payload = ChangePayloadKind.Int8, FromSeq = 1, ToSeq = 4, Items = items, HasMore = false,
        };

        AssertEquivalent(batch, ChangeBatchWire.Read(ChangeBatchWire.Write(batch)));
    }

    [Fact]
    public void Deletes_And_Unicode_RoundTrip()
    {
        var batch = new ChangeBatch
        {
            SourceReplicaId = Origin, Dimension = 8, DistanceFunction = DistanceFunction.Euclidean,
            Payload = ChangePayloadKind.Float, FromSeq = 100, ToSeq = 101, HasMore = false,
            Items =
            [
                new ChangeItem { Type = ChangeType.Delete, DocumentId = Guid.NewGuid(), Version = new EntryVersion(77, Origin) },
                new ChangeItem { Type = ChangeType.Upsert, DocumentId = Guid.NewGuid(), Version = new EntryVersion(78, Origin), Vector = Vec.Basis(8, 3), Metadata = "räksmörgås 🦐" },
            ],
        };

        AssertEquivalent(batch, ChangeBatchWire.Read(ChangeBatchWire.Write(batch)));
    }

    [Fact]
    public void EmptyBatch_RoundTrip()
    {
        var batch = FloatBatch(0);
        var bytes = ChangeBatchWire.Write(batch);
        var read = ChangeBatchWire.Read(bytes);
        Assert.Empty(read.Items);
        Assert.Equal(batch.FromSeq, read.FromSeq);
        Assert.Equal(batch.ToSeq, read.ToSeq);
    }

    [Fact]
    public void Auto_PicksBrotli_WhenMetadataDominates_AndNone_WhenVectorsDo()
    {
        var chatty = FloatBatch(20, metadataLength: 400);
        var terse = FloatBatch(20, metadataLength: 0);

        Assert.Equal((byte)SyncCompression.Brotli, ChangeBatchWire.Write(chatty)[6]);
        Assert.Equal((byte)SyncCompression.None, ChangeBatchWire.Write(terse)[6]);

        // Brotli should actually pay for itself on the chatty batch.
        Assert.True(ChangeBatchWire.Write(chatty).Length < ChangeBatchWire.Write(chatty, SyncCompression.None).Length);
    }

    [Fact]
    public void Stream_RoundTrip_LeavesPositionAfterBatch()
    {
        var a = FloatBatch(3);
        var b = FloatBatch(2);
        using var ms = new MemoryStream();
        ChangeBatchWire.Write(a, ms);
        ChangeBatchWire.Write(b, ms, SyncCompression.Brotli);
        ms.Position = 0;

        AssertEquivalent(a, ChangeBatchWire.Read(ms));
        AssertEquivalent(b, ChangeBatchWire.Read(ms));
        Assert.Equal(ms.Length, ms.Position);
    }

    [Fact]
    public void FlippedBit_FailsCrc()
    {
        var bytes = ChangeBatchWire.Write(FloatBatch(3), SyncCompression.None);
        bytes[70] ^= 0x01;
        var ex = Assert.Throws<SyncProtocolException>(() => ChangeBatchWire.Read(bytes));
        Assert.Contains("CRC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongMagic_Rejected()
    {
        var bytes = ChangeBatchWire.Write(FloatBatch(1));
        bytes[0] = (byte)'X';
        Assert.Throws<SyncProtocolException>(() => ChangeBatchWire.Read(bytes));
    }

    [Fact]
    public void UnknownVersion_Rejected()
    {
        var bytes = ChangeBatchWire.Write(FloatBatch(1));
        bytes[4] = 99;
        Assert.Throws<SyncProtocolException>(() => ChangeBatchWire.Read(bytes));
    }

    [Fact]
    public void Truncated_Rejected()
    {
        var bytes = ChangeBatchWire.Write(FloatBatch(3));
        Assert.Throws<SyncProtocolException>(() => ChangeBatchWire.Read(bytes.AsSpan(0, bytes.Length - 3)));
        Assert.Throws<SyncProtocolException>(() => ChangeBatchWire.Read(bytes.AsSpan(0, 10)));
        using var ms = new MemoryStream(bytes, 0, bytes.Length / 2);
        Assert.Throws<SyncProtocolException>(() => ChangeBatchWire.Read(ms));
    }

    [Fact]
    public void TrailingBytes_Rejected()
    {
        var bytes = ChangeBatchWire.Write(FloatBatch(1));
        Assert.Throws<SyncProtocolException>(() => ChangeBatchWire.Read([.. bytes, 0]));
    }
}
