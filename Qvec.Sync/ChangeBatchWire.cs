using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Qvec.Core;
using Qvec.Core.Quantization;
using Qvec.Core.Sync;

namespace Qvec.Sync;

/// <summary>How a <see cref="ChangeBatch"/> body is compressed on the wire.</summary>
public enum SyncCompression : byte
{
    /// <summary>Compress with Brotli when metadata makes up a meaningful share of the body (≥ 20 %).</summary>
    Auto = 255,
    None = 0,
    Brotli = 1,
    // 2 is reserved for Zstandard once it is available in the BCL (.NET 11).
}

/// <summary>
/// Binary, versioned, reflection-free encoding of a <see cref="ChangeBatch"/>:
/// <code>
/// "QVCB" | Version u16 | Compression u8 | PayloadKind u8 | Dim i32 | Distance u8 | HasMore u8 | Count i32
/// FromSeq i64 | ToSeq i64 | Origin 16 | BodyRawLength i32 | BodyStoredLength i32 | Body | Crc32 u32
/// </code>
/// The CRC covers everything before it. Items are encoded as
/// <c>Type u8 | DocumentId 16 | Hlc i64 | Origin 16 | [vector] | MetadataLen i32 | UTF-8</c>,
/// where the vector part is present only for upserts: <c>Dim × f32</c> for float payloads,
/// <c>Dim × u8 + 16 parameter bytes</c> for int8.
/// </summary>
public static class ChangeBatchWire
{
    public const ushort FormatVersion = 1;
    private const int HeaderSize = 4 + 2 + 1 + 1 + 4 + 1 + 1 + 4 + 8 + 8 + 16 + 4 + 4;
    private const int ItemFixedSize = 1 + 16 + 8 + 16;
    private const int MaxBodyLength = 1 << 30;
    private static readonly byte[] Magic = "QVCB"u8.ToArray();

    /// <summary>Encodes <paramref name="batch"/> and returns the bytes.</summary>
    public static byte[] Write(ChangeBatch batch, SyncCompression compression = SyncCompression.Auto)
    {
        using var ms = new MemoryStream();
        Write(batch, ms, compression);
        return ms.ToArray();
    }

    /// <summary>Encodes <paramref name="batch"/> to <paramref name="destination"/>.</summary>
    public static void Write(ChangeBatch batch, Stream destination, SyncCompression compression = SyncCompression.Auto)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(destination);
        if (batch.Items.Count > int.MaxValue / ItemFixedSize)
            throw new ArgumentException("Too many items for one wire batch.", nameof(batch));

        byte[] raw = EncodeBody(batch, out long metadataBytes);
        SyncCompression chosen = compression switch
        {
            SyncCompression.Auto => raw.Length > 0 && metadataBytes * 5 >= raw.Length ? SyncCompression.Brotli : SyncCompression.None,
            SyncCompression.None or SyncCompression.Brotli => compression,
            _ => throw new ArgumentOutOfRangeException(nameof(compression)),
        };
        byte[] stored = chosen == SyncCompression.Brotli ? CompressBrotli(raw) : raw;

        var header = new byte[HeaderSize];
        var span = header.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], FormatVersion);
        span[6] = (byte)chosen;
        span[7] = (byte)batch.Payload;
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], batch.Dimension);
        span[12] = checked((byte)batch.DistanceFunction);
        span[13] = batch.HasMore ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], batch.Items.Count);
        BinaryPrimitives.WriteInt64LittleEndian(span[18..], batch.FromSeq);
        BinaryPrimitives.WriteInt64LittleEndian(span[26..], batch.ToSeq);
        batch.SourceReplicaId.TryWriteBytes(span[34..50]);
        BinaryPrimitives.WriteInt32LittleEndian(span[50..], raw.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[54..], stored.Length);

        var crc = new Crc32();
        crc.Append(header);
        crc.Append(stored);
        var trailer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, crc.GetCurrentHashAsUInt32());

        destination.Write(header);
        destination.Write(stored);
        destination.Write(trailer);
    }

    /// <summary>Decodes a batch that was fully read into memory.</summary>
    /// <exception cref="SyncProtocolException">The bytes are not a valid batch of a supported version.</exception>
    public static ChangeBatch Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + 4)
            throw new SyncProtocolException($"Change batch is truncated: {data.Length} bytes, header alone is {HeaderSize + 4}.");
        if (!data[..4].SequenceEqual(Magic))
            throw new SyncProtocolException("Change batch does not start with the QVCB magic.");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (version != FormatVersion)
            throw new SyncProtocolException($"Change batch format version {version} is not supported by this reader (expected {FormatVersion}).");

        var compression = (SyncCompression)data[6];
        var payload = (ChangePayloadKind)data[7];
        int dim = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        var distance = (DistanceFunction)data[12];
        bool hasMore = data[13] != 0;
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
        long fromSeq = BinaryPrimitives.ReadInt64LittleEndian(data[18..]);
        long toSeq = BinaryPrimitives.ReadInt64LittleEndian(data[26..]);
        var origin = new Guid(data[34..50]);
        int rawLength = BinaryPrimitives.ReadInt32LittleEndian(data[50..]);
        int storedLength = BinaryPrimitives.ReadInt32LittleEndian(data[54..]);

        if (payload is not (ChangePayloadKind.Float or ChangePayloadKind.Int8))
            throw new SyncProtocolException($"Unknown payload kind {(byte)payload}.");
        if (dim <= 0 || count < 0 || rawLength < 0 || storedLength < 0 || rawLength > MaxBodyLength || storedLength > MaxBodyLength)
            throw new SyncProtocolException("Change batch header contains an out-of-range length.");
        if (data.Length != HeaderSize + storedLength + 4)
            throw new SyncProtocolException($"Change batch length {data.Length} does not match header ({HeaderSize + storedLength + 4} expected).");

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data[(HeaderSize + storedLength)..]);
        uint actualCrc = Crc32.HashToUInt32(data[..(HeaderSize + storedLength)]);
        if (expectedCrc != actualCrc)
            throw new SyncProtocolException($"Change batch CRC mismatch: header says 0x{expectedCrc:X8}, content hashes to 0x{actualCrc:X8}.");

        ReadOnlySpan<byte> stored = data.Slice(HeaderSize, storedLength);
        byte[] raw = compression switch
        {
            SyncCompression.None => stored.ToArray(),
            SyncCompression.Brotli => DecompressBrotli(stored, rawLength),
            _ => throw new SyncProtocolException($"Unknown compression {(byte)compression}."),
        };
        if (raw.Length != rawLength)
            throw new SyncProtocolException($"Change batch body decompressed to {raw.Length} bytes, header says {rawLength}.");

        var items = DecodeBody(raw, count, dim, payload);
        return new ChangeBatch
        {
            SourceReplicaId = origin,
            Dimension = dim,
            DistanceFunction = distance,
            Payload = payload,
            FromSeq = fromSeq,
            ToSeq = toSeq,
            Items = items,
            HasMore = hasMore,
        };
    }

    /// <summary>Reads exactly one batch from <paramref name="source"/>, leaving the position right after it.</summary>
    public static ChangeBatch Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var header = new byte[HeaderSize];
        int got = source.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false);
        if (got < HeaderSize)
            throw new SyncProtocolException($"Change batch is truncated: stream ended after {got} bytes, header alone is {HeaderSize + 4}.");
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new SyncProtocolException("Change batch does not start with the QVCB magic.");

        int storedLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(54));
        if (storedLength < 0 || storedLength > MaxBodyLength)
            throw new SyncProtocolException("Change batch header contains an out-of-range length.");

        var buffer = new byte[HeaderSize + storedLength + 4];
        header.CopyTo(buffer, 0);
        int rest = source.ReadAtLeast(buffer.AsSpan(HeaderSize), storedLength + 4, throwOnEndOfStream: false);
        if (rest < storedLength + 4)
            throw new SyncProtocolException($"Change batch is truncated: expected {storedLength + 4} body bytes, stream ended after {rest}.");
        return Read(buffer);
    }

    private static byte[] EncodeBody(ChangeBatch batch, out long metadataBytes)
    {
        metadataBytes = 0;
        int dim = batch.Dimension;
        int vectorSize = batch.Payload == ChangePayloadKind.Float ? dim * sizeof(float) : dim + Int8VectorParameters.Size;

        long size = 0;
        var metadata = new byte[batch.Items.Count][];
        for (int i = 0; i < batch.Items.Count; i++)
        {
            var item = batch.Items[i];
            size += ItemFixedSize;
            if (item.Type != ChangeType.Upsert) continue;

            metadata[i] = Encoding.UTF8.GetBytes(item.Metadata ?? string.Empty);
            metadataBytes += metadata[i].Length;
            size += vectorSize + 4 + metadata[i].Length;
        }
        if (size > MaxBodyLength)
            throw new ArgumentException($"Change batch body would be {size} bytes; the wire limit is {MaxBodyLength}. Use a smaller batch size.", nameof(batch));

        var body = new byte[size];
        var span = body.AsSpan();
        int pos = 0;
        for (int i = 0; i < batch.Items.Count; i++)
        {
            var item = batch.Items[i];
            span[pos++] = (byte)item.Type;
            item.DocumentId.TryWriteBytes(span.Slice(pos, 16)); pos += 16;
            BinaryPrimitives.WriteInt64LittleEndian(span[pos..], item.Version.Hlc); pos += 8;
            item.Version.Origin.TryWriteBytes(span.Slice(pos, 16)); pos += 16;
            if (item.Type != ChangeType.Upsert) continue;

            if (batch.Payload == ChangePayloadKind.Float)
            {
                var vector = item.Vector ?? throw new ArgumentException($"Item {i} is an upsert without a vector.", nameof(batch));
                if (vector.Length != dim) throw new ArgumentException($"Item {i} has dimension {vector.Length}, batch says {dim}.", nameof(batch));
                for (int d = 0; d < dim; d++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(span[pos..], vector[d]);
                    pos += sizeof(float);
                }
            }
            else
            {
                var codes = item.Codes ?? throw new ArgumentException($"Item {i} is an int8 upsert without codes.", nameof(batch));
                if (codes.Length != dim) throw new ArgumentException($"Item {i} has {codes.Length} codes, batch says {dim}.", nameof(batch));
                var parameters = item.Parameters ?? throw new ArgumentException($"Item {i} is an int8 upsert without parameters.", nameof(batch));
                codes.CopyTo(span.Slice(pos, dim)); pos += dim;
                parameters.WriteTo(span.Slice(pos, Int8VectorParameters.Size)); pos += Int8VectorParameters.Size;
            }

            BinaryPrimitives.WriteInt32LittleEndian(span[pos..], metadata[i].Length); pos += 4;
            metadata[i].CopyTo(span[pos..]); pos += metadata[i].Length;
        }

        return body;
    }

    private static List<ChangeItem> DecodeBody(ReadOnlySpan<byte> body, int count, int dim, ChangePayloadKind payload)
    {
        var items = new List<ChangeItem>(count);
        int pos = 0;
        for (int i = 0; i < count; i++)
        {
            Need(body, pos, ItemFixedSize);
            var type = (ChangeType)body[pos++];
            var id = new Guid(body.Slice(pos, 16)); pos += 16;
            long hlc = BinaryPrimitives.ReadInt64LittleEndian(body[pos..]); pos += 8;
            var origin = new Guid(body.Slice(pos, 16)); pos += 16;
            var version = new EntryVersion(hlc, origin);

            switch (type)
            {
                case ChangeType.Delete:
                    items.Add(new ChangeItem { Type = type, DocumentId = id, Version = version });
                    break;

                case ChangeType.Upsert:
                {
                    float[]? vector = null;
                    byte[]? codes = null;
                    Int8VectorParameters? parameters = null;
                    if (payload == ChangePayloadKind.Float)
                    {
                        Need(body, pos, dim * sizeof(float));
                        vector = new float[dim];
                        for (int d = 0; d < dim; d++)
                        {
                            vector[d] = BinaryPrimitives.ReadSingleLittleEndian(body[pos..]);
                            pos += sizeof(float);
                        }
                    }
                    else
                    {
                        Need(body, pos, dim + Int8VectorParameters.Size);
                        codes = body.Slice(pos, dim).ToArray(); pos += dim;
                        parameters = Int8VectorParameters.ReadFrom(body.Slice(pos, Int8VectorParameters.Size)); pos += Int8VectorParameters.Size;
                    }

                    Need(body, pos, 4);
                    int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(body[pos..]); pos += 4;
                    if (metadataLength < 0) throw new SyncProtocolException($"Item {i} has a negative metadata length.");
                    Need(body, pos, metadataLength);
                    string metadata = Encoding.UTF8.GetString(body.Slice(pos, metadataLength)); pos += metadataLength;

                    items.Add(new ChangeItem
                    {
                        Type = type, DocumentId = id, Version = version,
                        Vector = vector, Codes = codes, Parameters = parameters, Metadata = metadata,
                    });
                    break;
                }

                default:
                    throw new SyncProtocolException($"Item {i} has unknown change type {(byte)type}.");
            }
        }

        if (pos != body.Length)
            throw new SyncProtocolException($"Change batch body has {body.Length - pos} trailing bytes.");
        return items;

        static void Need(ReadOnlySpan<byte> body, int pos, int bytes)
        {
            if (bytes < 0 || pos > body.Length - bytes)
                throw new SyncProtocolException("Change batch body is truncated.");
        }
    }

    private static byte[] CompressBrotli(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var brotli = new BrotliStream(ms, new BrotliCompressionOptions { Quality = 4 }, leaveOpen: true))
        {
            brotli.Write(raw);
        }
        return ms.ToArray();
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> stored, int rawLength)
    {
        var raw = new byte[rawLength];
        using var input = new MemoryStream(stored.ToArray());
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        int read = 0;
        while (read < rawLength)
        {
            int n = brotli.Read(raw, read, rawLength - read);
            if (n == 0) break;
            read += n;
        }
        if (read != rawLength || brotli.ReadByte() != -1)
            throw new SyncProtocolException($"Change batch body decompressed to a different size than the header's {rawLength} bytes.");
        return raw;
    }
}
