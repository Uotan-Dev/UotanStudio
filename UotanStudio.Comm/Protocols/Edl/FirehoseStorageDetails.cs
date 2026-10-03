namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Descriptive storage facts reported inside a <c>&lt;getstorageinfo&gt;</c> log line; fields the
/// target did not include stay <see langword="null"/> because different Firehose builds report
/// different subsets.
/// <para><c>&lt;getstorageinfo&gt;</c> 日志中目标上报的描述性存储信息；不同 Firehose 版本上报的字段
/// 子集不同，目标未包含的字段保持 <see langword="null"/>。</para>
/// </summary>
public sealed record FirehoseStorageDetails
{
    /// <summary>The storage type reported by the target (for example UFS, EMMC or NAND).
    /// <para>目标上报的存储类型（如 UFS、EMMC、NAND）。</para></summary>
    public string? StorageType { get; init; }

    /// <summary>The memory type string from the target's storage descriptor.
    /// <para>目标存储描述符中的内存类型字符串。</para></summary>
    public string? MemType { get; init; }

    /// <summary>The reported page size in bytes. <para>目标上报的页大小（字节）。</para></summary>
    public long? PageSize { get; init; }

    /// <summary>The reported total storage density in bytes. <para>目标上报的总存储密度（字节）。</para></summary>
    public long? Density { get; init; }

    /// <summary>The storage serial number as reported text. <para>目标上报的存储序列号文本。</para></summary>
    public string? SerialNumber { get; init; }

    /// <summary>The storage manufacturer identifier as reported text.
    /// <para>目标上报的存储厂商标识文本。</para></summary>
    public string? ManufacturerId { get; init; }

    /// <summary>The storage firmware version string. <para>存储固件版本字符串。</para></summary>
    public string? FirmwareVersion { get; init; }

    /// <summary>The physical partition (LUN) number the report describes.
    /// <para>该报告描述的物理分区（LUN）编号。</para></summary>
    public int? PhysicalPartitionNumber { get; init; }
}