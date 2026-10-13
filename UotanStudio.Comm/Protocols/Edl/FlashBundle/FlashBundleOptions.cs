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
    /// Gets or sets whether program entries whose source image is absent are dropped instead of
    /// failing the bundle, mirroring the per-partition selection of a package GUI.
    /// <para>获取或设置源镜像缺失的 program 条目是被丢弃还是使整包失败，对应包图形界面的逐项选择行为。</para>
    /// </summary>
    /// <value>True to skip entries whose resolved source file does not exist and record each as a warning;
    /// false makes a missing image abort preparation.
    /// <para>为 true 时跳过解析后源文件不存在的条目并逐项记录警告；为 false 时缺失镜像会使准备阶段中止。</para></value>
    public bool SkipAbsentSources { get; set; }

    /// <summary>
    /// Gets or sets whether execution continues after an individual program, erase, patch, wipe or
    /// provisioning command fails.
    /// <para>获取或设置单个 program、erase、patch、wipe 或 provisioning 命令失败后是否继续执行。</para>
    /// </summary>
    /// <value>True attempts every remaining manifest item and reports the collected failures; false stops at
    /// the first failure. The run is still marked failed when any item failed.
    /// <para>为 true 时仍会尝试其余清单条目并汇总失败；为 false 时在首个失败处停止。任一条目失败时本次运行仍记为失败。</para></value>
    public bool ContinueOnFailure { get; set; }

    /// <summary>
    /// Gets or sets whether each programmed raw entry is read back and compared byte by byte
    /// after its transfer completes.
    /// <para>获取或设置每条 raw program 条目在传输完成后是否回读并逐字节比对。</para>
    /// </summary>
    /// <value>True verifies entries whose source exactly covers the declared sector range at an
    /// absolute start sector; sparse sources, source-sector offsets, truncated or rounded images
    /// and disk-relative addresses are skipped with a recorded warning.
    /// <para>为 true 时校验源文件恰好覆盖声明扇区范围且起始扇区为绝对地址的条目；sparse 源、
    /// 源扇区偏移、被截断或向上取整的镜像以及磁盘相对地址会被跳过并记录警告。</para></value>
    public bool Verify { get; set; }

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
    /// touched by the bundle after programming/erasing
    /// (0 disables the wipe phase).
    /// <para>获取或设置编程/擦除之后，对包内涉及的所有物理分区以零擦写的前导扇区数
    /// （0 表示禁用 wipe 阶段）。</para>
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
    /// Gets or sets the caller-supplied confirmation that authorises the <c>&lt;wipe&gt;</c> requests parsed from
    /// the manifest. A wipe overwrites storage with zeros and cannot be undone, so a manifest wipe is refused,
    /// with no device write, unless the phrase is exactly <c>WIPE</c>.
    /// <para>获取或设置授权清单中解析出的 <c>&lt;wipe&gt;</c> 请求的调用方确认。擦写会用零字节覆盖存储且
    /// 无法回退，因此除非短语恰好为 <c>WIPE</c>，否则清单擦写会被拒绝，且完全不写入设备。</para>
    /// </summary>
    /// <value>The confirmation record, or <see langword="null"/> to refuse every manifest wipe request. Leading
    /// sectors governed by <see cref="WipeFirstSectors"/> are the bundle's own post-programming step and stay
    /// unaffected by this gate.
    /// <para>确认记录；为 <see langword="null"/> 时拒绝全部清单擦写请求。由 <see cref="WipeFirstSectors"/>
    /// 控制的前导扇区属于本包自身的编程后步骤，不受该门控影响。</para></value>
    public WipeConfirmation? WipeConfirmation { get; set; }

    /// <summary>
    /// Gets or sets whether a manifest wipe request carrying <c>all="true"</c> may run. Such a request erases
    /// every block of the addressed partition, including its partition table, and stays refused while this flag
    /// is <see langword="false"/> even after the confirmation gate passed.
    /// <para>获取或设置是否允许执行清单中带 <c>all="true"</c> 的擦写请求。该请求会擦除目标分区的每一个块，
    /// 包含其分区表，因此即使确认门控已通过，在本开关为 <see langword="false"/> 时仍会被拒绝。</para>
    /// </summary>
    /// <value><see langword="true"/> to authorise whole-partition wipes; <see langword="false"/> (default)
    /// refuses them as ambiguous. <para>为 <see langword="true"/> 时授权整分区擦写；默认 <see langword="false"/>
    /// 因其含义不明确而拒绝。</para></value>
    public bool AllowEntireStorageWipe { get; set; }

    /// <summary>
    /// Gets or sets whether to commit the UFS provisioning configuration; <c>null</c> uses the
    /// <c>commit</c> attribute parsed from the provisioning XML.
    /// <para>获取或设置是否提交 UFS 配置；<c>null</c> 使用从配置 XML 解析的 <c>commit</c> 属性。</para>
    /// </summary>
    /// <value>Explicit commit decision, or <see langword="null"/> to derive it from the epilogue commit attribute (1 means commit).
    /// <para>显式的提交决定；为 <see langword="null"/> 时按 epilogue 的 commit 属性推导（1 表示提交）。</para></value>
    public bool? UfsCommit { get; set; }

    /// <summary>
    /// Gets or sets the explicit write-chunk bound applied to the session configuration before the bundle
    /// runs.
    /// <para>获取或设置运行本包之前写入会话配置的显式写入块上限。</para>
    /// </summary>
    /// <value><see langword="null"/> keeps the default bound; otherwise a 512-byte multiple between 512 and
    /// 16777216, and the protocol still refuses a raised value above what the programmer reported as
    /// supported.
    /// <para><see langword="null"/> 保留默认上限；否则须为 512 的倍数且介于 512 与 16777216 之间，且协议仍会
    /// 拒绝超过 programmer 上报支持值的上调。</para></value>
    public long? MaxProgramChunkBytes { get; set; }

    /// <summary>
    /// Gets or sets the NAND <c>PAGES_PER_BLOCK</c> configure attribute taken from the operator rather than
    /// from the manifest.
    /// <para>获取或设置来自操作者而非清单的 NAND <c>PAGES_PER_BLOCK</c> configure 属性。</para>
    /// </summary>
    /// <value><see langword="null"/> or 0 omits the attribute; otherwise 1 through 65535, checked before the
    /// session is initialized so the value is on the wire in the configure exchange.
    /// <para><see langword="null"/> 或 0 表示省略该属性；否则为 1 至 65535，并在会话初始化前完成校验，使该值随
    /// configure 交互上线。</para></value>
    public long? NandPagesPerBlock { get; set; }
}