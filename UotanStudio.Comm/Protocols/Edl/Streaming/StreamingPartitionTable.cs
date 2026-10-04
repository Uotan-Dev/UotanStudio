using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// One entry of the streaming on-device partition table: the decoded section name, the start LBA,
/// the sector count, the three attribute bytes, and the flash selector
/// (get_partitions, streaming.py:649-682).
/// <para>流式设备分区表中的一条记录：解码后的段名、起始 LBA、扇区数、三个属性字节与闪存选择器
/// （get_partitions，streaming.py:649-682）。</para>
/// </summary>
/// <param name="Name">The partition name with the leading storage-class prefix removed.
/// <para>去除存储类别前导后的分区名。</para></param>
/// <param name="StartLba">The start LBA recorded by the table or computed for the legacy
/// layout. <para>表内记录、或旧版布局下推算出的起始 LBA。</para></param>
/// <param name="Sectors">The masked sector count, length plus spare truncated to 16 bits like
/// the reference (streaming.py:675-676). <para>按参考做法掩码为 16 位的长度加备用扇区数
/// （streaming.py:675-676）。</para></param>
/// <param name="Attribute1">The first raw attribute byte. <para>第一个原始属性字节。</para></param>
/// <param name="Attribute2">The second raw attribute byte. <para>第二个原始属性字节。</para></param>
/// <param name="Attribute3">The third raw attribute byte. <para>第三个原始属性字节。</para></param>
/// <param name="WhichFlash">The flash selector byte. <para>闪存选择器字节。</para></param>
public sealed record StreamingPartitionEntry(
    string Name,
    uint StartLba,
    uint Sectors,
    byte Attribute1,
    byte Attribute2,
    byte Attribute3,
    byte WhichFlash);

/// <summary>
/// Parses the 512-byte streaming on-device partition table: the four little-endian header words
/// and the fixed 28-byte records, in the two field layouts the reference distinguishes by magic
/// (get_partitions and read_partition_table, streaming.py:634-682). Every length and record count
/// is bounds-checked with <c>checked</c> arithmetic before any record byte is read.
/// <para>解析 512 字节的流式设备分区表：四个小端头部字与固定的 28 字节记录，并按参考依据魔数
/// 区分的两种字段布局解析（get_partitions 与 read_partition_table，streaming.py:634-682）。每个
/// 长度与记录数在读取任何记录字节前都用 <c>checked</c> 运算做边界检查。</para>
/// </summary>
public sealed class StreamingPartitionTable
{
    /// <summary>
    /// Header magic of the legacy table whose records store length and spare instead of an
    /// explicit offset (streaming.py:665).
    /// <para>旧版表头魔数；其记录存长度与备用扇区而非显式偏移（streaming.py:665）。</para>
    /// </summary>
    public const uint LegacyMagic1 = 0xAA7D1B9A;

    /// <summary>
    /// Second legacy header magic word (streaming.py:665).
    /// <para>旧版表头第二个魔数字（streaming.py:665）。</para>
    /// </summary>
    public const uint LegacyMagic2 = 0x1F7D48BC;

    /// <summary>
    /// First recognized table magic accepted by the reference read path (streaming.py:645).
    /// <para>参考读取路径认可的首个表魔数（streaming.py:645）。</para>
    /// </summary>
    public const uint Partition9kMagic1 = 0x55EE73AA;

    /// <summary>
    /// Second recognized table magic accepted by the reference read path (streaming.py:645).
    /// <para>参考读取路径认可的第二个表魔数（streaming.py:645）。</para>
    /// </summary>
    public const uint Partition9kMagic2 = 0xE35EBDDB;

    /// <summary>
    /// Byte size of one partition record (the reference <c>0x1C</c> stride, streaming.py:666-670).
    /// <para>每条分区记录的字节数（参考的 <c>0x1C</c> 步长，streaming.py:666-670）。</para>
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
    /// Gets the parsed partition records, stopping where the reference stops at a record whose
    /// second name byte is not the storage-class colon.
    /// <para>获取已解析的分区记录，并在参考的停止条件处停止：记录名第二个字节不是存储类别冒号。</para>
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

    /// <summary>
    /// Parses a raw streaming partition table image (the reference reads it with the 512-byte
    /// on-device table size, streaming.py:15, 243-246).
    /// <para>解析原始流式分区表映像（参考按 512 字节的设备表大小读取，streaming.py:15、243-246）。</para>
    /// </summary>
    /// <param name="table">The raw table bytes; at most
    /// <see cref="StreamingConstants.PartitionTableBytes"/> long.
    /// <para>原始表字节，至多 <see cref="StreamingConstants.PartitionTableBytes"/> 字节。</para></param>
    /// <returns>The parsed table. <para>解析后的表。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image is shorter than the
    /// 16-byte header or longer than <see cref="StreamingConstants.PartitionTableBytes"/>.
    /// <para>映像短于 16 字节头或长于 <see cref="StreamingConstants.PartitionTableBytes"/> 时抛出。</para></exception>
    /// <exception cref="StreamingException">Thrown when the header record count exceeds the space
    /// the image can hold, or a record name leaves the reference storage-class prefix format.
    /// <para>头部记录数超出映像可容纳的空间，或记录名不符合参考的存储类别前缀格式时抛出。</para></exception>
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
    /// Decodes a record name up to its first NUL, mirroring the reference
    /// <c>rstrip(b"\x00")</c> (streaming.py:674).
    /// <para>将记录名解码到首个 NUL 为止，对应参考的 <c>rstrip(b"\x00")</c>
    /// （streaming.py:674）。</para>
    /// </summary>
    private static string DecodeName(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(terminator < 0 ? field : field[..terminator]);
    }
}