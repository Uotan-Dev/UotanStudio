namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Options for a flash bundle operation, containing all parameters needed for the
/// Program / Erase / Patch / Wipe / UFS-provision workflow.
/// <para>固件刷写包操作的配置选项，包含刷写流程所需的全部参数。</para>
/// </summary>
public sealed class FlashBundleOptions
{
    /// <summary>Captures operation policy before callbacks or asynchronous device work.</summary>
    internal FlashBundleOptions Snapshot()
    {
        var snapshot = (FlashBundleOptions)MemberwiseClone();
        snapshot.Labels = Labels is null ? null : [.. Labels];
        snapshot.NotLabels = NotLabels is null ? null : [.. NotLabels];
        snapshot.WipePhysicalPartitions = WipePhysicalPartitions is null ? null : [.. WipePhysicalPartitions];
        return snapshot;
    }

    /// <summary>
    /// Gets or sets the rawprogram XML file path (e.g. rawprogram0.xml).
    /// <para>获取或设置 rawprogram XML 文件路径（如 rawprogram0.xml）。</para>
    /// </summary>
    public string RawProgramPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional patch XML file path (e.g. patch0.xml).
    /// <para>获取或设置可选的 patch XML 文件路径（如 patch0.xml）。</para>
    /// </summary>
    public string? PatchPath { get; set; }

    /// <summary>
    /// Gets or sets the base directory used to resolve image file paths referenced by the
    /// rawprogram entries.
    /// <para>获取或设置用于解析 rawprogram 条目引用的镜像文件路径的基础目录。</para>
    /// </summary>
    public string ImageDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the explicit UFS provisioning XML path. When <c>null</c>, the orchestrator
    /// auto-discovers a <c>*ufs*provision*.xml</c> file under <see cref="ImageDirectory"/>.
    /// <para>获取或设置显式的 UFS 配置 XML 路径。为 <c>null</c> 时，编排器会在
    /// <see cref="ImageDirectory"/> 下自动发现 <c>*ufs*provision*.xml</c> 文件。</para>
    /// </summary>
    public string? UfsProvisionPath { get; set; }

    /// <summary>
    /// Gets or sets the optional label inclusion filter; only entries with these labels are
    /// processed.
    /// <para>获取或设置可选的标签包含过滤器；仅处理这些标签的条目。</para>
    /// </summary>
    public List<string>? Labels { get; set; }

    /// <summary>
    /// Gets or sets the optional label exclusion filter; entries with these labels are skipped.
    /// <para>获取或设置可选的标签排除过滤器；跳过这些标签的条目。</para>
    /// </summary>
    public List<string>? NotLabels { get; set; }

    /// <summary>
    /// Gets or sets the optional A/B slot override applied to the Firehose configuration.
    /// <para>获取或设置应用于 Firehose 配置的可选 A/B 槽位覆盖值。</para>
    /// </summary>
    public int? Slot { get; set; }

    /// <summary>
    /// Gets or sets the optional sector-size override (bytes) applied to the Firehose
    /// configuration before programming.
    /// <para>获取或设置在编程前应用于 Firehose 配置的可选扇区大小覆盖值（字节）。</para>
    /// </summary>
    public long? OverrideSectorSize { get; set; }

    /// <summary>
    /// Gets or sets whether program/erase entries are skipped entirely.
    /// <para>获取或设置是否完全跳过 program/erase 条目。</para>
    /// </summary>
    public bool SkipProgram { get; set; }

    /// <summary>
    /// Gets or sets whether patch entries are skipped entirely.
    /// <para>获取或设置是否完全跳过 patch 条目。</para>
    /// </summary>
    public bool SkipPatch { get; set; }

    /// <summary>
    /// Gets or sets whether the wipe (program-zeros) phase is skipped entirely.
    /// <para>获取或设置是否完全跳过 wipe（零字节编程）阶段。</para>
    /// </summary>
    public bool SkipWipe { get; set; }

    /// <summary>
    /// Gets or sets whether UFS provisioning is skipped entirely.
    /// <para>获取或设置是否完全跳过 UFS 配置。</para>
    /// </summary>
    public bool SkipUfsProvision { get; set; }

    /// <summary>
    /// Gets or sets whether the bundle runs offline source/command preflight without device
    /// communication. Sector-based operations require a manifest, configured or overridden
    /// sector size; device capacity and authorization cannot be verified offline.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets the number of leading sectors wiped with zeros on every physical partition
    /// touched by the bundle after programming/erasing (mirrors fh_loader --wipefirst;
    /// 0 disables the wipe phase).
    /// <para>获取或设置编程/擦除之后，对包内涉及的所有物理分区以零擦写的前导扇区数
    /// （对齐 fh_loader --wipefirst；0 表示禁用 wipe 阶段）。</para>
    /// </summary>
    public long WipeFirstSectors { get; set; }

    /// <summary>
    /// Gets or sets the physical partitions the wipe phase targets; <c>null</c> wipes every
    /// physical partition touched by the bundle.
    /// <para>获取或设置 wipe 阶段目标的物理分区；<c>null</c> 表示对包内涉及的所有物理分区
    /// 执行擦写。</para>
    /// </summary>
    public int[]? WipePhysicalPartitions { get; set; }

    /// <summary>
    /// Gets or sets whether to commit the UFS provisioning configuration; <c>null</c> uses the
    /// <c>commit</c> attribute parsed from the provisioning XML.
    /// <para>获取或设置是否提交 UFS 配置；<c>null</c> 使用从配置 XML 解析的 <c>commit</c> 属性。</para>
    /// </summary>
    public bool? UfsCommit { get; set; }
}