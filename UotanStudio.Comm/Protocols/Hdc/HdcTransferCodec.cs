using System.Buffers;
using System.Text;
using K4os.Compression.LZ4;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>Upstream transfer.h / serial_struct.h file and app metadata codecs.</summary>
internal static class HdcTransferCodec
{
    /// <summary>Encodes a transfer configuration without reflection. <para>不借助反射编码传输配置。</para></summary>
    public static byte[] EncodeConfig(TransferConfig config)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(config.FileSize);
        using var output = new MemoryStream();
        HdcDaemonCodec.Number(output, 1, (ulong)config.FileSize);
        HdcDaemonCodec.Number(output, 2, checked((ulong)config.Atime));
        HdcDaemonCodec.Number(output, 3, checked((ulong)config.Mtime));
        void Text(uint tag, string value) => HdcDaemonCodec.Bytes(output, tag, Encoding.UTF8.GetBytes(value));
        Text(4, config.Options); Text(5, config.Path); Text(6, config.OptionalName);
        HdcDaemonCodec.Number(output, 7, config.UpdateIfNew ? 1u : 0);
        HdcDaemonCodec.Number(output, 8, checked((ulong)config.CompressType));
        HdcDaemonCodec.Number(output, 9, config.HoldTimestamp ? 1u : 0);
        Text(10, config.FunctionName); Text(11, config.ClientCwd); Text(12, config.Reserve1); Text(13, config.Reserve2);
        return output.ToArray();
    }

    /// <summary>Decodes metadata and rejects out-of-range file sizes. <para>解码元数据并拒绝越界的文件大小。</para></summary>
    public static TransferConfig DecodeConfig(ReadOnlySpan<byte> data)
    {
        var fields = HdcDaemonCodec.ReadFields(data);
        long Number(uint tag) => checked((long)HdcDaemonCodec.GetNumber(fields, tag));
        string Text(uint tag) => fields.TryGetValue(tag, out var field) && field.Bytes is { } bytes
            ? Encoding.UTF8.GetString(bytes) : throw new InvalidDataException("Missing HDC transfer string field.");
        return new TransferConfig
        {
            FileSize = Number(1),
            Atime = Number(2),
            Mtime = Number(3),
            Options = Text(4),
            Path = Text(5),
            OptionalName = Text(6),
            UpdateIfNew = Number(7) != 0,
            CompressType = checked((int)Number(8)),
            HoldTimestamp = Number(9) != 0,
            FunctionName = Text(10),
            ClientCwd = Text(11),
            Reserve1 = Text(12),
            Reserve2 = Text(13),
        };
    }

    /// <summary>Encodes a raw chunk after the upstream 64-byte reserved metadata prefix. <para>在上游 64 字节保留元数据前缀之后编码原始分块。</para></summary>
    /// <remarks>The metadata varints are written directly into the returned buffer and a compressed payload is staged
    /// through a pooled scratch buffer, so one chunk never allocates a second full-size copy of its data.
    /// <para>元数据 varint 直接写入返回的缓冲区，压缩载荷经池化暂存缓冲中转，因此单个分块不会再分配第二份等大的数据副本。</para>
    /// </remarks>
    public static byte[] EncodeChunk(long offset, ReadOnlySpan<byte> data, bool compress = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (data.Length > HdcConstants.MaxPayloadSize - 64) throw new InvalidDataException("HDC transfer chunk exceeds limit.");
        var expanded = data.Length;
        byte[]? rented = null;
        try
        {
            var compressed = false;
            if (compress && !data.IsEmpty)
            {
                var maximum = LZ4Codec.MaximumOutputSize(data.Length);
                rented = ArrayPool<byte>.Shared.Rent(maximum);
                var count = LZ4Codec.Encode(data, rented.AsSpan(0, maximum));
                if (count > 0 && count < data.Length)
                {
                    data = rented.AsSpan(0, count);
                    compressed = true;
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = null;
                }
            }

            var result = new byte[64 + data.Length];
            var metadataLength = HdcDaemonCodec.WriteNumber(result, 0, 1, (ulong)offset);
            metadataLength = HdcDaemonCodec.WriteNumber(result, metadataLength, 2, compressed ? 1u : 0u);
            metadataLength = HdcDaemonCodec.WriteNumber(result, metadataLength, 3, (uint)data.Length);
            _ = HdcDaemonCodec.WriteNumber(result, metadataLength, 4, (uint)expanded);
            data.CopyTo(result.AsSpan(64));
            return result;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Decodes bounded raw transfer data; unsupported compression is rejected. <para>解码有界的原始传输数据；不支持的压缩方式被拒绝。</para></summary>
    public static (long Offset, byte[] Data) DecodeChunk(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 64 || payload.Length > HdcConstants.MaxPayloadSize)
            throw new InvalidDataException("Invalid HDC transfer packet size.");
        var fields = HdcDaemonCodec.ReadFields(payload[..64], allowPadding: true);
        var offset = HdcDaemonCodec.GetNumber(fields, 1);
        var compression = HdcDaemonCodec.GetNumber(fields, 2);
        var size = HdcDaemonCodec.GetNumber(fields, 3);
        var expanded = HdcDaemonCodec.GetNumber(fields, 4);
        if (offset > long.MaxValue || size != (ulong)payload.Length - 64 || expanded > HdcConstants.MaxPayloadSize)
            throw new InvalidDataException("Invalid HDC transfer chunk lengths.");
        if (compression == 1)
        {
            var decoded = new byte[(int)expanded];
            if (LZ4Codec.Decode(payload[64..], decoded) != decoded.Length)
                throw new InvalidDataException("Invalid HDC LZ4 chunk.");
            return ((long)offset, decoded);
        }
        if (compression != 0) throw new NotSupportedException($"HDC transfer compression {compression} is unsupported.");
        if (size != expanded) throw new InvalidDataException("HDC raw transfer size mismatch.");
        return ((long)offset, payload[64..].ToArray());
    }
}