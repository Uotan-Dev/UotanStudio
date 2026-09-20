using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.IO;

internal static class BigEndianReader
{
    /// <summary>
    /// Reads a 64-bit unsigned integer from a stream in big-endian byte order.
    /// <para>从流中以大端字节序读取64位无符号整数。</para>
    /// </summary>
    internal static ulong ReadUInt64BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer from a stream in big-endian byte order.
    /// <para>从流中以大端字节序读取32位无符号整数。</para>
    /// </summary>
    internal static uint ReadUInt32BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    /// <summary>
    /// Asynchronously reads a 64-bit unsigned integer from a stream in big-endian byte order.
    /// <para>从流中以大端字节序异步读取64位无符号整数。</para>
    /// </summary>
    internal static async Task<ulong> ReadUInt64BigEndianAsync(Stream stream)
    {
        var buffer = new byte[8];
        await stream.ReadExactlyAsync(buffer).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    /// <summary>
    /// Asynchronously reads a 32-bit unsigned integer from a stream in big-endian byte order.
    /// <para>从流中以大端字节序异步读取32位无符号整数。</para>
    /// </summary>
    internal static async Task<uint> ReadUInt32BigEndianAsync(Stream stream)
    {
        var buffer = new byte[4];
        await stream.ReadExactlyAsync(buffer).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    /// <summary>
    /// Reads a 64-bit unsigned integer from a byte array at the specified offset in big-endian byte order.
    /// <para>从字节数组的指定偏移量处以大端字节序读取64位无符号整数。</para>
    /// </summary>
    internal static ulong ReadUInt64BigEndian(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(offset, 8));

    /// <summary>
    /// Reads a 32-bit unsigned integer from a byte array at the specified offset in big-endian byte order.
    /// <para>从字节数组的指定偏移量处以大端字节序读取32位无符号整数。</para>
    /// </summary>
    internal static uint ReadUInt32BigEndian(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
}