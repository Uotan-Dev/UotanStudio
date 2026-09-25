using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>
/// Big-endian and ASCII primitive readers shared by firmware parsers; each read consumes the stream in place.
/// <para>固件解析器共用的大端与 ASCII 基础读取助手；每次读取都就地消耗流。</para>
/// </summary>
public static class BinaryReaderHelpers
{
    /// <summary>
    /// Reads a big-endian UInt16.
    /// <para>读取大端 UInt16。</para>
    /// </summary>
    /// <param name="stream">Readable stream positioned at the field. <para>定位到该字段、可读的流。</para></param>
    /// <returns>The 16-bit value decoded from two bytes. <para>由两个字节解码得到的 16 位值。</para></returns>
    /// <exception cref="EndOfStreamException">Fewer than two bytes remain. <para>剩余字节不足两个。</para></exception>
    /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
    public static ushort ReadUInt16BE(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    /// <summary>
    /// Reads a big-endian UInt32.
    /// <para>读取大端 UInt32。</para>
    /// </summary>
    /// <param name="stream">Readable stream positioned at the field. <para>定位到该字段、可读的流。</para></param>
    /// <returns>The 32-bit value decoded from four bytes. <para>由四个字节解码得到的 32 位值。</para></returns>
    /// <exception cref="EndOfStreamException">Fewer than four bytes remain. <para>剩余字节不足四个。</para></exception>
    /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
    public static uint ReadUInt32BE(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    /// <summary>
    /// Reads a big-endian UInt64.
    /// <para>读取大端 UInt64。</para>
    /// </summary>
    /// <param name="stream">Readable stream positioned at the field. <para>定位到该字段、可读的流。</para></param>
    /// <returns>The 64-bit value decoded from eight bytes. <para>由八个字节解码得到的 64 位值。</para></returns>
    /// <exception cref="EndOfStreamException">Fewer than eight bytes remain. <para>剩余字节不足八个。</para></exception>
    /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
    public static ulong ReadUInt64BE(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    /// <summary>
    /// Reads an ASCII string of the given length, trimmed of NUL padding.
    /// <para>读取定长 ASCII 字符串并去除 NUL 填充。</para>
    /// </summary>
    /// <param name="stream">Readable stream positioned at the fixed-width field. <para>定位到定长字段、可读的流。</para></param>
    /// <param name="length">Field width in bytes; the whole field is consumed even when the string ends earlier. <para>字段宽度（字节）；即使字符串提前结束，仍会消耗整个字段。</para></param>
    /// <returns>
    /// The bytes before the first NUL decoded as ASCII, or the empty string when the field starts with NUL or <paramref name="length"/> is <c>0</c>.
    /// <para>以 ASCII 解码的首个 NUL 之前的内容；字段以 NUL 开头或 <paramref name="length"/> 为 <c>0</c> 时返回空字符串。</para>
    /// </returns>
    /// <exception cref="EndOfStreamException">Fewer than <paramref name="length"/> bytes remain. <para>剩余字节少于 <paramref name="length"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative. <para><paramref name="length"/> 为负数。</para></exception>
    public static string ReadAscii(Stream stream, int length)
    {
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        var end = Array.IndexOf(buffer, (byte)0);
        if (end < 0)
        {
            end = buffer.Length;
        }
        return Encoding.ASCII.GetString(buffer, 0, end);
    }

    /// <summary>
    /// Reads bytes until an end marker, returning the trimmed payload.
    /// <para>读取至结束标记,返回去除填充后的数据。</para>
    /// </summary>
    /// <param name="stream">Readable stream positioned at the payload. <para>定位到载荷、可读的流。</para></param>
    /// <param name="endMarker">Byte sequence that terminates the payload; the marker itself is not returned. <para>终止载荷的字节序列；标记本身不计入返回值。</para></param>
    /// <param name="maxBytes">Hard cap on bytes read, so untrusted input cannot grow the buffer without bound. <para>读取字节的硬性上限，防止不可信输入无界增长缓冲区。</para></param>
    /// <returns>
    /// Payload up to the marker with trailing NUL bytes removed; when the stream ends or <paramref name="maxBytes"/> is reached first,
    /// everything read so far is returned without trimming.
    /// <para>返回标记之前且去除末尾 NUL 的载荷；若先行到达流末尾或触及 <paramref name="maxBytes"/>，则原样返回已读取的全部字节。</para>
    /// </returns>
    /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
    public static byte[] ReadUntil(Stream stream, ReadOnlySpan<byte> endMarker, int maxBytes = 1 << 20)
    {
        using var ms = new MemoryStream();
        var window = new byte[endMarker.Length];
        var count = 0;
        while (count < maxBytes)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                break;
            }
            ms.WriteByte((byte)b);
            count++;
            if (count >= endMarker.Length)
            {

                var pos = ms.Position;
                ms.Position = pos - endMarker.Length;
                var tail = new byte[endMarker.Length];
                var n = ms.Read(tail, 0, endMarker.Length);
                ms.Position = pos;
                if (n == endMarker.Length && tail.AsSpan().SequenceEqual(endMarker))
                {

                    var bytes = ms.ToArray();
                    var end = bytes.Length - endMarker.Length;
                    while (end > 0 && bytes[end - 1] == 0)
                    {
                        end--;
                    }
                    return bytes.AsSpan(0, end).ToArray();
                }
            }
        }
        return ms.ToArray();
    }
}