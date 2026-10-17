using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Core;

/// <summary>
/// Endian and ASCII primitive readers shared by firmware parsers, surfaced as extension members on
/// <see cref="Stream"/> and <see cref="T:byte[]"/>; each read consumes the stream in place and throws on
/// truncation instead of returning partial data.
/// <para>固件解析器共用的字节序与 ASCII 基础读取助手，以扩展成员形式挂接到 <see cref="Stream"/> 与 <see cref="T:byte[]"/>；每次读取都就地消耗流，遇到截断即抛出异常而非返回部分数据。</para>
/// </summary>
public static class BinaryReaderHelpers
{
    /// <summary>
    /// Endian and ASCII primitive readers on a readable stream.
    /// <para>面向可读流的字节序与 ASCII 基础读取助手。</para>
    /// </summary>
    extension(Stream stream)
    {
        /// <summary>
        /// Reads a big-endian UInt16.
        /// <para>读取大端 UInt16。</para>
        /// </summary>
        /// <returns>The 16-bit value decoded from two bytes. <para>由两个字节解码得到的 16 位值。</para></returns>
        /// <exception cref="EndOfStreamException">Fewer than two bytes remain. <para>剩余字节不足两个。</para></exception>
        /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
        [System.Runtime.CompilerServices.SkipLocalsInit]
        public ushort ReadUInt16BE()
        {
            Span<byte> buffer = stackalloc byte[2];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt16BigEndian(buffer);
        }

        /// <summary>
        /// Reads a big-endian UInt32.
        /// <para>读取大端 UInt32。</para>
        /// </summary>
        /// <returns>The 32-bit value decoded from four bytes. <para>由四个字节解码得到的 32 位值。</para></returns>
        /// <exception cref="EndOfStreamException">Fewer than four bytes remain. <para>剩余字节不足四个。</para></exception>
        /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
        [System.Runtime.CompilerServices.SkipLocalsInit]
        public uint ReadUInt32BE()
        {
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt32BigEndian(buffer);
        }

        /// <summary>
        /// Reads a big-endian UInt64.
        /// <para>读取大端 UInt64。</para>
        /// </summary>
        /// <returns>The 64-bit value decoded from eight bytes. <para>由八个字节解码得到的 64 位值。</para></returns>
        /// <exception cref="EndOfStreamException">Fewer than eight bytes remain. <para>剩余字节不足八个。</para></exception>
        /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
        [System.Runtime.CompilerServices.SkipLocalsInit]
        public ulong ReadUInt64BE()
        {
            Span<byte> buffer = stackalloc byte[8];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt64BigEndian(buffer);
        }

        /// <summary>
        /// Reads a little-endian UInt16.
        /// <para>读取小端 UInt16。</para>
        /// </summary>
        /// <returns>The 16-bit value decoded from two bytes. <para>由两个字节解码得到的 16 位值。</para></returns>
        /// <exception cref="EndOfStreamException">Fewer than two bytes remain. <para>剩余字节不足两个。</para></exception>
        /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
        [System.Runtime.CompilerServices.SkipLocalsInit]
        public ushort ReadUInt16LE()
        {
            Span<byte> buffer = stackalloc byte[2];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        }

        /// <summary>
        /// Reads a little-endian UInt32.
        /// <para>读取小端 UInt32。</para>
        /// </summary>
        /// <returns>The 32-bit value decoded from four bytes. <para>由四个字节解码得到的 32 位值。</para></returns>
        /// <exception cref="EndOfStreamException">Fewer than four bytes remain. <para>剩余字节不足四个。</para></exception>
        /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
        [System.Runtime.CompilerServices.SkipLocalsInit]
        public uint ReadUInt32LE()
        {
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        }

        /// <summary>
        /// Reads a little-endian UInt64.
        /// <para>读取小端 UInt64。</para>
        /// </summary>
        /// <returns>The 64-bit value decoded from eight bytes. <para>由八个字节解码得到的 64 位值。</para></returns>
        /// <exception cref="EndOfStreamException">Fewer than eight bytes remain. <para>剩余字节不足八个。</para></exception>
        /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
        [System.Runtime.CompilerServices.SkipLocalsInit]
        public ulong ReadUInt64LE()
        {
            Span<byte> buffer = stackalloc byte[8];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        }

        /// <summary>
        /// Asynchronously reads a big-endian UInt32.
        /// <para>异步读取大端 UInt32。</para>
        /// </summary>
        /// <param name="cancellationToken">Token that cancels the read. <para>取消读取的令牌。</para></param>
        /// <returns>A task producing the 32-bit value decoded from four bytes. <para>产出由四个字节解码得到的 32 位值的任务。</para></returns>
        /// <exception cref="EndOfStreamException">Fewer than four bytes remain. <para>剩余字节不足四个。</para></exception>
        public async Task<uint> ReadUInt32BEAsync(CancellationToken cancellationToken = default)
        {
            var buffer = new byte[4];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return BinaryPrimitives.ReadUInt32BigEndian(buffer);
        }

        /// <summary>
        /// Asynchronously reads a big-endian UInt64.
        /// <para>异步读取大端 UInt64。</para>
        /// </summary>
        /// <param name="cancellationToken">Token that cancels the read. <para>取消读取的令牌。</para></param>
        /// <returns>A task producing the 64-bit value decoded from eight bytes. <para>产出由八个字节解码得到的 64 位值的任务。</para></returns>
        /// <exception cref="EndOfStreamException">Fewer than eight bytes remain. <para>剩余字节不足八个。</para></exception>
        public async Task<ulong> ReadUInt64BEAsync(CancellationToken cancellationToken = default)
        {
            var buffer = new byte[8];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return BinaryPrimitives.ReadUInt64BigEndian(buffer);
        }

        /// <summary>
        /// Reads an ASCII string of the given length, trimmed of NUL padding.
        /// <para>读取定长 ASCII 字符串并去除 NUL 填充。</para>
        /// </summary>
        /// <param name="length">Field width in bytes; the whole field is consumed even when the string ends earlier. <para>字段宽度（字节）；即使字符串提前结束，仍会消耗整个字段。</para></param>
        /// <returns>
        /// The bytes before the first NUL decoded as ASCII, or the empty string when the field starts with NUL or <paramref name="length"/> is <c>0</c>.
        /// <para>以 ASCII 解码的首个 NUL 之前的内容；字段以 NUL 开头或 <paramref name="length"/> 为 <c>0</c> 时返回空字符串。</para>
        /// </returns>
        /// <exception cref="EndOfStreamException">Fewer than <paramref name="length"/> bytes remain. <para>剩余字节少于 <paramref name="length"/>。</para></exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative. <para><paramref name="length"/> 为负数。</para></exception>
        public string ReadAscii(int length)
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
        /// <param name="endMarker">Byte sequence that terminates the payload; the marker itself is not returned. <para>终止载荷的字节序列；标记本身不计入返回值。</para></param>
        /// <param name="maxBytes">Hard cap on bytes read, so untrusted input cannot grow the buffer without bound. <para>读取字节的硬性上限，防止不可信输入无界增长缓冲区。</para></param>
        /// <returns>
        /// Payload up to the marker with trailing NUL bytes removed; when the stream ends or <paramref name="maxBytes"/> is reached first,
        /// everything read so far is returned without trimming.
        /// <para>返回标记之前且去除末尾 NUL 的载荷；若先行到达流末尾或触及 <paramref name="maxBytes"/>，则原样返回已读取的全部字节。</para>
        /// </returns>
        /// <exception cref="NotSupportedException">The stream does not support reading. <para>流不支持读取。</para></exception>
        public byte[] ReadUntil(ReadOnlySpan<byte> endMarker, int maxBytes = 1 << 20)
        {
            if (maxBytes <= 0)
            {
                return [];
            }

            if (endMarker.IsEmpty)
            {
                var single = stream.ReadByte();
                if (single < 0)
                {
                    return [];
                }

                var one = new[] { (byte)single };
                var oneEnd = one.Length;
                while (oneEnd > 0 && one[oneEnd - 1] == 0)
                {
                    oneEnd--;
                }

                return one.AsSpan(0, oneEnd).ToArray();
            }

            var markerLength = endMarker.Length;
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxBytes, 8192));
            using var ms = new MemoryStream();
            var scanned = 0;
            try
            {
                while (ms.Length < maxBytes)
                {
                    var want = (int)Math.Min(buffer.Length, maxBytes - ms.Length);
                    var read = stream.Read(buffer, 0, want);
                    if (read <= 0)
                    {
                        break;
                    }

                    var chunkStart = (int)ms.Length;
                    ms.Write(buffer, 0, read);
                    var total = chunkStart + read;
                    var source = ms.GetBuffer();
                    var scanFrom = Math.Max(chunkStart - markerLength + 1, scanned);
                    for (var start = scanFrom; start + markerLength <= total; start++)
                    {
                        if (source.AsSpan(start, markerLength).SequenceEqual(endMarker))
                        {
                            var payload = source.AsSpan(0, start);
                            var length = payload.LastIndexOfAnyExcept((byte)0) + 1;
                            return payload[..length].ToArray();
                        }
                    }

                    scanned = Math.Max(scanned, total - markerLength + 1);
                }

                return ms.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Big-endian and little-endian field readers over a buffer at an explicit offset; kept as classic
    /// extension methods because the public API analyzer cannot match array-receiver extension blocks.
    /// <para>基于缓冲区与显式偏移的大端、小端字段读取助手；保持经典扩展方法形式，因为公共 API 分析器无法匹配数组接收者的扩展块。</para>
    /// </summary>
    /// <param name="buffer">Buffer holding the field. <para>存放该字段的缓冲区。</para></param>
    /// <param name="offset">Index of the first field byte. <para>字段首字节的索引。</para></param>
    /// <returns>The 32-bit value decoded from four bytes. <para>由四个字节解码得到的 32 位值。</para></returns>
    public static uint ReadUInt32BE(this byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));

    /// <summary>
    /// Reads a big-endian UInt64 at the given offset.
    /// <para>从指定偏移处读取大端 UInt64。</para>
    /// </summary>
    /// <param name="buffer">Buffer holding the field. <para>存放该字段的缓冲区。</para></param>
    /// <param name="offset">Index of the first field byte. <para>字段首字节的索引。</para></param>
    /// <returns>The 64-bit value decoded from eight bytes. <para>由八个字节解码得到的 64 位值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than eight bytes remain after <paramref name="offset"/>. <para><paramref name="offset"/> 之后剩余字节不足八个。</para></exception>
    public static ulong ReadUInt64BE(this byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(offset, 8));

    /// <summary>
    /// Reads a little-endian UInt16 at the given offset.
    /// <para>从指定偏移处读取小端 UInt16。</para>
    /// </summary>
    /// <param name="buffer">Buffer holding the field. <para>存放该字段的缓冲区。</para></param>
    /// <param name="offset">Index of the first field byte. <para>字段首字节的索引。</para></param>
    /// <returns>The 16-bit value decoded from two bytes. <para>由两个字节解码得到的 16 位值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than two bytes remain after <paramref name="offset"/>. <para><paramref name="offset"/> 之后剩余字节不足两个。</para></exception>
    public static ushort ReadUInt16LE(this byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));

    /// <summary>
    /// Reads a little-endian UInt32 at the given offset.
    /// <para>从指定偏移处读取小端 UInt32。</para>
    /// </summary>
    /// <param name="buffer">Buffer holding the field. <para>存放该字段的缓冲区。</para></param>
    /// <param name="offset">Index of the first field byte. <para>字段首字节的索引。</para></param>
    /// <returns>The 32-bit value decoded from four bytes. <para>由四个字节解码得到的 32 位值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than four bytes remain after <paramref name="offset"/>. <para><paramref name="offset"/> 之后剩余字节不足四个。</para></exception>
    public static uint ReadUInt32LE(this byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));

    /// <summary>
    /// Reads a little-endian UInt64 at the given offset.
    /// <para>从指定偏移处读取小端 UInt64。</para>
    /// </summary>
    /// <param name="buffer">Buffer holding the field. <para>存放该字段的缓冲区。</para></param>
    /// <param name="offset">Index of the first field byte. <para>字段首字节的索引。</para></param>
    /// <returns>The 64-bit value decoded from eight bytes. <para>由八个字节解码得到的 64 位值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than eight bytes remain after <paramref name="offset"/>. <para><paramref name="offset"/> 之后剩余字节不足八个。</para></exception>
    public static ulong ReadUInt64LE(this byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));
}