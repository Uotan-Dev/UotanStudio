using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>
/// One partition entry of a Sunxi MBR (<c>sunxi_mbr.fex</c>) partition table.
/// <para>Sunxi MBR（<c>sunxi_mbr.fex</c>）分区表的一个分区条目。</para>
/// </summary>
/// <param name="ClassName">Vendor class name of the partition. <para>分区的厂商类名。</para></param>
/// <param name="Name">Partition name; the flashing flow resolves the payload file by it.
/// <para>分区名；烧录流程按它解析负载文件。</para></param>
/// <param name="FirstSector">First 512-byte sector of the partition on the storage.
/// <para>分区在存储上的首个 512 字节扇区。</para></param>
/// <param name="SectorCount">Partition capacity counted in 512-byte sectors.
/// <para>分区容量，以 512 字节扇区计数。</para></param>
/// <param name="UserType">Vendor user-type word. <para>厂商 user-type 字。</para></param>
/// <param name="KeyData">Whether the partition carries vendor key data.
/// <para>分区是否承载厂商密钥数据。</para></param>
/// <param name="ReadOnly">Whether the partition is marked read-only. <para>分区是否标记为只读。</para></param>
public sealed record SunxiMbrPartition(string ClassName, string Name, ulong FirstSector, ulong SectorCount,
    uint UserType, bool KeyData, bool ReadOnly);

/// <summary>
/// Parsed header of a Sunxi MBR partition table together with its partition entries in table order.
/// <para>解析出的 Sunxi MBR 分区表头，及其按表顺序排列的分区条目。</para>
/// </summary>
public sealed class SunxiMbrTableInfo
{
    /// <summary>Creates the parsed table view. <para>创建解析后的表视图。</para></summary>
    /// <param name="crc32">Declared table CRC word, reported uninterpreted because the reference flow does not
    /// define its coverage. <para>声明的表 CRC 字；参考流程未定义其覆盖范围，原样报告。</para></param>
    /// <param name="version">Table format version word. <para>表格式版本字。</para></param>
    /// <param name="copy">Declared table copy count. <para>声明的表副本数。</para></param>
    /// <param name="index">Declared table index. <para>声明的表序号。</para></param>
    /// <param name="partitions">Entries in table order. <para>按表顺序排列的条目。</para></param>
    internal SunxiMbrTableInfo(uint crc32, uint version, uint copy, uint index,
        IReadOnlyList<SunxiMbrPartition> partitions)
    {
        Crc32 = crc32;
        Version = version;
        Copy = copy;
        Index = index;
        Partitions = partitions;
    }

    /// <summary>Declared table CRC word, reported uninterpreted. <para>声明的表 CRC 字，原样报告。</para></summary>
    public uint Crc32 { get; }

    /// <summary>Table format version word. <para>表格式版本字。</para></summary>
    public uint Version { get; }

    /// <summary>Declared table copy count. <para>声明的表副本数。</para></summary>
    public uint Copy { get; }

    /// <summary>Declared table index. <para>声明的表序号。</para></summary>
    public uint Index { get; }

    /// <summary>Entries in table order. <para>按表顺序排列的条目。</para></summary>
    public IReadOnlyList<SunxiMbrPartition> Partitions { get; }
}

/// <summary>
/// Bounded parser for the Sunxi MBR partition table (<c>sunxi_mbr.fex</c>): a 32-byte header carrying the
/// <c>softw411</c> magic followed by up to 120 packed 128-byte partition entries; the vendor layout the FES
/// flashing flow downloads with the MBR tag.
/// <para>Sunxi MBR 分区表（<c>sunxi_mbr.fex</c>）的有界解析器：32 字节表头携带 <c>softw411</c> 魔数，后随至多
/// 120 个 128 字节打包分区条目；即 FES 烧录流程以 MBR 标签下载的厂商布局。</para>
/// </summary>
public static class SunxiMbrTable
{
    /// <summary>Magic bytes at offset 8 of the table. <para>表中偏移 8 处的魔数字节。</para></summary>
    public static ReadOnlySpan<byte> Magic => "softw411"u8;

    /// <summary>Hard ceiling on the declared partition count. <para>声明分区数量的硬性上限。</para></summary>
    public const int MaximumPartitions = 120;

    /// <summary>Byte length of one packed partition entry. <para>单个打包分区条目的字节长度。</para></summary>
    public const int PartitionEntrySize = 128;

    /// <summary>Byte length of the table header. <para>表头的字节长度。</para></summary>
    public const int HeaderSize = 32;

    /// <summary>Vendor full-table size the flashing flow downloads. <para>烧录流程下载的整表厂商尺寸。</para></summary>
    public const int TableSize = 16 * 1024;

    private const int PartitionNameLength = 16;
    private const int PartitionReservedLength = 68;

    /// <summary>
    /// Reports whether the buffer starts a Sunxi MBR table: long enough and carrying the magic at offset 8.
    /// <para>判断缓冲区是否为 Sunxi MBR 表的开头：长度足够且偏移 8 处携带魔数。</para>
    /// </summary>
    /// <param name="data">Leading bytes of the table, at least 16. <para>表的前导字节，至少 16 个。</para></param>
    /// <returns><see langword="true"/> when the magic matches. <para>魔数匹配时为 <see langword="true"/>。</para></returns>
    public static bool HasMagic(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && data[8..16].SequenceEqual(Magic);

    /// <summary>
    /// Parses a table from its bytes, rejecting truncated data, foreign magic or a declared partition count
    /// outside the packed-entry array.
    /// <para>从字节解析表；拒绝截断数据、外来魔数或超出打包条目数组的声明分区数。</para>
    /// </summary>
    /// <param name="data">Table bytes; the vendor table is 16 KiB, and the parser only requires the declared
    /// entries to be present. <para>表字节；厂商整表为 16 KiB，解析器只要求声明的条目在场。</para></param>
    /// <returns>The parsed table. <para>解析出的表。</para></returns>
    /// <exception cref="InvalidDataException">The buffer is shorter than the header, the magic is absent, or the
    /// declared count exceeds the entry array. <para>缓冲区短于表头、魔数缺失，或声明数量超出条目数组。</para></exception>
    public static SunxiMbrTableInfo Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException($"A Sunxi MBR table is at least {HeaderSize} bytes.");
        }

        if (!HasMagic(data))
        {
            throw new InvalidDataException("The Sunxi MBR table does not carry the softw411 magic.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
        if (count > MaximumPartitions)
        {
            throw new InvalidDataException(
                $"The Sunxi MBR table declares {count} partitions, above the packed limit of {MaximumPartitions}.");
        }

        if (data.Length < HeaderSize + (int)count * PartitionEntrySize)
        {
            throw new InvalidDataException("The Sunxi MBR table ends before its declared partitions.");
        }

        var partitions = new List<SunxiMbrPartition>((int)count);
        for (var index = 0; index < (int)count; index++)
        {
            partitions.Add(ParsePartition(data.Slice(HeaderSize + index * PartitionEntrySize, PartitionEntrySize)));
        }

        return new SunxiMbrTableInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            partitions);
    }

    /// <summary>
    /// Parses a table from a stream, reading at most <see cref="TableSize"/> bytes so an oversized stream cannot
    /// force a large allocation.
    /// <para>从流解析表；至多读取 <see cref="TableSize"/> 字节，避免过大的流逼出大量分配。</para>
    /// </summary>
    /// <param name="source">Readable stream positioned at the table. <para>定位在表处的可读流。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>A task that produces the parsed table. <para>产生解析出的表的任务。</para></returns>
    /// <exception cref="InvalidDataException">The stream ends before the header or the declared partitions.
    /// <para>流在表头或声明的分区之前结束。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static async Task<SunxiMbrTableInfo> ParseAsync(Stream source, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[TableSize];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return Parse(buffer.AsSpan(0, filled));
    }

    private static SunxiMbrPartition ParsePartition(ReadOnlySpan<byte> entry)
    {
        return new SunxiMbrPartition(
            ReadText(entry.Slice(16, PartitionNameLength)),
            ReadText(entry.Slice(32, PartitionNameLength)),
            ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(entry) << 32)
                | BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]),
            ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) << 32)
                | BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(entry[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(entry[52..]) != 0,
            BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]) != 0);
    }

    private static string ReadText(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(terminator < 0 ? field : field[..terminator]);
        return text.TrimEnd(' ');
    }
}