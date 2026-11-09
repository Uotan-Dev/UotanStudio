using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Which fastboot branch answered a device-info read: a generic gadget reports the partition table, while the SpacemiT
/// BootROM FDL stage has no partition table at all and only answers the boot-chain variables the vendor script asks for.
/// <para>一次设备信息读取由哪条 fastboot 分支应答：通用 gadget 上报分区表，而进迭时空 BootROM FDL 阶段根本没有分区表，
/// 只应答厂商脚本所问的引导链变量。</para>
/// </summary>
public enum FastbootInfoBranch
{
    /// <summary>
    /// A fastboot endpoint that is not the SpacemiT gadget, or one whose stage could not be established: the standard
    /// read of <c>getvar all</c> and its <c>partition-size:*</c> keys.
    /// <para>不是进迭时空 gadget、或阶段无法确定的 fastboot 端点：按标准做法读 <c>getvar all</c> 及其
    /// <c>partition-size:*</c> 键。</para>
    /// </summary>
    Generic,

    /// <summary>
    /// The SpacemiT BootROM download mode: it answers <c>version-brom</c> and the storage sizes, has no partition table
    /// yet, and cannot be asked for <c>getvar all</c>.
    /// <para>进迭时空 BootROM 下载模式：应答 <c>version-brom</c> 与存储容量，尚无分区表，也不能被要求执行
    /// <c>getvar all</c>。</para>
    /// </summary>
    SpacemiTFdl,

    /// <summary>
    /// The SpacemiT U-Boot fastboot gadget: it answers the standard variables plus the package's storage sizes, and its
    /// <c>getvar all</c> lists the partitions the board currently has.
    /// <para>进迭时空 U-Boot fastboot gadget：应答标准变量与包的存储容量，其 <c>getvar all</c> 列出板卡当前拥有的分区。</para>
    /// </summary>
    SpacemiTUboot
}

/// <summary>
/// One labelled device fact, carrying the resource key of its label so the page renders it in the current language.
/// <para>一条带标签的设备事实；标签以资源键承载，页面据此按当前语言渲染。</para>
/// </summary>
/// <param name="LabelKey">Resource key of the label, for example <c>Flash_FastbootInfo_BromVersion</c>.
/// <para>标签的资源键，例如 <c>Flash_FastbootInfo_BromVersion</c>。</para></param>
/// <param name="Value">The value the device reported, verbatim. <para>设备上报的原样值。</para></param>
public sealed record FastbootInfoFact(string LabelKey, string Value);

/// <summary>
/// What one device-info read produced: the branch that answered, the stage it was read at, the labelled facts and the
/// partitions the branch could list.
/// <para>一次设备信息读取的结果：应答的分支、读取时所处的阶段、带标签的事实，以及该分支能列出的分区。</para>
/// </summary>
/// <param name="Branch">The branch that answered. <para>应答的分支。</para></param>
/// <param name="Stage">The boot stage the read established. <para>本次读取确定的启动阶段。</para></param>
/// <param name="Facts">Labelled facts, in display order; empty when the branch reports none.
/// <para>按显示顺序排列的带标签事实；该分支不报事实时为空。</para></param>
/// <param name="Partitions">Partitions the branch could list; empty for the FDL stage, which has no table, and for an
/// endpoint that refuses <c>getvar all</c>, which is the only way fastboot enumerates them.
/// <para>该分支能列出的分区；FDL 阶段没有分区表，拒绝 <c>getvar all</c>（fastboot 唯一的枚举方式）的端点同样为空。</para></param>
public sealed record FastbootDeviceInfo(
    FastbootInfoBranch Branch,
    SpacemiTBootStage Stage,
    IReadOnlyList<FastbootInfoFact> Facts,
    IReadOnlyList<(string Name, long Size)> Partitions)
{
    /// <summary>
    /// Whether the endpoint actually enumerated a partition table, which only <c>getvar all</c> can supply. It stays
    /// <see langword="false"/> for the BootROM FDL stage, which has no table, and for an endpoint that refuses the
    /// variable dump, so a caller never reads an unavailable list as a device with no partitions.
    /// <para>端点是否真正枚举出分区表（只有 <c>getvar all</c> 能提供）。对没有分区表的 BootROM FDL 阶段与拒绝变量转储的
    /// 端点保持 <see langword="false"/>，调用方因此不会把“不可用”读成“设备没有分区”。</para>
    /// </summary>
    public bool PartitionTableAvailable { get; init; }
}

/// <summary>
/// Reads device information from a fastboot endpoint through the branch that endpoint actually implements. A generic
/// gadget keeps the standard read; the SpacemiT board is split by boot stage, because its BootROM FDL only answers the
/// boot-chain variables (no <c>getvar all</c>, no partition table) while its U-Boot gadget answers everything the
/// standard path needs plus the package's storage sizes.
/// <para>通过端点真正实现的分支读取 fastboot 设备信息。通用 gadget 沿用标准读取；进迭时空板卡按启动阶段拆分，因为其
/// BootROM FDL 只应答引导链变量（没有 <c>getvar all</c>，也没有分区表），而其 U-Boot gadget 应答标准路径所需的全部内容
/// 以及包的存储容量。</para>
/// </summary>
/// <remarks>
/// The live probe decides the branch, not the enumeration hint: the descriptor serial only separates the two SpacemiT
/// stages until the interface is claimed, and the vendor's own flow asks <c>getvar:version-brom</c> for the answer. A
/// gadget whose probe answers nothing falls back to the standard branch, so an unreadable board still produces the
/// device's own error instead of a fabricated FDL report.
/// <para>分支由实时探测决定而不是枚举线索：描述符序列号只能在认领接口之前区分两个进迭时空阶段，厂商流程本身也是用
/// <c>getvar:version-brom</c> 询问答案。探测无应答的 gadget 回退到标准分支，因此读不到的板卡仍会给出设备自身的错误，
/// 而不是一份编造的 FDL 报告。</para>
/// </remarks>
public static class FastbootDeviceInfoService
{
    /// <summary>
    /// Variables the BootROM FDL stage is asked for, in display order. The first three are what the vendor script itself
    /// reads; the rest are reported by some FDL builds and are read only when they answer.
    /// <para>向 BootROM FDL 阶段询问的变量，按显示顺序排列。前三个是厂商脚本自己读取的内容；其余由部分 FDL 固件上报，
    /// 仅在确实应答时才读取。</para>
    /// </summary>
    private static readonly (string Variable, string LabelKey)[] FdlVariables =
    [
        (SpacemiTConstants.VarBromVersion, "Flash_FastbootInfo_BromVersion"),
        (SpacemiTConstants.VarMtdSize, "Flash_FastbootInfo_MtdSize"),
        (SpacemiTConstants.VarBlockSize, "Flash_FastbootInfo_BlockSize"),
        ("product", "Flash_FastbootInfo_Product"),
        ("serialno", "Flash_FastbootInfo_Serial")
    ];

    /// <summary>
    /// Variables a fastboot endpoint reports, in display order: the standard identity facts plus the two storage sizes
    /// the SpacemiT package selects its partition tables with. The U-Boot gadget answers <c>getvar all</c>, so these are
    /// picked out of that single answer; a gadget that refuses <c>getvar all</c> is asked for them one at a time.
    /// <para>fastboot 端点上报的变量，按显示顺序排列：标准身份事实，加上进迭时空包用来选择分区表的两个存储容量。U-Boot
    /// gadget 应答 <c>getvar all</c>，因此这些值从同一次应答中挑出；拒绝 <c>getvar all</c> 的 gadget 则逐个询问它们。</para>
    /// </summary>
    private static readonly (string Variable, string LabelKey)[] StandardVariables =
    [
        ("version", "Flash_FastbootInfo_UbootVersion"),
        ("product", "Flash_FastbootInfo_Product"),
        ("bootloader", "Flash_FastbootInfo_Bootloader"),
        ("serialno", "Flash_FastbootInfo_Serial"),
        (SpacemiTConstants.VarMtdSize, "Flash_FastbootInfo_MtdSize"),
        (SpacemiTConstants.VarBlockSize, "Flash_FastbootInfo_BlockSize")
    ];

    /// <summary>
    /// Reads device information through the branch the endpoint implements.
    /// <para>通过端点实现的分支读取设备信息。</para>
    /// </summary>
    /// <remarks>
    /// <c>getvar all</c> is the only way fastboot enumerates partitions, and not every gadget implements it — the
    /// SpacemiT BootROM FDL answers "Variable not implemented" to it while still answering the individual variables the
    /// vendor script asks for. Such an endpoint is read variable by variable instead of failing the whole read, and the
    /// result says the partition list was unavailable so the page never reports an empty list as if the device had none.
    /// <para><c>getvar all</c> 是 fastboot 枚举分区的唯一方式，而并非每个 gadget 都实现它——进迭时空 BootROM FDL 对它的
    /// 应答是 “Variable not implemented”，但仍会应答厂商脚本询问的各个变量。这类端点改为逐个变量读取而不是让整次读取失败，
    /// 结果里标注分区列表不可用，页面因此不会把空列表当作设备确实没有分区来汇报。</para>
    /// </remarks>
    /// <param name="device">The selected fastboot device. <para>所选 fastboot 设备。</para></param>
    /// <param name="showAll">When true, keep physical partitions when the partition list can be read.
    /// <para>为真时在能读到分区列表的情况下保留物理分区。</para></param>
    /// <param name="ct">Token cancelling the read. <para>取消读取的令牌。</para></param>
    /// <returns>The branch that answered and what it could report. <para>应答的分支及它能上报的内容。</para></returns>
    /// <exception cref="ArgumentException">The device is not a fastboot endpoint.
    /// <para>该设备不是 fastboot 端点。</para></exception>
    /// <exception cref="FastbootException">The endpoint refuses <c>getvar all</c> and answers none of the individual
    /// variables either. <para>端点拒绝 <c>getvar all</c>，且逐个变量也全无应答。</para></exception>
    public static Task<FastbootDeviceInfo> ReadAsync(DetectedDevice device, bool showAll, CancellationToken ct)
        => ReadCoreAsync(device, showAll, includePartitions: true, ct);

    /// <summary>
    /// Reads the facts a fastboot endpoint reports without asking for its partition table.
    /// <para>只读取 fastboot 端点上报的事实，不请求其分区表。</para>
    /// </summary>
    /// <remarks>
    /// The variable dump the facts come from is also the only source of the partition table, so this read still asks
    /// for it; what it never does is turn those keys into a partition list or query <c>is-userspace</c> for one, which
    /// keeps an information read free of the partition backfill only the partition-table action performs.
    /// <para>事实所依据的变量转储同时也是分区表的唯一来源，因此这次读取仍会请求它；它绝不把那些键解析成分区列表，也不为
    /// 了列表去查询 <c>is-userspace</c>，从而让信息读取与只有分区表动作负责的分区回填彻底分开。</para>
    /// </remarks>
    /// <param name="device">The selected fastboot device. <para>所选 fastboot 设备。</para></param>
    /// <param name="ct">Token cancelling the read. <para>取消读取的令牌。</para></param>
    /// <returns>The branch that answered and the facts it reported, with no partition list.
    /// <para>应答的分支及它上报的事实，不含分区列表。</para></returns>
    /// <exception cref="ArgumentException">The device is not a fastboot endpoint.
    /// <para>该设备不是 fastboot 端点。</para></exception>
    /// <exception cref="FastbootException">The endpoint refuses <c>getvar all</c> and answers none of the individual
    /// variables either. <para>端点拒绝 <c>getvar all</c>，且逐个变量也全无应答。</para></exception>
    public static Task<FastbootDeviceInfo> ReadFactsAsync(DetectedDevice device, CancellationToken ct)
        => ReadCoreAsync(device, showAll: false, includePartitions: false, ct);

    /// <summary>
    /// Runs one read over a single leased session, optionally turning the variable dump into a partition list.
    /// <para>在一次租用的会话上完成读取，并可选地把变量转储解析成分区列表。</para>
    /// </summary>
    /// <param name="device">The selected fastboot device. <para>所选 fastboot 设备。</para></param>
    /// <param name="showAll">Whether physical partitions are kept. <para>是否保留物理分区。</para></param>
    /// <param name="includePartitions">Whether the variable dump is parsed into the partition list.
    /// <para>是否把变量转储解析成分区列表。</para></param>
    /// <param name="ct">Token cancelling the read. <para>取消读取的令牌。</para></param>
    /// <returns>The branch that answered and what this read asked for.
    /// <para>应答的分支及本次读取所请求的内容。</para></returns>
    private static async Task<FastbootDeviceInfo> ReadCoreAsync(DetectedDevice device, bool showAll,
        bool includePartitions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol != DeviceProtocolKind.Fastboot)
        {
            throw new ArgumentException("A Fastboot device is required.", nameof(device));
        }

        var isSpacemiT = IsSpacemiTGadget(device);
        return await Task.Run(() => App.DeviceMonitor.RunFastboot(device, session =>
        {
            ct.ThrowIfCancellationRequested();
            var stage = isSpacemiT ? SpacemiTBootStageClassifier.Probe(session) : SpacemiTBootStage.Unknown;
            if (stage == SpacemiTBootStage.Unknown && isSpacemiT)
            {
                stage = device.FastbootStage;
            }

            Dictionary<string, string>? variables;
            FastbootException? refusal = null;
            try
            {
                variables = session.GetAllVariables();
            }
            catch (FastbootException exception)
            {
                variables = null;
                refusal = exception;
            }

            ct.ThrowIfCancellationRequested();
            if (variables is null)
            {
                return ReadWithoutVariableList(session, device, isSpacemiT, stage, refusal, ct);
            }

            var branch = ResolveBranch(isSpacemiT, stage);
            if (branch == FastbootInfoBranch.SpacemiTFdl || !includePartitions)
            {
                return new FastbootDeviceInfo(branch, stage, BuildFacts(branch, variables), []);
            }

            var userspace = variables.TryGetValue("is-userspace", out var reported)
                ? reported
                : session.GetVariable("is-userspace", useCache: false);
            ct.ThrowIfCancellationRequested();
            return new FastbootDeviceInfo(branch, stage, BuildFacts(branch, variables),
                FastbootPartitionReader.Parse(variables,
                    logicalOnly: !showAll && userspace.Equals("yes", StringComparison.OrdinalIgnoreCase)))
            {
                PartitionTableAvailable = true
            };
        }, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an endpoint that refused <c>getvar all</c>: the boot-chain discriminator decides whether it is the BootROM
    /// FDL, and the remaining variables are probed one by one so the page can still show what the gadget reports.
    /// <para>读取拒绝 <c>getvar all</c> 的端点：先由引导链判据确定它是否为 BootROM FDL，再逐个探测其余变量，使页面仍能显示
    /// 该 gadget 上报的内容。</para>
    /// </summary>
    /// <param name="session">The leased session. <para>已租用的会话。</para></param>
    /// <param name="device">The selected device, for its enumeration hint. <para>所选设备，用于其枚举线索。</para></param>
    /// <param name="isSpacemiT">Whether the endpoint is the vendor gadget. <para>端点是否为厂商 gadget。</para></param>
    /// <param name="stage">The stage established so far. <para>目前确定的阶段。</para></param>
    /// <param name="refusal">The exception <c>getvar all</c> raised, rethrown when nothing answers.
    /// <para><c>getvar all</c> 抛出的异常；全无应答时重新抛出它。</para></param>
    /// <param name="ct">Token cancelling between probes. <para>在探测之间取消的令牌。</para></param>
    /// <returns>The facts the endpoint answered, with no partition list and <see cref="FastbootDeviceInfo.PartitionTableAvailable"/>
    /// left false. <para>端点应答的事实，不含分区列表，且 <see cref="FastbootDeviceInfo.PartitionTableAvailable"/> 保持 false。</para></returns>
    private static FastbootDeviceInfo ReadWithoutVariableList(FastbootSession session, DetectedDevice device,
        bool isSpacemiT, SpacemiTBootStage stage, FastbootException? refusal, CancellationToken ct)
    {
        if (stage == SpacemiTBootStage.Unknown)
        {
            stage = SpacemiTBootStageClassifier.Probe(session);
            if (stage == SpacemiTBootStage.Unknown && isSpacemiT)
            {
                stage = device.FastbootStage;
            }
        }

        var branch = ResolveBranch(isSpacemiT, stage);
        var fdl = branch == FastbootInfoBranch.SpacemiTFdl;
        var answered = ProbeVariables(session, fdl ? FdlVariables : StandardVariables, ct);
        if (answered.Count == 0)
        {
            throw refusal ?? new FastbootException(FastbootResponseState.Fail,
                "The device answered neither 'getvar all' nor the individual fastboot variables.");
        }

        return new FastbootDeviceInfo(branch, stage,
            fdl ? BuildFacts(branch, answered) : BuildStandardFacts(answered), []);
    }

    /// <summary>
    /// Whether the endpoint is the SpacemiT K1/K3 fastboot gadget, which is the only family whose boot stage changes
    /// what its fastboot interface can answer.
    /// <para>判断端点是否为进迭时空 K1/K3 fastboot gadget；只有该系列会因启动阶段改变其 fastboot 接口能应答的内容。</para>
    /// </summary>
    /// <param name="device">The device to classify. <para>要分类的设备。</para></param>
    /// <returns><see langword="true"/> for the vendor's vid/pid pair. <para>厂商 vid/pid 对时为 <see langword="true"/>。</para></returns>
    internal static bool IsSpacemiTGadget(DetectedDevice device) =>
        device.Protocol == DeviceProtocolKind.Fastboot &&
        device.VendorId == SpacemiTConstants.UsbVendorId &&
        device.ProductId == SpacemiTConstants.UsbProductId;

    /// <summary>
    /// Chooses the branch for an endpoint. The BootROM FDL is decided by the wire alone: <c>version-brom</c> is a vendor
    /// key, so a gadget that answers it is that stage whatever its descriptors said. The U-Boot branch additionally
    /// needs the vendor's vid/pid pair, because a generic bootloader answering <c>version</c> is not that gadget, and an
    /// unknown stage keeps the standard read since nothing proves the boot-chain variables are the ones to ask.
    /// <para>为端点选择分支。BootROM FDL 只由线路判定：<c>version-brom</c> 是厂商键，因此应答它的 gadget 就是该阶段，无论其
    /// 描述符怎么写。U-Boot 分支还要求厂商 vid/pid 对，因为应答 <c>version</c> 的通用 bootloader 并不是该 gadget；阶段未知时
    /// 保持标准读取，因为没有任何证据表明该问的是引导链变量。</para>
    /// </summary>
    /// <param name="isSpacemiTGadget">Whether the endpoint is the vendor gadget. <para>端点是否为厂商 gadget。</para></param>
    /// <param name="stage">The stage established for it. <para>为它确定的阶段。</para></param>
    /// <returns>The branch to read through. <para>要用于读取的分支。</para></returns>
    internal static FastbootInfoBranch ResolveBranch(bool isSpacemiTGadget, SpacemiTBootStage stage) => stage switch
    {
        SpacemiTBootStage.BromFdl => FastbootInfoBranch.SpacemiTFdl,
        SpacemiTBootStage.Uboot when isSpacemiTGadget => FastbootInfoBranch.SpacemiTUboot,
        _ => FastbootInfoBranch.Generic
    };

    /// <summary>
    /// Asks for the given variables one at a time: used where no variable dump is available, and the only way to read a
    /// gadget that answers individual keys but refuses <c>getvar all</c>. A refusal simply means the firmware does not
    /// report that variable, so it never aborts the rest of the read.
    /// <para>逐个询问给定变量：在没有变量转储可用时使用，也是读取“应答单个键但拒绝 <c>getvar all</c>”的 gadget 的唯一方式。
    /// 拒绝只表示该固件不上报这个变量，因此绝不中断其余读取。</para>
    /// </summary>
    /// <param name="session">The leased session. <para>已租用的会话。</para></param>
    /// <param name="definitions">The variables and their label keys. <para>变量及其标签键。</para></param>
    /// <param name="ct">Token cancelling between probes. <para>在探测之间取消的令牌。</para></param>
    /// <returns>The variables that answered, keyed by variable name.
    /// <para>有应答的变量，以变量名为键。</para></returns>
    private static Dictionary<string, string> ProbeVariables(FastbootSession session,
        (string Variable, string LabelKey)[] definitions, CancellationToken ct)
    {
        var answered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (variable, _) in definitions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var probe = session.GetVarWithResult(variable, useCache: false);
                if (probe.Success && probe.Value is { Length: > 0 } value)
                {
                    answered[variable] = value;
                }
            }
            catch (Exception exception) when (exception is FastbootException or IOException or TimeoutException
                or InvalidOperationException or NotSupportedException)
            {
            }
        }

        return answered;
    }

    /// <summary>
    /// Picks the labelled facts a branch is defined to report, in the order that makes the stage readable at a glance.
    /// Values the device left empty, and storage answers of <c>NULL</c> — the vendor's marker for a storage kind the
    /// board does not carry — are dropped instead of reported as facts about hardware that is not there.
    /// <para>按一眼可读的顺序挑出该分支约定上报的带标签事实。设备留空的值，以及 <c>NULL</c> 这一厂商用来表示板卡未装配该类
    /// 存储的应答，都被丢弃，而不是当作关于并不存在的硬件的事实上报。</para>
    /// </summary>
    /// <param name="branch">The branch being read. <para>正在读取的分支。</para></param>
    /// <param name="variables">The variables that branch reported. <para>该分支上报的变量。</para></param>
    /// <returns>The facts to display; empty for the generic branch. <para>要显示的事实；通用分支为空。</para></returns>
    internal static List<FastbootInfoFact> BuildFacts(FastbootInfoBranch branch,
        IReadOnlyDictionary<string, string> variables)
    {
        var definitions = branch switch
        {
            FastbootInfoBranch.SpacemiTFdl => FdlVariables,
            FastbootInfoBranch.SpacemiTUboot => StandardVariables,
            _ => []
        };
        return PickFacts(definitions, variables);
    }

    /// <summary>
    /// Picks the standard facts only, for an endpoint read without a variable dump: a generic gadget that refuses
    /// <c>getvar all</c> still reports these keys, and the page shows them instead of an empty table.
    /// <para>仅挑出标准事实，用于没有变量转储的读取：拒绝 <c>getvar all</c> 的通用 gadget 仍会应答这些键，页面因此显示它们
    /// 而不是一张空表。</para>
    /// </summary>
    /// <param name="variables">The variables the endpoint answered. <para>端点应答的变量。</para></param>
    /// <returns>The facts to display. <para>要显示的事实。</para></returns>
    internal static List<FastbootInfoFact> BuildStandardFacts(IReadOnlyDictionary<string, string> variables) =>
        PickFacts(StandardVariables, variables);

    /// <summary>
    /// Keeps the values the endpoint actually reported, in the definitions' order.
    /// <para>按定义顺序保留端点确实上报的取值。</para>
    /// </summary>
    /// <param name="definitions">The variables and their label keys. <para>变量及其标签键。</para></param>
    /// <param name="variables">Everything the endpoint reported. <para>端点上报的全部内容。</para></param>
    /// <returns>The facts to display. <para>要显示的事实。</para></returns>
    private static List<FastbootInfoFact> PickFacts((string Variable, string LabelKey)[] definitions,
        IReadOnlyDictionary<string, string> variables)
    {
        var facts = new List<FastbootInfoFact>(definitions.Length);
        foreach (var (variable, labelKey) in definitions)
        {
            if (!variables.TryGetValue(variable, out var value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            facts.Add(new FastbootInfoFact(labelKey, trimmed));
        }

        return facts;
    }
}