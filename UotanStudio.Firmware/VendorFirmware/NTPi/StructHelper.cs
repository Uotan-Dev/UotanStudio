using System.Buffers.Binary;
using UotanStudio.Firmware.VendorFirmware.NTPi.Models;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.VendorFirmware.NTPi;

internal static class StructHelper
{
    /// <summary>
    /// Reads an NtpiHeader (48 bytes) from a byte span.
    /// <para>从字节跨度读取 NtpiHeader(48 字节)。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes starting at the record; at least 48 bytes are required and trailing bytes beyond the record are ignored.
    /// <para>从记录起始处提供的字节；至少需要 48 字节，记录之后的多余字节会被忽略。</para>
    /// </param>
    /// <returns>
    /// A header carrying the raw magic bytes, the padding word, the three version components and the first region
    /// header; the magic itself is not checked.
    /// <para>携带原始魔数字节、填充字、三个版本分量与首个区域头的头部；魔数本身未被校验。</para>
    /// </returns>
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
    /// <param name="data">
    /// Bytes starting at the record; at least 40 bytes are required and trailing bytes beyond the record are ignored.
    /// <para>从记录起始处提供的字节；至少需要 40 字节，记录之后的多余字节会被忽略。</para>
    /// </param>
    /// <returns>
    /// A block header whose ThisHeader describes the current region, NextHeader the following region in the chain and
    /// RealSize the exact decrypted payload length.
    /// <para>返回的块头中 ThisHeader 描述当前区域，NextHeader 描述链中的下一个区域，RealSize 为解密后载荷的精确长度。</para>
    /// </returns>
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
    /// <param name="data">
    /// Bytes starting at the record; at least 112 bytes are required and trailing bytes beyond the record are ignored.
    /// The declared key and IV fields are copied out as fresh arrays of 32 bytes each.
    /// <para>从记录起始处提供的字节；至少需要 112 字节，记录之后的多余字节会被忽略。声明的密钥与 IV 字段被复制为各 32 字节的新数组。</para>
    /// </param>
    /// <returns>
    /// A header carrying the 8-byte magic, the codec selector fields, the reserved padding word, the two size fields,
    /// the key and IV arrays and the declared key and IV lengths; the magic is not checked.
    /// <para>返回的头部携带 8 字节魔数、编解码选择字段、保留填充字、两个长度字段、密钥与 IV 数组以及声明的密钥和 IV 长度；魔数未被校验。</para>
    /// </returns>
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
    /// <param name="data">
    /// Bytes starting at the record; a span shorter than 8 bytes never matches.
    /// <para>从记录起始处提供的字节；短于 8 字节的跨度始终不匹配。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the span begins with the ASCII magic "NTENCODE".
    /// <para>跨度以 ASCII 魔数 "NTENCODE" 开头时返回 <see langword="true"/>。</para>
    /// </returns>
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
    /// <param name="data">
    /// Buffer holding the record; <see langword="null"/> reports no match instead of throwing.
    /// <para>容纳记录的缓冲区；为 <see langword="null"/> 时报告不匹配而不抛出异常。</para>
    /// </param>
    /// <param name="offset">
    /// Index in <paramref name="data"/> of the first magic byte; a negative index or one leaving fewer
    /// than 8 bytes reports no match.
    /// <para><paramref name="data"/> 中魔数首字节的索引；索引为负或剩余不足 8 字节时报告不匹配。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the eight bytes at <paramref name="offset"/> are the ASCII magic
    /// "NTENCODE". <para><paramref name="offset"/> 处的 8 字节为 ASCII 魔数 "NTENCODE" 时返回 <see langword="true"/>。</para>
    /// </returns>
    public static bool IsNtEncodeMagic(byte[] data, int offset = 0) => data is not null && offset >= 0 && offset + 8 <= data.Length && IsNtEncodeMagic(data.AsSpan(offset, 8));

    /// <summary>
    /// Reads the OriginalSize field of NtEncodeHeader (offset 32).
    /// <para>从字节跨度读取 NtEncodeHeader 的 OriginalSize 字段(偏移 32)。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes starting at the record; at least 40 bytes are required because the field occupies offsets 32..39.
    /// <para>从记录起始处提供的字节；该字段占据偏移 32..39，因此至少需要 40 字节。</para>
    /// </param>
    /// <returns>
    /// The little-endian unsigned original size stored at offset 32.
    /// <para>偏移 32 处按小端存放的无符号原始长度。</para>
    /// </returns>
    public static ulong ReadNtEncodeOriginalSize(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);

    /// <summary>
    /// Reads the OriginalSize field of NtEncodeHeader from a byte array at the given offset.
    /// <para>从字节数组指定偏移读取 NtEncodeHeader 的 OriginalSize 字段。</para>
    /// </summary>
    /// <param name="data">
    /// Buffer holding the record; the field is read from <paramref name="offset"/> plus 32.
    /// <para>容纳记录的缓冲区；字段取自 <paramref name="offset"/> 加 32 处。</para>
    /// </param>
    /// <param name="offset">
    /// Index in <paramref name="data"/> of the start of the record; an offset leaving fewer than 40 bytes
    /// for the field fails the read.
    /// <para><paramref name="data"/> 中记录的起始索引；留下的字节不足 40 时读取失败。</para>
    /// </param>
    /// <returns>
    /// The little-endian unsigned original size stored at offset 32 of the record.
    /// <para>记录偏移 32 处按小端存放的无符号原始长度。</para>
    /// </returns>
    public static ulong ReadNtEncodeOriginalSize(byte[] data, int offset = 0) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 32));

    /// <summary>
    /// Reads an NtDecompressHeader (112 bytes) from a byte span.
    /// <para>从字节跨度读取 NtDecompressHeader(112 字节)。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes starting at the record; at least 112 bytes are required and trailing bytes beyond the record are ignored.
    /// The 72 reserved bytes at offsets 40..112 are copied out as a fresh array.
    /// <para>从记录起始处提供的字节；至少需要 112 字节，记录之后的多余字节会被忽略。偏移 40..112 的 72 个保留字节被复制为新数组。</para>
    /// </param>
    /// <returns>
    /// A header carrying the 8-byte magic, the primary type and decompression subtype, the reserved 8-byte field at
    /// offset 16, the processed and original sizes, and the reserved tail; the magic is not checked.
    /// <para>返回的头部携带 8 字节魔数、主类型与解压子类型、偏移 16 处的 8 字节保留字段、已处理与原始长度以及保留尾部；魔数未被校验。</para>
    /// </returns>
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
    /// <param name="header">
    /// Header whose magic, padding, version triple and first region header are serialized.
    /// <para>要序列化魔数、填充、版本三元组与首个区域头的头部。</para>
    /// </param>
    /// <param name="destination">
    /// Target span starting at the record position; 48 bytes are written and any remaining capacity is left untouched.
    /// <para>从记录位置起始的目标跨度；写入 48 字节，其后容量保持不变。</para>
    /// </param>
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
    /// <param name="header">
    /// Header whose current region, next region and real size are serialized.
    /// <para>要序列化当前区域、下一区域与实际载荷长度的块头。</para>
    /// </param>
    /// <param name="destination">
    /// Target span starting at the record position; 40 bytes are written and any remaining capacity is left untouched.
    /// <para>从记录位置起始的目标跨度；写入 40 字节，其后容量保持不变。</para>
    /// </param>
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
    /// <param name="header">
    /// Header whose magic, codec selectors, padding, sizes, key and IV fields and declared lengths are serialized; the
    /// arrays are copied as stored and are not resized to their record widths.
    /// <para>要序列化魔数、编解码选择字段、填充、长度、密钥与 IV 字段以及声明长度的头部；数组按现有长度复制，不会补齐到记录宽度。</para>
    /// </param>
    /// <param name="destination">
    /// Target span starting at the record position; 112 bytes are written and any remaining capacity is left untouched.
    /// <para>从记录位置起始的目标跨度；写入 112 字节，其后容量保持不变。</para>
    /// </param>
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
    /// <param name="header">
    /// Header whose magic, primary type, decompression subtype, reserved word, sizes and reserved tail are serialized;
    /// the tail array is copied as stored and is not resized to its 72-byte record width.
    /// <para>要序列化魔数、主类型、解压子类型、保留字段、长度与保留尾部的头部；尾部数组按现有长度复制，不会补齐到 72 字节的记录宽度。</para>
    /// </param>
    /// <param name="destination">
    /// Target span starting at the record position; 112 bytes are written and any remaining capacity is left untouched.
    /// <para>从记录位置起始的目标跨度；写入 112 字节，其后容量保持不变。</para>
    /// </param>
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
    /// <param name="data">
    /// Bytes to hash; an empty span hashes as zero input bytes.
    /// <para>参与哈希的字节；空跨度按零字节输入计算。</para>
    /// </param>
    /// <returns>
    /// The 64-character lowercase hexadecimal digest, without separators.
    /// <para>64 个字符的小写十六进制摘要，不含分隔符。</para>
    /// </returns>
    public static string ComputeSha256Hex(ReadOnlySpan<byte> data) => Sha256.ComputeHexLower(data);
}