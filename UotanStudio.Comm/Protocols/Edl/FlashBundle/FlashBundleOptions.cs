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
    /// <value>Manifest parsed for program and erase entries; preparation fails when it is empty or the file does not exist.
    /// <para>用于解析 program 与 erase 条目的清单；为空或文件不存在时准备阶段失败。</para></value>
    public string RawProgramPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional patch XML file path (e.g. patch0.xml).
    /// <para>获取或设置可选的 patch XML 文件路径（如 patch0.xml）。</para>
    /// </summary>
    /// <value>The patch XML path, or <see langword="null"/> to parse no patches; a non-empty path must exist on disk.
    /// <para>patch XML 路径；为 <see langword="null"/> 时不解析补丁；非空路径必须存在于磁盘。</para></value>
    public string? PatchPath { get; set; }

    /// <summary>
    /// Gets or sets the base directory used to resolve image file paths referenced by the
    /// rawprogram entries.
    /// <para>获取或设置用于解析 rawprogram 条目引用的镜像文件路径的基础目录。</para>
    /// </summary>
    /// <value>Directory that must be non-empty; image filenames and UFS auto-discovery resolve against it.
    /// <para>必须非空的目录；镜像文件名与 UFS 自动发现都按它解析。</para></value>
    public string ImageDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the explicit UFS provisioning XML path. When <c>null</c>, the orchestrator
    /// auto-discovers a <c>*ufs*provision*.xml</c> file under <see cref="ImageDirectory"/>.
    /// <para>获取或设置显式的 UFS 配置 XML 路径。为 <c>null</c> 时，编排器会在
    /// <see cref="ImageDirectory"/> 下自动发现 <c>*ufs*provision*.xml</c> 文件。</para>
    /// </summary>
    /// <value>Explicit provisioning XML path, or <see langword="null"/> to auto-discover one match; several matches abort preparation.
    /// <para>显式配置 XML 路径；为 <see langword="null"/> 时自动发现唯一匹配；匹配多个会使准备失败。</para></value>
    public string? UfsProvisionPath { get; set; }

    /// <summary>
    /// Gets or sets the optional label inclusion filter; only entries with these labels are
    /// processed.
    /// <para>获取或设置可选的标签包含过滤器；仅处理这些标签的条目。</para>
    /// </summary>
    /// <value>Accepted labels compared case-insensitively; <see langword="null"/> or empty disables the inclusion filter.
    /// <para>按不区分大小写比较的接受标签；为 <see langword="null"/> 或空时禁用包含过滤。</para></value>
    public List<string>? Labels { get; set; }

    /// <summary>
    /// Gets or sets the optional label exclusion filter; entries with these labels are skipped.
    /// <para>获取或设置可选的标签排除过滤器；跳过这些标签的条目。</para>
    /// </summary>
    /// <value>Rejected labels compared case-insensitively; entries with an empty label are kept, and <see langword="null"/> or empty disables the filter.
    /// <para>按不区分大小写比较的排除标签；标签为空的条目会被保留；为 <see langword="null"/> 或空时禁用该过滤。</para></value>
    public List<string>? NotLabels { get; set; }

    /// <summary>
    /// Gets or sets the optional A/B slot override applied to the Firehose configuration.
    /// <para>获取或设置应用于 Firehose 配置的可选 A/B 槽位覆盖值。</para>
    /// </summary>
    /// <value>Slot written to the Firehose configuration before programming, or <see langword="null"/> to keep the current configuration.
    /// <para>编程前写入 Firehose 配置的槽位；为 <see langword="null"/> 时保留当前配置。</para></value>
    public int? Slot { get; set; }

    /// <summary>
    /// Gets or sets the optional sector-size override (bytes) applied to the Firehose
    /// configuration before programming.
    /// <para>获取或设置在编程前应用于 Firehose 配置的可选扇区大小覆盖值（字节）。</para>
    /// </summary>
    /// <value>Sector size that takes precedence over manifest and configured values; when set it must be positive and at most 65536.
    /// <para>优先于清单与配置值的扇区大小；设置时必须为正且不超过 65536。</para></value>
    public long? OverrideSectorSize { get; set; }

    /// <summary>
    /// Gets or sets whether program/erase entries are skipped entirely.
    /// <para>获取或设置是否完全跳过 program/erase 条目。</para>
    /// </summary>
    /// <value>True to skip source resolution, validation and execution of every program and erase entry, including counting them as skipped.
    /// <para>为 true 时跳过全部 program 与 erase 条目的源解析、校验与执行，并将其计为已跳过。</para></value>
    public bool SkipProgram { get; set; }

    /// <summary>
    /// Gets or sets whether patch entries are skipped entirely.
    /// <para>获取或设置是否完全跳过 patch 条目。</para>
    /// </summary>
    /// <value>True to ignore the patch XML even when <see cref="PatchPath"/> points to an existing file.
    /// <para>为 true 时即使 <see cref="PatchPath"/> 指向已存在的文件也忽略 patch XML。</para></value>
    public bool SkipPatch { get; set; }

    /// <summary>
    /// Gets or sets whether the wipe (program-zeros) phase is skipped entirely.
    /// <para>获取或设置是否完全跳过 wipe（零字节编程）阶段。</para>
    /// </summary>
    /// <value>True to leave out the leading-sector zeroing steps governed by <see cref="WipeFirstSectors"/>.
    /// <para>为 true 时省略由 <see cref="WipeFirstSectors"/> 控制的前导扇区清零步骤。</para></value>
    public bool SkipWipe { get; set; }

    /// <summary>
    /// Gets or sets whether UFS provisioning is skipped entirely.
    /// <para>获取或设置是否完全跳过 UFS 配置。</para>
    /// </summary>
    /// <value>True to skip parsing and executing a provisioning XML even when one is present.
    /// <para>为 true 时即使存在配置 XML 也跳过其解析与执行。</para></value>
    public bool SkipUfsProvision { get; set; }

    /// <summary>
    /// Gets or sets whether the bundle runs offline source/command preflight without device
    /// communication. Sector-based operations require a manifest, configured or overridden
    /// sector size; device capacity and authorization cannot be verified offline.
    /// <para>获取或设置该包是否在不进行设备通信的情况下执行离线源文件/命令预检。基于扇区的
    /// 操作需要来自清单、配置或显式覆盖的扇区大小；设备容量与授权无法离线验证。</para>
    /// </summary>
    /// <value>True to inspect sources and sector math against local files only; false performs the real flashing run.
    /// <para>为 true 时仅依据本地文件检查源与扇区运算；为 false 时执行真实刷写。</para></value>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets the number of leading sectors wiped with zeros on every physical partition
    /// touched by the bundle after programming/erasing (mirrors fh_loader --wipefirst;
    /// 0 disables the wipe phase).
    /// <para>获取或设置编程/擦除之后，对包内涉及的所有物理分区以零擦写的前导扇区数
    /// （对齐 fh_loader --wipefirst；0 表示禁用 wipe 阶段）。</para>
    /// </summary>
    /// <value>Leading-sector count zeroed per target partition; must be non-negative, and 0 disables the wipe phase.
    /// <para>每个目标分区被清零的前导扇区数；必须非负，0 表示禁用 wipe 阶段。</para></value>
    public long WipeFirstSectors { get; set; }

    /// <summary>
    /// Gets or sets the physical partitions the wipe phase targets; <c>null</c> wipes every
    /// physical partition touched by the bundle.
    /// <para>获取或设置 wipe 阶段目标的物理分区；<c>null</c> 表示对包内涉及的所有物理分区
    /// 执行擦写。</para>
    /// </summary>
    /// <value>Explicit partition list (duplicates are collapsed), or <see langword="null"/> / empty to target every partition the bundle touches.
    /// <para>显式分区列表（重复项会被去重），或为 <see langword="null"/>/空以针对包涉及的所有分区。</para></value>
    public int[]? WipePhysicalPartitions { get; set; }

    /// <summary>
    /// Gets or sets whether to commit the UFS provisioning configuration; <c>null</c> uses the
    /// <c>commit</c> attribute parsed from the provisioning XML.
    /// <para>获取或设置是否提交 UFS 配置；<c>null</c> 使用从配置 XML 解析的 <c>commit</c> 属性。</para>
    /// </summary>
    /// <value>Explicit commit decision, or <see langword="null"/> to derive it from the epilogue commit attribute (1 means commit).
    /// <para>显式的提交决定；为 <see langword="null"/> 时按 epilogue 的 commit 属性推导（1 表示提交）。</para></value>
    public bool? UfsCommit { get; set; }
}