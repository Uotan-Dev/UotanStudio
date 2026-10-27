using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Diag;

/// <summary>
/// Encodes and decodes the classic DIAG NV request/response payloads: the request carries the item
/// id plus up to 128 data bytes, and the response carries a status code plus the item id and data.
/// <para>经典 DIAG NV 请求/响应负载的编解码：请求携带条目号与至多 128 字节数据，响应携带状态码、
/// 条目号与数据。</para>
/// </summary>
public static class DiagNvPacket
{
    /// <summary>
    /// Encodes an NV read request payload: item id then 128 reserved data bytes.
    /// <para>编码 NV 读取请求负载：条目号后跟 128 字节保留数据区。</para>
    /// </summary>
    /// <param name="itemId">The NV item id. <para>NV 条目号。</para></param>
    /// <returns>The request payload starting with the item id. <para>以条目号开头的请求负载。</para></returns>
    public static byte[] EncodeReadRequest(ushort itemId)
    {
        var payload = new byte[2 + DiagConstants.MaxNvDataBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, itemId);
        return payload;
    }

    /// <summary>
    /// Encodes an NV write request payload: item id then the data, zero-padded to 128 bytes.
    /// <para>编码 NV 写入请求负载：条目号后跟数据，补零到 128 字节。</para>
    /// </summary>
    /// <param name="itemId">The NV item id. <para>NV 条目号。</para></param>
    /// <param name="data">The item data; at most 128 bytes. <para>条目数据；至多 128 字节。</para></param>
    /// <returns>The request payload starting with the item id. <para>以条目号开头的请求负载。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The data exceeds 128 bytes.
    /// <para>数据超过 128 字节。</para></exception>
    public static byte[] EncodeWriteRequest(ushort itemId, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, DiagConstants.MaxNvDataBytes);
        var payload = new byte[2 + DiagConstants.MaxNvDataBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, itemId);
        data.CopyTo(payload.AsSpan(2));
        return payload;
    }

    /// <summary>
    /// Decodes an NV read response payload: status (2 bytes), item id (2 bytes), data (up to 128).
    /// <para>解码 NV 读取响应负载：状态（2 字节）、条目号（2 字节）、数据（至多 128 字节）。</para>
    /// </summary>
    /// <param name="payload">The response payload after the command byte.
    /// <para>命令字节之后的响应负载。</para></param>
    /// <returns>The decoded status, item id and data. <para>解码出的状态、条目号与数据。</para></returns>
    /// <exception cref="InvalidDataException">The payload is shorter than the fixed header.
    /// <para>负载短于固定头。</para></exception>
    public static (ushort Status, ushort ItemId, byte[] Data) DecodeReadResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            throw new InvalidDataException("The NV read response is shorter than its fixed header.");
        }

        var status = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var itemId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2));
        var data = payload.Slice(4).ToArray();
        return (status, itemId, data);
    }

    /// <summary>
    /// Decodes an NV write response payload: status (2 bytes) then item id (2 bytes).
    /// <para>解码 NV 写入响应负载：状态（2 字节）与条目号（2 字节）。</para>
    /// </summary>
    /// <param name="payload">The response payload after the command byte.
    /// <para>命令字节之后的响应负载。</para></param>
    /// <returns>The decoded status and item id. <para>解码出的状态与条目号。</para></returns>
    /// <exception cref="InvalidDataException">The payload is shorter than 4 bytes.
    /// <para>负载短于 4 字节。</para></exception>
    public static (ushort Status, ushort ItemId) DecodeWriteResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            throw new InvalidDataException("The NV write response is shorter than its fixed header.");
        }

        return (BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2)));
    }
}