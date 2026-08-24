using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Oppo.Models;

/// <summary>
/// <para>OFP QC 文件尾部页结构。</para>
/// OFP QC file tail page structure.
/// </summary>
internal sealed class OfpQcTailPage
{
    public const int Size = 0x200;
    public const uint Magic = 0x7CEF;

    public uint MagicValue { get; init; }
    public uint XmlSectorOffset { get; init; }
    public uint XmlLength { get; init; }
    public int PageSize { get; init; }

    /// <summary>
    /// <para>从二进制数据解析 OFP QC 尾部页结构。</para>
    /// Parses the OFP QC tail page structure from binary data.
    /// </summary>
    /// <param name="data"><para>包含尾部页数据的只读字节跨度。</para>Read-only byte span containing the tail page data.</param>
    /// <param name="pageSize"><para>页面大小（0x200 或 0x1000）。</para>Page size (0x200 or 0x1000).</param>
    /// <returns><para>解析后的尾部页结构。</para>The parsed tail page structure.</returns>
    internal static OfpQcTailPage Parse(ReadOnlySpan<byte> data, int pageSize)
    {
        if (data.Length < 0x20)
        {
            throw new InvalidOppFormatException("Tail page data too short.");
        }

        var magicValue = BinaryPrimitives.ReadUInt32LittleEndian(data[0x10..]);
        var xmlSectorOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        var xmlLength = BinaryPrimitives.ReadUInt32LittleEndian(data[0x18..]);

        return new OfpQcTailPage
        {
            MagicValue = magicValue,
            XmlSectorOffset = xmlSectorOffset,
            XmlLength = xmlLength,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// <para>验证指定偏移量处是否存在 QC 魔数。</para>
    /// Validates whether the QC magic number exists at the specified offset.
    /// </summary>
    /// <param name="data"><para>包含尾部页数据的只读字节跨度。</para>Read-only byte span containing the tail page data.</param>
    /// <param name="offset"><para>魔数在数据中的偏移量。</para>Offset of the magic number in the data.</param>
    /// <returns><para>如果魔数匹配返回 true，否则返回 false。</para>True if the magic number matches, false otherwise.</returns>
    internal static bool ValidateMagic(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            return false;
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        return value == Magic;
    }
}

/// <summary>
/// <para>OFP MTK 主头结构。</para>
/// OFP MTK main header structure.
/// </summary>
internal sealed class OfpMtkHeader
{
    public const int Size = 0x6C;

    public string ProjectName { get; init; } = string.Empty;
    public string CpuModel { get; init; } = string.Empty;
    public string FlashType { get; init; } = string.Empty;
    public int FileTableEntryCount { get; init; }
    public string ProjectInfo { get; init; } = string.Empty;

    /// <summary>
    /// <para>从二进制数据解析 OFP MTK 主头结构。</para>
    /// Parses the OFP MTK main header structure from binary data.
    /// </summary>
    /// <param name="data"><para>包含主头数据的只读字节跨度。</para>Read-only byte span containing the main header data.</param>
    /// <returns><para>解析后的主头结构。</para>The parsed main header structure.</returns>
    internal static OfpMtkHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new InvalidOppFormatException("MTK header data too short.");
        }

        var projectName = OfpHelper.DecodeAscii(data[..46]);
        // Python struct format: "46s Q 4s 7s 5s H 32s H" with native alignment.
        // The Q (unsigned long long) requires 8-byte alignment, so there are 2 bytes
        // of padding after the 46-byte prjname field (offset 46-47 = padding).
        // Correct offsets: unknownval=48, reserved=56, cpu=60, flashtype=67, hdr2entries=72, prjinfo=74, crc=106
        var cpuModel = OfpHelper.DecodeAscii(data.Slice(60, 7));
        var flashType = OfpHelper.DecodeAscii(data.Slice(67, 5));
        var fileTableEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(72, 2));
        var projectInfo = OfpHelper.DecodeAscii(data.Slice(74, 32));

        return new OfpMtkHeader
        {
            ProjectName = projectName,
            CpuModel = cpuModel,
            FlashType = flashType,
            FileTableEntryCount = fileTableEntryCount,
            ProjectInfo = projectInfo
        };
    }
}

/// <summary>
/// <para>OFP MTK 文件表条目结构。</para>
/// OFP MTK file table entry structure.
/// </summary>
internal sealed class OfpMtkFileEntry
{
    public const int Size = 0x60;

    public string Name { get; init; } = string.Empty;
    public long StartOffset { get; init; }
    public long TotalLength { get; init; }
    public long EncryptedLength { get; init; }
    public string FileName { get; init; } = string.Empty;
    public ulong Crc { get; init; }

    /// <summary>
    /// <para>从二进制数据解析 OFP MTK 文件表条目。</para>
    /// Parses an OFP MTK file table entry from binary data.
    /// </summary>
    /// <param name="data"><para>包含文件表条目数据的只读字节跨度。</para>Read-only byte span containing the file table entry data.</param>
    /// <returns><para>解析后的文件表条目。</para>The parsed file table entry.</returns>
    internal static OfpMtkFileEntry Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new InvalidOppFormatException("MTK file entry data too short.");
        }

        var name = OfpHelper.DecodeAscii(data[..32]);
        var startOffset = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(32, 8));
        var totalLength = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(40, 8));
        var encryptedLength = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(48, 8));
        var fileName = OfpHelper.DecodeAscii(data.Slice(56, 32));
        // CRC is at offset 88 but entry is only 0x60=96 bytes, so CRC at 88 is 8 bytes
        var crc = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(88, 8));

        return new OfpMtkFileEntry
        {
            Name = name,
            StartOffset = startOffset,
            TotalLength = totalLength,
            EncryptedLength = encryptedLength,
            FileName = fileName,
            Crc = crc
        };
    }
}

/// <summary>
/// <para>OPS 尾部配置页结构。</para>
/// OPS tail configuration page structure.
/// </summary>
internal sealed class OpsTailPage
{
    public const int Size = 0x200;
    public const uint Magic = 0x7CEF;

    public uint Version { get; init; }
    public uint Flags { get; init; }
    public uint MagicValue { get; init; }
    public uint ConfigSectorPos { get; init; }
    public uint XmlLength { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string FirmwareName { get; init; } = string.Empty;

    /// <summary>
    /// <para>从二进制数据解析 OPS 尾部配置页结构。</para>
    /// Parses the OPS tail configuration page structure from binary data.
    /// </summary>
    /// <param name="data"><para>包含尾部配置页数据的只读字节跨度。</para>Read-only byte span containing the tail configuration page data.</param>
    /// <returns><para>解析后的尾部配置页结构。</para>The parsed tail configuration page structure.</returns>
    internal static OpsTailPage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x4C)
        {
            throw new InvalidOppFormatException("OPS tail page data too short.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        // Skip 8 bytes reserved at offset 8
        var magicValue = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x10, 4));
        var configSectorPos = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x14, 4));
        var xmlLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x18, 4));
        var projectId = OfpHelper.DecodeAscii(data.Slice(0x1C, 16));
        var firmwareName = OfpHelper.DecodeAscii(data.Slice(0x2C, 32));

        return new OpsTailPage
        {
            Version = version,
            Flags = flags,
            MagicValue = magicValue,
            ConfigSectorPos = configSectorPos,
            XmlLength = xmlLength,
            ProjectId = projectId,
            FirmwareName = firmwareName
        };
    }
}

/// <summary>
/// <para>OFP 辅助工具类。</para>
/// OFP helper utility class.
/// </summary>
internal static class OfpHelper
{
    /// <summary>
    /// <para>从只读字节跨度解码 ASCII 字符串并去除尾部空字符。</para>
    /// Decodes an ASCII string from a read-only byte span and trims trailing null characters.
    /// </summary>
    /// <param name="data"><para>包含 ASCII 编码数据的只读字节跨度。</para>Read-only byte span containing ASCII-encoded data.</param>
    /// <returns><para>解码后的字符串。</para>The decoded string.</returns>
    internal static string DecodeAscii(ReadOnlySpan<byte> data) => Encoding.ASCII.GetString(data.ToArray()).TrimEnd('\0');
}