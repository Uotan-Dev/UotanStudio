using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Helpers;

internal static class StructHelper
{
    /// <summary>
    /// Reads an NtpiHeader (48 bytes) from a byte span.
    /// <para>从字节跨度读取 NtpiHeader(48 字节)。</para>
    /// </summary>
    public static NtpiHeader ReadNtpiHeader(ReadOnlySpan<byte> data)
    {
        return new NtpiHeader
        {
            Magic = data[..4].ToArray(),
            Padding = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            VersionMajor = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            VersionMinor = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
            VersionPatch = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]),
            FirstRegionHeader = ReadRegionHeader(data[32..])
        };
    }

    /// <summary>
    /// Reads a RegionHeader (16 bytes) from a byte span.
    /// <para>从字节跨度读取 RegionHeader(16 字节)。</para>
    /// </summary>
    public static RegionHeader ReadRegionHeader(ReadOnlySpan<byte> data)
    {
        return new RegionHeader
        {
            RegionType = BinaryPrimitives.ReadUInt64LittleEndian(data),
            RegionSize = BinaryPrimitives.ReadUInt64LittleEndian(data[8..])
        };
    }

    /// <summary>
    /// Reads a RegionBlockHeader (40 bytes) from a byte span.
    /// <para>从字节跨度读取 RegionBlockHeader(40 字节)。</para>
    /// </summary>
    public static RegionBlockHeader ReadRegionBlockHeader(ReadOnlySpan<byte> data)
    {
        return new RegionBlockHeader
        {
            ThisHeader = ReadRegionHeader(data),
            NextHeader = ReadRegionHeader(data[16..]),
            RealSize = BinaryPrimitives.ReadUInt64LittleEndian(data[32..])
        };
    }

    /// <summary>
    /// Reads an NtEncodeHeader (112 bytes) from a byte span.
    /// <para>从字节跨度读取 NtEncodeHeader(112 字节)。</para>
    /// </summary>
    public static NtEncodeHeader ReadNtEncodeHeader(ReadOnlySpan<byte> data)
    {
        return new NtEncodeHeader
        {
            Magic = data[..8].ToArray(),
            PrimaryType = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            CompressSubtype = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            EncryptSubtype = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            Padding = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            ProcessedSize = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]),
            OriginalSize = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]),
            Key = data.Slice(40, 32).ToArray(),
            Iv = data.Slice(72, 32).ToArray(),
            KeySize = BinaryPrimitives.ReadUInt32LittleEndian(data[104..]),
            IvSize = BinaryPrimitives.ReadUInt32LittleEndian(data[108..])
        };
    }

    /// <summary>
    /// Validates whether the first 8 bytes are "NTENCODE".
    /// <para>验证字节跨度开头的 8 字节是否为 "NTENCODE"。</para>
    /// </summary>
    public static bool IsNtEncodeMagic(ReadOnlySpan<byte> data)
    {
        return data.Length >= 8
            && data[0] == (byte)'N' && data[1] == (byte)'T' && data[2] == (byte)'E' && data[3] == (byte)'N'
            && data[4] == (byte)'C' && data[5] == (byte)'O' && data[6] == (byte)'D' && data[7] == (byte)'E';
    }

    /// <summary>
    /// Validates whether the byte array at the given offset starts with "NTENCODE".
    /// <para>验证字节数组从指定偏移开始是否为 "NTENCODE"。</para>
    /// </summary>
    public static bool IsNtEncodeMagic(byte[] data, int offset = 0) => data is not null && offset >= 0 && offset + 8 <= data.Length && IsNtEncodeMagic(data.AsSpan(offset, 8));

    /// <summary>
    /// Reads the OriginalSize field of NtEncodeHeader (offset 32).
    /// <para>从字节跨度读取 NtEncodeHeader 的 OriginalSize 字段(偏移 32)。</para>
    /// </summary>
    public static ulong ReadNtEncodeOriginalSize(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);

    /// <summary>
    /// Reads the OriginalSize field of NtEncodeHeader from a byte array at the given offset.
    /// <para>从字节数组指定偏移读取 NtEncodeHeader 的 OriginalSize 字段。</para>
    /// </summary>
    public static ulong ReadNtEncodeOriginalSize(byte[] data, int offset = 0) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 32));

    /// <summary>
    /// Reads an NtDecompressHeader (112 bytes) from a byte span.
    /// <para>从字节跨度读取 NtDecompressHeader(112 字节)。</para>
    /// </summary>
    public static NtDecompressHeader ReadNtDecompressHeader(ReadOnlySpan<byte> data)
    {
        return new NtDecompressHeader
        {
            Magic = data[..8].ToArray(),
            PrimaryType = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            DecompressSubtype = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            Padding = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
            ProcessedSize = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]),
            OriginalSize = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]),
            Padding2 = data.Slice(40, 72).ToArray()
        };
    }

    /// <summary>
    /// Writes an NtpiHeader to a byte span.
    /// <para>将 NtpiHeader 写入字节跨度。</para>
    /// </summary>
    public static void WriteNtpiHeader(NtpiHeader header, Span<byte> destination)
    {
        header.Magic.AsSpan().CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], header.Padding);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], header.VersionMajor);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], header.VersionMinor);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], header.VersionPatch);
        WriteRegionHeader(header.FirstRegionHeader, destination[32..]);
    }

    /// <summary>
    /// Writes a RegionHeader to a byte span.
    /// <para>将 RegionHeader 写入字节跨度。</para>
    /// </summary>
    public static void WriteRegionHeader(RegionHeader header, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, header.RegionType);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], header.RegionSize);
    }

    /// <summary>
    /// Writes a RegionBlockHeader to a byte span.
    /// <para>将 RegionBlockHeader 写入字节跨度。</para>
    /// </summary>
    public static void WriteRegionBlockHeader(RegionBlockHeader header, Span<byte> destination)
    {
        WriteRegionHeader(header.ThisHeader, destination);
        WriteRegionHeader(header.NextHeader, destination[16..]);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], header.RealSize);
    }

    /// <summary>
    /// Writes an NtEncodeHeader to a byte span.
    /// <para>将 NtEncodeHeader 写入字节跨度。</para>
    /// </summary>
    public static void WriteNtEncodeHeader(NtEncodeHeader header, Span<byte> destination)
    {
        header.Magic.AsSpan().CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], header.PrimaryType);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], header.CompressSubtype);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], header.EncryptSubtype);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], header.Padding);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], header.ProcessedSize);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], header.OriginalSize);
        header.Key.AsSpan().CopyTo(destination[40..]);
        header.Iv.AsSpan().CopyTo(destination[72..]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[104..], header.KeySize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[108..], header.IvSize);
    }

    /// <summary>
    /// Writes an NtDecompressHeader to a byte span.
    /// <para>将 NtDecompressHeader 写入字节跨度。</para>
    /// </summary>
    public static void WriteNtDecompressHeader(NtDecompressHeader header, Span<byte> destination)
    {
        header.Magic.AsSpan().CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], header.PrimaryType);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], header.DecompressSubtype);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], header.Padding);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], header.ProcessedSize);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], header.OriginalSize);
        header.Padding2.AsSpan().CopyTo(destination[40..]);
    }

    /// <summary>
    /// Computes the SHA-256 hex string of the data.
    /// <para>计算数据的 SHA-256 十六进制字符串。</para>
    /// </summary>
    public static string ComputeSha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
