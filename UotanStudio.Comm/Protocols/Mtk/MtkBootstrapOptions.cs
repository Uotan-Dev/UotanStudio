using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Identifies the stock DA protocol selected by the caller's loader profile.
/// <para>标识调用方加载器配置选定的标准 DA 协议。</para></summary>
public enum MtkDaProtocol
{
    /// <summary>The packet-based XFlash protocol.
    /// <para>基于分包的 XFlash 协议。</para></summary>
    XFlash,
    /// <summary>The XML command protocol.
    /// <para>XML 命令协议。</para></summary>
    Xml,
    /// <summary>The non-IoT Legacy DA byte protocol.
    /// <para>非 IoT Legacy DA 字节协议。</para></summary>
    Legacy
}

/// <summary>
/// Explicit version policy for an already selected loader entry, not automatic device compatibility proof.
/// <para>针对已选定加载器条目的显式版本策略，而非设备兼容性的自动证明。</para>
/// </summary>
public enum MtkDaIdentityMatch
{
    /// <summary>
    /// Hardware/subcode and both revision fields must match exactly.
    /// <para>硬件码/子码以及两项版本字段都必须完全一致。</para>
    /// </summary>
    Exact,
    /// <summary>
    /// Hardware/subcode must match; device revisions must meet the entry's minimum revisions.
    /// <para>硬件码/子码必须一致；设备版本必须达到条目的最低版本要求。</para>
    /// </summary>
    MinimumRevisions
}

/// <summary>
/// Caller-selected resources and policy for one explicitly selected stock DA startup attempt.
/// <para>调用方为一次显式选择的标准 DA 启动尝试所选定的资源和策略。</para>
/// </summary>
public record MtkBootstrapOptions
{
    /// <summary>Gets the XML startup policy, required only for the XML protocol.
    /// <para>获取 XML 启动策略，仅在使用 XML 协议时必需。</para></summary>
    /// <value>The explicit XML settings, or null for XFlash.
    /// <para>显式 XML 设置；XFlash 使用 null。</para></value>
    public MtkXmlBootstrapOptions? Xml { get; init; }
    /// <summary>Gets the explicit non-IoT Legacy wire profile.
    /// <para>获取显式的非 IoT Legacy 线协议配置。</para></summary>
    /// <value>The profile required for Legacy eMMC, NOR or NAND startup; null for XML and XFlash.
    /// <para>Legacy eMMC、NOR 或 NAND 启动所需的配置；XML 和 XFlash 使用 null。</para></value>
    public MtkLegacyOptions? Legacy { get; init; }
    /// <summary>
    /// Gets the explicitly selected loader path.
    /// <para>获取调用方显式选定的加载器文件路径。</para>
    /// </summary>
    /// <value>
    /// The file path opened from disk during startup.
    /// <para>启动期间从磁盘打开的文件路径。</para>
    /// </value>
    public required string LoaderPath { get; init; }
    /// <summary>
    /// Gets the expected directory layout or unambiguous detection policy.
    /// <para>获取预期的目录布局，或无歧义的自动检测策略。</para>
    /// </summary>
    /// <value>
    /// The layout hint applied when the loader file is opened.
    /// <para>打开加载器文件时应用的布局提示。</para>
    /// </value>
    public MtkDaDirectoryLayout DirectoryLayout { get; init; } = MtkDaDirectoryLayout.Auto;
    /// <summary>
    /// Gets the zero-based loader entry index.
    /// <para>获取从零开始计的加载器条目索引。</para>
    /// </summary>
    /// <value>
    /// The entry whose regions and identity are used for the attempt.
    /// <para>用于本次尝试、提供区域与身份信息的目标条目索引。</para>
    /// </value>
    public required int EntryIndex { get; init; }
    /// <summary>
    /// Gets the DA1 region index; its complete bytes and declared signature length are sent.
    /// <para>获取 DA1 区域索引；将发送其完整字节和声明的签名长度。</para>
    /// </summary>
    /// <value>
    /// The region index read from the selected entry for the first stage.
    /// <para>为第一阶段从选定条目读取的区域索引。</para>
    /// </value>
    public required int StageOneRegionIndex { get; init; }
    /// <summary>
    /// Gets the distinct DA2 region index.
    /// <para>获取与之不同的 DA2 区域索引。</para>
    /// </summary>
    /// <value>
    /// The region index read from the selected entry for the second stage.
    /// <para>为第二阶段从选定条目读取的区域索引。</para>
    /// </value>
    public required int StageTwoRegionIndex { get; init; }
    /// <summary>
    /// Gets the explicit DA2 signature-tail inclusion policy.
    /// <para>获取显式的 DA2 签名尾部包含策略。</para>
    /// </summary>
    /// <value>
    /// Whether the signature tail is included when the second-stage region is read.
    /// <para>读取第二阶段区域时是否包含签名尾部。</para>
    /// </value>
    public required MtkDaRegionContent StageTwoContent { get; init; }
    /// <summary>
    /// Gets the identity matching policy; no wildcard hardware/subcode match is permitted.
    /// <para>获取身份匹配策略；不允许任何硬件码/子码的通配匹配。</para>
    /// </summary>
    /// <value>
    /// The revision comparison mode applied after the hardware identity query.
    /// <para>在硬件身份查询之后应用的版本比较模式。</para>
    /// </value>
    public MtkDaIdentityMatch IdentityMatch { get; init; } = MtkDaIdentityMatch.Exact;
    /// <summary>
    /// Gets the selected storage family; no fallback family is inferred.
    /// <para>获取选定的存储家族；不会推断任何回退家族。</para>
    /// </summary>
    /// <value>
    /// The storage family the agent must report during discovery.
    /// <para>发现阶段代理必须上报的存储家族。</para>
    /// </value>
    public required MtkXFlashStorageKind StorageKind { get; init; }
    /// <summary>
    /// Gets optional explicitly extracted EMI, required for the XFlash Boot ROM path
    /// or a Legacy agent requesting DRAM parameters.
    /// XML uses its host file store instead and rejects this setting.
    /// <para>获取可选的、显式提取的 EMI 参数；XFlash Boot ROM 路径或请求 DRAM 参数的 Legacy 代理必须提供。
    /// XML 改用其主机文件存储，并拒绝此设置。</para>
    /// </summary>
    /// <value>
    /// The selected EMI parameter source, or <see langword="null"/> when no explicit DRAM resource is supplied.
    /// <para>选定的 EMI 参数来源；未提供显式 DRAM 资源时为 <see langword="null"/>。</para>
    /// </value>
    public MtkEmiParameters? EmiParameters { get; init; }
    /// <summary>
    /// Gets the UART log level sent to XFlash DA1. XML uses its runtime settings.
    /// <para>获取发送给 XFlash DA1 的 UART 日志级别。XML 使用其运行时设置。</para>
    /// </summary>
    /// <value>
    /// The numeric verbosity applied to the initialized agent.
    /// <para>应用于已初始化代理的数值化详细级别。</para>
    /// </value>
    public uint DaLogLevel { get; init; } = 2;
    /// <summary>
    /// Gets the per-transfer timeout in milliseconds.
    /// <para>获取每次传输的超时时间（毫秒）。</para>
    /// </summary>
    /// <value>
    /// The positive time budget applied to each data transfer.
    /// <para>应用于每次数据传输的正数时间预算。</para>
    /// </value>
    public int TransferTimeoutMs { get; init; } = 5000;
    /// <summary>
    /// Gets the boot handshake's per-byte timeout in milliseconds.
    /// <para>获取 boot 握手的逐字节超时时间（毫秒）。</para>
    /// </summary>
    /// <value>
    /// The positive per-byte budget used during the initial complemented-byte handshake.
    /// <para>在初始反码字节握手中使用的正数逐字节预算。</para>
    /// </value>
    public int HandshakeTimeoutMs { get; init; } = 100;
    /// <summary>
    /// Gets the explicit provider for the boot byte-stream profile. No provider is invoked
    /// unless BootResourceOrder selects resources; this does not implement packet/ZLP USB authentication.
    /// <para>获取 boot 字节流配置的显式提供程序。除非 BootResourceOrder 选择了资源，否则不会调用任何提供程序；
    /// 这并不实现 packet/ZLP USB 认证。</para>
    /// </summary>
    /// <value>
    /// The boot resource provider, or <see langword="null"/> when no boot resources are selected.
    /// <para>boot 资源提供程序；未选择任何 boot 资源时为 <see langword="null"/>。</para>
    /// </value>
    public IMtkBootResourceProvider? BootResourceProvider { get; init; }
    /// <summary>
    /// Gets zero to two distinct resource kinds in caller-selected upload order, captured
    /// before startup. The provider must be present exactly when this selection is nonempty.
    /// The caller must not mutate the collection during initial synchronous capture.
    /// <para>获取零到两个互不相同的资源种类，按调用方选定的上传顺序排列，并在启动前完成快照捕获。
    /// 当且仅当该选择非空时必须提供提供程序。在初始同步捕获期间调用方不得修改该集合。</para>
    /// </summary>
    /// <value>
    /// The caller-selected resource kinds in upload order; empty when no boot resources are used.
    /// <para>按上传顺序排列的调用方选定资源种类；不使用 boot 资源时为空。</para>
    /// </value>
    public IReadOnlyList<MtkBootResourceKind> BootResourceOrder { get; init; } = [];
    /// <summary>
    /// Gets an optional explicit SLA responder, invoked only if the DA1 request reports 0x1D0D.
    /// <para>获取可选的显式 SLA 应答器；仅当 DA1 请求上报 0x1D0D 时才会被调用。</para>
    /// </summary>
    /// <value>
    /// The SLA responder, or <see langword="null"/> to reject the exchange when 0x1D0D appears.
    /// <para>SLA 应答器；为 <see langword="null"/> 时，出现 0x1D0D 将拒绝该交换。</para>
    /// </value>
    public IMtkBootRomSlaResponder? BootRomSlaResponder { get; init; }
    /// <summary>
    /// Gets the authorized XFlash/XML DA2 signing provider, invoked only when DA2 reports SLA enabled.
    /// Legacy profiles reject this setting.
    /// <para>获取授权的 XFlash/XML DA2 签名提供方，仅在 DA2 报告启用 SLA 时调用。
    /// Legacy 配置拒绝此设置。</para>
    /// </summary>
    /// <value>The provider, or null to reject enabled SLA without submitting a response.
    /// <para>签名提供方；为 null 时，在启用 SLA 的情况下停止且不提交响应。</para></value>
    public IMtkDaSlaResponder? DaSlaResponder { get; init; }
    /// <summary>
    /// Gets the cooperative SLA exchange deadline in milliseconds; no retry occurs on expiry.
    /// <para>获取协作式 SLA 交换的截止时间（毫秒）；超时后不会重试。</para>
    /// </summary>
    /// <value>
    /// The positive whole-exchange budget for the SLA handshake.
    /// <para>SLA 握手整个交换过程的正数时间预算。</para>
    /// </value>
    public int SlaTimeoutMs { get; init; } = 60000;

    /// <summary>
    /// Builds startup options from an evaluated loader profile selection, carrying the entry and
    /// region indices decided by hardware identity rather than by caller-counted indices. The
    /// selection must be a single match; every weaker decision is rejected here so no bootstrap
    /// attempt starts from an unproven pairing. Companion resources (EMI, XML settings, SLA
    /// responders) are added by the caller through record <c>with</c> expressions.
    /// <para>从已评估的加载器配置选择结果构建启动选项，携带由硬件身份而非调用方手数索引决定的条目与区域索引。
    /// 选择结果必须是唯一匹配；任何更弱的决策都会在此被拒绝，使未证实的配对无法开始启动尝试。
    /// 伴随资源（EMI、XML 设置、SLA 应答器）由调用方通过记录 <c>with</c> 表达式补充。</para>
    /// </summary>
    /// <param name="profile">
    /// The parsed, hashed loader profile the selection was evaluated against.
    /// <para>评估该选择时所依据的、已解析并哈希的加载器配置。</para>
    /// </param>
    /// <param name="selection">
    /// The evaluated selection; must report <see cref="MtkProfileDecision.SingleMatch"/>.
    /// <para>已评估的选择结果；必须报告 <see cref="MtkProfileDecision.SingleMatch"/>。</para>
    /// </param>
    /// <param name="storageKind">
    /// The explicitly selected storage family. <para>显式选定的存储家族。</para>
    /// </param>
    /// <param name="identityMatch">
    /// The revision policy that produced the selection. <para>产生该选择的版本策略。</para>
    /// </param>
    /// <returns>
    /// Options wired to the selected entry and validated region pair.
    /// <para>已接到所选条目与经校验区域配对的选项。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="profile"/> or <paramref name="selection"/> is null.
    /// <para><paramref name="profile"/> 或 <paramref name="selection"/> 为 null。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The selection is not a single match. <para>选择结果不是唯一匹配。</para>
    /// </exception>
    public static MtkBootstrapOptions From(MtkLoaderProfile profile, MtkProfileSelection selection,
        MtkXFlashStorageKind storageKind, MtkDaIdentityMatch identityMatch = MtkDaIdentityMatch.Exact)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Decision != MtkProfileDecision.SingleMatch
            || selection.EntryIndex is not { } entryIndex
            || selection.StageOneRegionIndex is not { } stageOne
            || selection.StageTwoRegionIndex is not { } stageTwo)
        {
            throw new InvalidOperationException(
                $"Bootstrap options require a single-match profile selection; the evaluation returned {selection.Decision}.");
        }

        return new MtkBootstrapOptions
        {
            LoaderPath = profile.FilePath,
            EntryIndex = entryIndex,
            StageOneRegionIndex = stageOne,
            StageTwoRegionIndex = stageTwo,
            StageTwoContent = MtkDaRegionContent.WithoutSignatureSuffix,
            IdentityMatch = identityMatch,
            StorageKind = storageKind
        };
    }
}