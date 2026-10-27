using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>One entry of the streaming on-device partition table: the decoded section name, the start LBA, the sector count, the three attribute bytes, and the flash selector.
/// <para>流式设备分区表中的一条记录：解码后的段名、起始 LBA、扇区数、三个属性字节与闪存选择器。</para></summary>
/// <param name="Name">The partition name with the leading storage-class prefix removed.
/// <para>去除存储类别前导后的分区名。</para></param>
/// <param name="StartLba">The start LBA recorded by the table, or derived from the record length and spare for the layout without explicit offsets.
/// <para>表内记录的起始 LBA；无显式偏移的布局下由记录长度与备用扇区推算。</para></param>
/// <param name="Sectors">The masked sector count: the length plus the spare, truncated to 16 bits.
/// <para>掩码后的扇区数：长度加备用扇区，截断为 16 位。</para></param>
/// <param name="Attribute1">The first raw attribute byte.<para>第一个原始属性字节。</para></param>
/// <param name="Attribute2">The second raw attribute byte.<para>第二个原始属性字节。</para></param>
/// <param name="Attribute3">The third raw attribute byte.<para>第三个原始属性字节。</para></param>
/// <param name="WhichFlash">The flash selector byte.<para>闪存选择器字节。</para></param>
public sealed record StreamingPartitionEntry(
    string Name,
    uint StartLba,
    uint Sectors,
    byte Attribute1,
    byte Attribute2,
    byte Attribute3,
    byte WhichFlash);

/// <summary>
/// Parses the 512-byte streaming on-device partition table: four little-endian header words and fixed 28-byte records, in the two field layouts distinguished by magic. Every length and record count is bounds-checked with checked arithmetic before any record byte is read.
/// <para>解析 512 字节流式设备端分区表：四个小端头部字与定长 28 字节记录，按魔数区分两种字段布局。读取任何记录字节前，所有长度与记录数均以 checked 运算做上限校验。</para>
/// </summary>
public sealed class StreamingPartitionTable
{
    /// <summary>
    /// Header magic of the table layout whose records store length and spare instead of an
    /// explicit offset.
    /// <para>该表布局的表头魔数；其记录存长度与备用扇区而非显式偏移。</para>
    /// </summary>
    public const uint LegacyMagic1 = 0xAA7D1B9A;

    /// <summary>
    /// Second header magic word of the layout whose records store length and spare.
    /// <para>该布局的第二个表头魔数字。</para>
    /// </summary>
    public const uint LegacyMagic2 = 0x1F7D48BC;

    /// <summary>
    /// First table magic the read path accepts.
    /// <para>读取路径接受的首个表魔数。</para>
    /// </summary>
    public const uint Partition9kMagic1 = 0x55EE73AA;

    /// <summary>
    /// Second table magic the read path accepts.
    /// <para>读取路径接受的第二个表魔数。</para>
    /// </summary>
    public const uint Partition9kMagic2 = 0xE35EBDDB;

    /// <summary>
    /// Byte size of one partition record: the fixed <c>0x1C</c> stride.
    /// <para>每条分区记录的字节数：固定的 <c>0x1C</c> 步长。</para>
    /// </summary>
    public const int RecordBytes = 0x1C;

    /// <summary>
    /// Gets the first header magic word.
    /// <para>获取第一个表头魔数。</para>
    /// </summary>
    /// <value>The raw magic1 word. <para>原始 magic1 字。</para></value>
    public uint Magic1 { get; }

    /// <summary>
    /// Gets the second header magic word.
    /// <para>获取第二个表头魔数。</para>
    /// </summary>
    /// <value>The raw magic2 word. <para>原始 magic2 字。</para></value>
    public uint Magic2 { get; }

    /// <summary>
    /// Gets the table version word.
    /// <para>获取表版本字。</para>
    /// </summary>
    /// <value>The raw version word. <para>原始版本字。</para></value>
    public uint Version { get; }

    /// <summary>
    /// Gets the parsed partition records, stopping at the first record whose second name byte is
    /// not the storage-class colon.
    /// <para>获取已解析的分区记录，在名字节第二位不是存储类别冒号的首条记录处停止。</para>
    /// </summary>
    /// <value>The decoded entries in table order. <para>按表顺序排列的解码条目。</para></value>
    public IReadOnlyList<StreamingPartitionEntry> Entries { get; }

    private StreamingPartitionTable(uint magic1, uint magic2, uint version,
        IReadOnlyList<StreamingPartitionEntry> entries)
    {
        Magic1 = magic1;
        Magic2 = magic2;
        Version = version;
        Entries = entries;
    }

    /// <summary>Parses a raw 512-byte on-device streaming partition table image.
    /// <para>解析 512 字节的原始流式设备分区表映像。</para></summary>
    /// <param name="table">The raw table bytes; at most <see cref="StreamingConstants.PartitionTableBytes"/> long.
    /// <para>原始表字节，至多 <see cref="StreamingConstants.PartitionTableBytes"/> 字节。</para></param>
    /// <returns>The parsed table.<para>解析后的表。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image is shorter than the 16-byte header or longer than <see cref="StreamingConstants.PartitionTableBytes"/>.
    /// <para>映像短于 16 字节头或长于 <see cref="StreamingConstants.PartitionTableBytes"/> 时抛出。</para></exception>
    /// <exception cref="StreamingException">Thrown when the header record count exceeds the space the image can hold, or a record name leaves the storage-class prefix format.
    /// <para>头部记录数超出映像可容纳的空间，或记录名不符合存储类别前缀格式时抛出。</para></exception>
    public static StreamingPartitionTable Parse(ReadOnlySpan<byte> table)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(table.Length, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(table.Length, StreamingConstants.PartitionTableBytes);

        var magic1 = BinaryPrimitives.ReadUInt32LittleEndian(table);
        var magic2 = BinaryPrimitives.ReadUInt32LittleEndian(table[4..]);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(table[8..]);
        var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(table[12..]);
        if (recordCount > (uint)((table.Length - 16) / RecordBytes))
        {
            throw new StreamingException(EdlErrorCode.InvalidParameter,
                $"The partition table header claims {recordCount} records, more than the image can hold.");
        }

        var legacy = magic1 == LegacyMagic1 && magic2 == LegacyMagic2;
        var entries = new List<StreamingPartitionEntry>();
        uint runningOffset = 0;
        for (var index = 0u; index < recordCount; index++)
        {
            var start = checked((int)(index * RecordBytes)) + 16;
            var name = table[start..(start + 16)];
            if (name[1] != 0x3A)
            {
                break;
            }

            var label = DecodeName(name[2..]);
            uint offset;
            uint sectors;
            byte attribute1;
            byte attribute2;
            byte attribute3;
            byte whichFlash;
            if (legacy)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(table[(start + 16)..]);
                var spare = table[start + 20];
                offset = runningOffset;
                sectors = checked(length + spare) & 0xFFFF;
                attribute1 = table[start + 21];
                attribute2 = table[start + 22];
                attribute3 = table[start + 23];
                whichFlash = table[start + 24];
                runningOffset = checked(runningOffset + length + spare);
            }
            else
            {
                offset = BinaryPrimitives.ReadUInt32LittleEndian(table[(start + 16)..]);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(table[(start + 20)..]);
                attribute1 = table[start + 24];
                attribute2 = table[start + 25];
                attribute3 = table[start + 26];
                whichFlash = table[start + 27];
                sectors = length & 0xFFFF;
            }

            entries.Add(new StreamingPartitionEntry(label, offset, sectors,
                attribute1, attribute2, attribute3, whichFlash));
        }

        return new StreamingPartitionTable(magic1, magic2, version, entries);
    }

    /// <summary>
    /// Decodes a record name up to its first NUL byte and drops the trailing NUL pad.
    /// <para>把记录名解码到首个 NUL 字节为止，并去掉尾部的 NUL 填充。</para>
    /// </summary>
    private static string DecodeName(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(terminator < 0 ? field : field[..terminator]);
    }
}