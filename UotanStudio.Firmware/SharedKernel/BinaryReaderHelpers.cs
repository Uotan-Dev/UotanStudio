using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.SharedKernel;

public static class BinaryReaderHelpers
{
    /// <summary>
    /// Reads a big-endian UInt16.
    /// <para>读取大端 UInt16。</para>
    /// </summary>
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