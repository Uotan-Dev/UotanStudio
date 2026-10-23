namespace UotanStudio.Firmware.BlockImage;

/// <summary>
/// Partition-table layouts recognised by <see cref="PartitionTableProbe"/>.
/// <para><see cref="PartitionTableProbe"/> 可识别的分区表布局。</para>
/// </summary>
public enum PartitionTableKind
{
    /// <summary>No recognised partition table. <para>未识别出分区表。</para></summary>
    Unknown = 0,

    /// <summary>UEFI GPT partition table. <para>UEFI GPT 分区表。</para></summary>
    Gpt = 1,

    /// <summary>Classic MBR partition table. <para>经典 MBR 分区表。</para></summary>
    Mbr = 2,

    /// <summary>Amlogic vendor partition table. <para>Amlogic 厂商私有分区表。</para></summary>
    Amlogic = 3
}