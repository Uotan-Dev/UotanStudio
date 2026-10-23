namespace UotanStudio.Firmware.BlockImage;

/// <summary>
/// A normalized partition-table entry shared by the GPT, MBR and Amlogic table builders.
/// <para>GPT、MBR 与 Amlogic 分区表构建器共用的归一化分区表条目。</para>
/// </summary>
public sealed class PartitionEntry
{
    /// <summary>
    /// Partition name.
    /// <para>分区名称。</para>
    /// </summary>
    /// <value>The partition name; empty when the table carries no names. <para>分区名称；表不含名称时为空字符串。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Partition start sector (512-byte sectors).
    /// <para>分区起始扇区（512 字节扇区）。</para>
    /// </summary>
    /// <value>The first sector of the partition. <para>分区的起始扇区。</para></value>
    public ulong StartSector { get; set; }

    /// <summary>
    /// Partition size in 512-byte sectors.
    /// <para>分区大小（512 字节扇区）。</para>
    /// </summary>
    /// <value>The partition size counted in sectors. <para>分区大小，以扇区计数。</para></value>
    public ulong SizeSectors { get; set; }

    /// <summary>
    /// Partition type text accepted by the target table builder: 0x-hex or decimal for MBR/Amlogic, and
    /// a type description such as <c>linear</c> for LP.
    /// <para>目标表构建器接受的分区类型文本：MBR/Amlogic 为 0x 十六进制或十进制，LP 为 <c>linear</c> 之类的类型描述。</para>
    /// </summary>
    /// <value>The type text of the partition. <para>分区的类型文本。</para></value>
    public string Type { get; set; } = string.Empty;
}