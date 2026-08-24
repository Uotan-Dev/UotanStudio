using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.OnePlusOps;

/// <summary>
/// Represents the header of a OnePlus OPS firmware file
/// </summary>
/// <para>表示OnePlus OPS固件文件的头部</para>
public class OpsHeader
{
    /// <summary>
    /// Gets or sets the magic bytes identifying the OPS file format
    /// </summary>
    /// <para>获取或设置标识OPS文件格式的魔术字节</para>
    public string Magic { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OPS format version number
    /// </summary>
    /// <para>获取或设置OPS格式版本号</para>
    public uint Version { get; set; }

    /// <summary>
    /// Gets or sets the size of the header in bytes
    /// </summary>
    /// <para>获取或设置头部的字节大小</para>
    public uint HeaderSize { get; set; }

    /// <summary>
    /// Gets or sets the total file size in bytes
    /// </summary>
    /// <para>获取或设置文件的总字节大小</para>
    public uint FileSize { get; set; }

    /// <summary>
    /// Gets or sets the number of entries in the OPS file
    /// </summary>
    /// <para>获取或设置OPS文件中的条目数量</para>
    public uint EntryCount { get; set; }

    /// <summary>
    /// Gets or sets the flags field from the header
    /// </summary>
    /// <para>获取或设置头部中的标志字段</para>
    public uint Flags { get; set; }

    /// <summary>
    /// Gets a value indicating whether the header contains a valid OPS magic signature
    /// </summary>
    /// <para>获取一个值，指示头部是否包含有效的OPS魔术签名</para>
    public bool IsValid => Magic == OpsConstants.OPS_MAGIC;

    /// <summary>
    /// Parses an OpsHeader from a read-only byte span
    /// </summary>
    /// <para>从只读字节跨度解析OpsHeader</para>
    /// <param name="data">The byte span containing the header data (must be at least HEADER_SIZE bytes)</param>
    /// <returns>A parsed OpsHeader instance</returns>
    /// <exception cref="ArgumentException">Thrown when the data span is smaller than HEADER_SIZE</exception>
    public static OpsHeader FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < OpsConstants.HEADER_SIZE
            ? throw new ArgumentException("Insufficient data to parse OPS header", nameof(data))
            : new OpsHeader
            {
                Magic = Encoding.ASCII.GetString(data[..4].ToArray()),
                Version = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)),
                HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)),
                FileSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4)),
                EntryCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4)),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4))
            };
    }
}

/// <summary>
/// Represents a single entry within a OnePlus OPS firmware file
/// </summary>
/// <para>表示OnePlus OPS固件文件中的单个条目</para>
public class OpsEntry
{
    /// <summary>
    /// Gets or sets the entry identifier
    /// </summary>
    /// <para>获取或设置条目标识符</para>
    public uint Id { get; set; }

    /// <summary>
    /// Gets or sets the offset within the OPS stream where the entry data begins
    /// </summary>
    /// <para>获取或设置OPS流中条目数据开始的偏移量</para>
    public uint Offset { get; set; }

    /// <summary>
    /// Gets or sets the size of the entry data in bytes
    /// </summary>
    /// <para>获取或设置条目数据的字节大小</para>
    public uint Size { get; set; }

    /// <summary>
    /// Gets or sets the name of the entry
    /// </summary>
    /// <para>获取或设置条目的名称</para>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the raw data of the entry
    /// </summary>
    /// <para>获取或设置条目的原始数据</para>
    public byte[] Data { get; set; } = [];

    /// <summary>
    /// Parses an OpsEntry from a read-only byte span
    /// </summary>
    /// <para>从只读字节跨度解析OpsEntry</para>
    /// <param name="data">The byte span containing the entry header (must be at least ENTRY_HEADER_SIZE bytes)</param>
    /// <returns>A parsed OpsEntry instance</returns>
    public static OpsEntry FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < OpsConstants.ENTRY_HEADER_SIZE
            ? throw new ArgumentException("Insufficient data to parse OPS entry", nameof(data))
            : new OpsEntry
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]),
                Offset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)),
                Size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)),
                Name = Encoding.ASCII.GetString(data.Slice(12, OpsConstants.ENTRY_NAME_SIZE)).TrimEnd('\0')
            };
    }
}