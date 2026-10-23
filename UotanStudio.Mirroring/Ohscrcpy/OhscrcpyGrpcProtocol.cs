using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Mirroring;

/// <summary>One parsed <c>ReplyMessage</c> from the device gRPC video stream.
/// <para>设备 gRPC 视频流中解析出的一条 <c>ReplyMessage</c>。</para>
/// </summary>
/// <param name="Data">Diagnostic text carried in field 1, or <see langword="null"/>.
/// <para>字段 1 携带的诊断文本；没有时为 <see langword="null"/>。</para></param>
/// <param name="ReplyType">Numeric reply type carried in field 2. <para>字段 2 携带的数字回复类型。</para></param>
/// <param name="VideoPayload">Bytes under the <c>data</c> map key, or <see langword="null"/> when absent.
/// <para><c>data</c> 映射键下的字节；缺失时为 <see langword="null"/>。</para></param>
internal sealed record OhscrcpyReply(string? Data, int ReplyType, byte[]? VideoPayload);

/// <summary>
/// Encodes and decodes the <c>scrcpy.proto</c> messages and gRPC message framing used by the pushed
/// capture library. <para>编码与解码推送采集库使用的 <c>scrcpy.proto</c> 消息及 gRPC 消息分帧。</para>
/// </summary>
/// <remarks>
/// <para>
/// The device serves a standard gRPC HTTP/2 endpoint. The contract is deliberately limited to what the
/// extension publishes: an empty request, a server-streaming <c>ReplyMessage</c> whose <c>payload</c> map
/// carries <c>data</c> bytes, and two unary <c>ReplyEndMessage</c> calls.
/// </para>
/// <para>
/// 设备提供标准 gRPC HTTP/2 端点。契约仅覆盖扩展实际发布的内容：空请求、其 <c>payload</c> 映射携带
/// <c>data</c> 字节的服务端流 <c>ReplyMessage</c>，以及两个一元 <c>ReplyEndMessage</c> 调用。
/// </para>
/// </remarks>
internal static class OhscrcpyGrpcProtocol
{
    private const int MaximumDiagnosticTextLength = 4096;

    internal static byte[] BuildEmptyRequest() => new byte[5];

    internal static async Task WriteEmptyRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(BuildEmptyRequest(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one length-prefixed gRPC message, returning <see langword="null"/> at a clean boundary.
    /// <para>读取一条带长度前缀的 gRPC 消息；在干净边界处返回 <see langword="null"/>。</para>
    /// </summary>
    internal static async Task<byte[]?> ReadMessageAsync(
        Stream stream, int maximumLength, CancellationToken cancellationToken)
    {
        var header = new byte[OhscrcpyConstants.GrpcHeaderLength];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        if (header[0] != 0)
        {
            throw new InvalidDataException("The device gRPC stream compressed a message the client cannot read.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (length > (uint)maximumLength)
        {
            throw new InvalidDataException(
                $"The device gRPC message announces {length} bytes, above the {maximumLength}-byte bound.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    internal static bool TryParseReply(ReadOnlySpan<byte> payload, out OhscrcpyReply? reply)
    {
        reply = null;
        string? data = null;
        var replyType = 0;
        byte[]? videoPayload = null;
        var offset = 0;
        while (offset < payload.Length)
        {
            if (!TryReadTag(payload, ref offset, out var field, out var wireType)) return false;
            if (field == 1 && wireType == 2)
            {
                if (!TryReadLengthDelimited(payload, ref offset, out var slice)) return false;
                if (slice.Length <= MaximumDiagnosticTextLength) data = Encoding.UTF8.GetString(slice);
            }
            else if (field == 2 && wireType == 0)
            {
                if (!TryReadVarint(payload, ref offset, out var value) || value > int.MaxValue) return false;
                replyType = (int)value;
            }
            else if (field == 3 && wireType == 2)
            {
                if (!TryReadLengthDelimited(payload, ref offset, out var entry)) return false;
                if (TryParsePayloadEntry(entry, out var key, out var value) && key == OhscrcpyConstants.VideoPayloadKey)
                {
                    videoPayload = value;
                }
            }
            else if (!TrySkip(payload, ref offset, wireType))
            {
                return false;
            }
        }

        reply = new OhscrcpyReply(data, replyType, videoPayload);
        return true;
    }

    internal static bool TryParseEndResult(ReadOnlySpan<byte> payload, out int result)
    {
        result = 0;
        var offset = 0;
        while (offset < payload.Length)
        {
            if (!TryReadTag(payload, ref offset, out var field, out var wireType)) return false;
            if (field == 1 && wireType == 0)
            {
                if (!TryReadVarint(payload, ref offset, out var value) || value > int.MaxValue) return false;
                result = (int)value;
                continue;
            }

            if (!TrySkip(payload, ref offset, wireType)) return false;
        }

        return true;
    }

    private static bool TryParsePayloadEntry(
        ReadOnlySpan<byte> entry, out string key, out byte[]? value)
    {
        key = string.Empty;
        value = null;
        var foundKey = false;
        var offset = 0;
        while (offset < entry.Length)
        {
            if (!TryReadTag(entry, ref offset, out var field, out var wireType)) return false;
            if (field == 1 && wireType == 2)
            {
                if (!TryReadLengthDelimited(entry, ref offset, out var slice)) return false;
                if (slice.Length > MaximumDiagnosticTextLength) return false;
                key = Encoding.UTF8.GetString(slice);
                foundKey = true;
            }
            else if (field == 2 && wireType == 2)
            {
                if (!TryReadLengthDelimited(entry, ref offset, out var paramValue)) return false;
                value = ParseParamValueBytes(paramValue);
            }
            else if (!TrySkip(entry, ref offset, wireType))
            {
                return false;
            }
        }

        return foundKey;
    }

    private static byte[]? ParseParamValueBytes(ReadOnlySpan<byte> payload)
    {
        byte[]? value = null;
        var offset = 0;
        while (offset < payload.Length)
        {
            if (!TryReadTag(payload, ref offset, out var field, out var wireType)) return null;
            if (field == 5 && wireType == 2)
            {
                if (!TryReadLengthDelimited(payload, ref offset, out var slice)) return null;
                value = slice.ToArray();
            }
            else if (!TrySkip(payload, ref offset, wireType))
            {
                return null;
            }
        }

        return value;
    }

    private static bool TryReadTag(ReadOnlySpan<byte> data, ref int offset, out int field, out int wireType)
    {
        field = 0;
        wireType = 0;
        if (!TryReadVarint(data, ref offset, out var tag) || tag == 0) return false;
        field = (int)(tag >> 3);
        wireType = (int)(tag & 7);
        return field > 0 && wireType <= 5;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= data.Length) return false;
            var current = data[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return true;
        }

        return false;
    }

    private static bool TryReadLengthDelimited(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!TryReadVarint(data, ref offset, out var length) || length > (ulong)(data.Length - offset)) return false;
        slice = data.Slice(offset, (int)length);
        offset += (int)length;
        return true;
    }

    private static bool TrySkip(ReadOnlySpan<byte> data, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                return TryReadVarint(data, ref offset, out _);
            case 1 when offset + 8 <= data.Length:
                offset += 8;
                return true;
            case 2:
                return TryReadLengthDelimited(data, ref offset, out _);
            case 5 when offset + 4 <= data.Length:
                offset += 4;
                return true;
            default:
                return false;
        }
    }
}