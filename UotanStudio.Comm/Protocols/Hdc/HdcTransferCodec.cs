using System.Text;
using K4os.Compression.LZ4;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>Upstream transfer.h / serial_struct.h file and app metadata codecs.</summary>
public static class HdcTransferCodec
{
    /// <summary>Encodes a transfer configuration without reflection.</summary>
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

    /// <summary>Decodes metadata and rejects out-of-range file sizes.</summary>
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

    /// <summary>Encodes a raw chunk after the upstream 64-byte reserved metadata prefix.</summary>
    public static byte[] EncodeChunk(long offset, ReadOnlySpan<byte> data, bool compress = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (data.Length > HdcConstants.MaxPayloadSize - 64) throw new InvalidDataException("HDC transfer chunk exceeds limit.");
        var expanded = data.Length;
        byte[]? compressed = null;
        if (compress && !data.IsEmpty)
        {
            compressed = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
            var count = LZ4Codec.Encode(data, compressed);
            if (count > 0 && count < data.Length) data = compressed.AsSpan(0, count);
            else compressed = null;
        }
        using var metadata = new MemoryStream();
        HdcDaemonCodec.Number(metadata, 1, (ulong)offset); HdcDaemonCodec.Number(metadata, 2, compressed is null ? 0u : 1u);
        HdcDaemonCodec.Number(metadata, 3, (uint)data.Length); HdcDaemonCodec.Number(metadata, 4, (uint)expanded);
        var result = new byte[64 + data.Length];
        metadata.GetBuffer().AsSpan(0, (int)metadata.Length).CopyTo(result);
        data.CopyTo(result.AsSpan(64));
        return result;
    }

    /// <summary>Decodes bounded raw transfer data; unsupported compression is rejected.</summary>
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