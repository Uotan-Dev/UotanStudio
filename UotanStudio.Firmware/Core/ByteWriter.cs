using System.Buffers.Binary;

namespace UotanStudio.Firmware.Core;

/// <summary>
/// Explicit-endianness fixed-width byte writers over <see cref="BinaryPrimitives"/>. The read-side
/// counterpart is <see cref="BinaryReaderHelpers"/>; both centralize the little/big-endian byte
/// order of every format so callers never depend on CPU endianness.
/// <para>基于 <see cref="BinaryPrimitives"/> 的显式端序定宽字节写入器。读取侧对应 <see cref="BinaryReaderHelpers"/>；
/// 两者集中各格式的读写端序，使调用方不依赖 CPU 端序。</para>
/// </summary>
internal static class ByteWriter
{
    /// <summary>Writes a little-endian <see cref="ushort"/> into the start of <paramref name="destination"/>. 以小端序写入 ushort。</summary>
    internal static void WriteUInt16LE(Span<byte> destination, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(destination, value);

    /// <summary>Writes a little-endian <see cref="uint"/> into the start of <paramref name="destination"/>. 以小端序写入 uint。</summary>
    internal static void WriteUInt32LE(Span<byte> destination, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(destination, value);

    /// <summary>Writes a big-endian <see cref="ushort"/> into the start of <paramref name="destination"/>. 以大端序写入 ushort。</summary>
    internal static void WriteUInt16BE(Span<byte> destination, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(destination, value);

    /// <summary>Writes a big-endian <see cref="uint"/> into the start of <paramref name="destination"/>. 以大端序写入 uint。</summary>
    internal static void WriteUInt32BE(Span<byte> destination, uint value) => BinaryPrimitives.WriteUInt32BigEndian(destination, value);

    /// <summary>Writes a little-endian <see cref="ushort"/> at <paramref name="offset"/> in <paramref name="buffer"/>. 在缓冲区偏移处小端写入 ushort。</summary>
    internal static void WriteUInt16LE(byte[] buffer, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), value);

    /// <summary>Writes a little-endian <see cref="uint"/> at <paramref name="offset"/> in <paramref name="buffer"/>. 在缓冲区偏移处小端写入 uint。</summary>
    internal static void WriteUInt32LE(byte[] buffer, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    /// <summary>Writes a big-endian <see cref="uint"/> at <paramref name="offset"/> in <paramref name="buffer"/>. 在缓冲区偏移处大端写入 uint。</summary>
    internal static void WriteUInt32BE(byte[] buffer, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);

    /// <summary>Returns the little-endian 4-byte representation of <paramref name="value"/>. 返回 value 的小端 4 字节表示。</summary>
    internal static byte[] ToUInt32LE(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return buffer;
    }
}