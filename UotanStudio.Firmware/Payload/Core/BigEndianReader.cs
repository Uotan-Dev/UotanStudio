using System.Buffers.Binary;

namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Reads primitive values from a stream in big-endian byte order.
/// <para>以大端字节序从流中读取原始值。</para>
/// </summary>
internal static class BigEndianReader
{
    /// <summary>
    /// Reads a 64-bit unsigned integer from a stream in big-endian byte order.
    /// <para>从流中以大端字节序读取64位无符号整数。</para>
    /// </summary>
    /// <param name="stream">The stream to read from.
    /// <para>要读取的流。</para>
    /// </param>
    /// <returns>The 64-bit unsigned integer read from the stream.
    /// <para>从流中读取的64位无符号整数。</para>
    /// </returns>
    /// <exception cref="EndOfStreamException">Thrown when the stream does not contain enough bytes to read a 64-bit value.
    /// <para>当流中没有足够的字节来读取64位值时抛出。</para>
    /// </exception>
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
    /// <param name="stream">The stream to read from.
    /// <para>要读取的流。</para>
    /// </param>
    /// <returns>The 32-bit unsigned integer read from the stream.
    /// <para>从流中读取的32位无符号整数。</para>
    /// </returns>
    /// <exception cref="EndOfStreamException">Thrown when the stream does not contain enough bytes to read a 32-bit value.
    /// <para>当流中没有足够的字节来读取32位值时抛出。</para>
    /// </exception>
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
    /// <param name="stream">The stream to read from.
    /// <para>要读取的流。</para>
    /// </param>
    /// <returns>A task that represents the asynchronous read operation. The task result contains the 64-bit unsigned integer read from the stream.
    /// <para>表示异步读取操作的任务。任务结果包含从流中读取的64位无符号整数。</para>
    /// </returns>
    /// <exception cref="EndOfStreamException">Thrown when the stream does not contain enough bytes to read a 64-bit value.
    /// <para>当流中没有足够的字节来读取64位值时抛出。</para>
    /// </exception>
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
    /// <param name="stream">The stream to read from.
    /// <para>要读取的流。</para>
    /// </param>
    /// <returns>A task that represents the asynchronous read operation. The task result contains the 32-bit unsigned integer read from the stream.
    /// <para>表示异步读取操作的任务。任务结果包含从流中读取的32位无符号整数。</para>
    /// </returns>
    /// <exception cref="EndOfStreamException">Thrown when the stream does not contain enough bytes to read a 32-bit value.
    /// <para>当流中没有足够的字节来读取32位值时抛出。</para>
    /// </exception>
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
    /// <param name="buffer">The byte array to read from.
    /// <para>要读取的字节数组。</para>
    /// </param>
    /// <param name="offset">The zero-based byte offset in the buffer at which to start reading.
    /// <para>缓冲区中开始读取的从零开始的字节偏移量。</para>
    /// </param>
    /// <returns>The 64-bit unsigned integer read from the buffer.
    /// <para>从缓冲区中读取的64位无符号整数。</para>
    /// </returns>
    internal static ulong ReadUInt64BigEndian(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(offset, 8));

    /// <summary>
    /// Reads a 32-bit unsigned integer from a byte array at the specified offset in big-endian byte order.
    /// <para>从字节数组的指定偏移量处以大端字节序读取32位无符号整数。</para>
    /// </summary>
    /// <param name="buffer">The byte array to read from.
    /// <para>要读取的字节数组。</para>
    /// </param>
    /// <param name="offset">The zero-based byte offset in the buffer at which to start reading.
    /// <para>缓冲区中开始读取的从零开始的字节偏移量。</para>
    /// </param>
    /// <returns>The 32-bit unsigned integer read from the buffer.
    /// <para>从缓冲区中读取的32位无符号整数。</para>
    /// </returns>
    internal static uint ReadUInt32BigEndian(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
}