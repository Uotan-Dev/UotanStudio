using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// The device modes the flash page distinguishes when it decides which controls are usable.
/// <para>烧录页面在决定哪些控件可用时区分的设备模式。</para>
/// </summary>
internal enum FlashDeviceMode
{
    /// <summary>No device is selected, so no device-facing control is usable.
    /// <para>尚未选中设备，因此没有任何面向设备的可用控件。</para>
    /// </summary>
    None,

    /// <summary>A selected device without a dedicated protocol section; fastboot, ADB, HDC and Apple endpoints all
    /// fall in this mode, and the kind dimension decides which shared actions each of them keeps.
    /// <para>没有专属协议分类的已选设备；fastboot、ADB、HDC 与 Apple 端点都属该模式，由种类维度决定各自保留
    /// 哪些通用动作。</para>
    /// </summary>
    Shared,

    /// <summary>Qualcomm EDL (9008). <para>高通 EDL（9008）。</para></summary>
    Edl,

    /// <summary>MediaTek BootROM or download-agent mode. <para>联发科 BootROM 或下载代理模式。</para></summary>
    Mtk,

    /// <summary>Rockchip bootrom or loader (rockusb). <para>瑞芯微 bootrom/loader（rockusb）。</para></summary>
    Rkdl,

    /// <summary>A selected fastboot endpoint, which owns the SpacemiT section on top of the shared flash action.
    /// <para>已选 fastboot 端点，在通用烧录动作之外还拥有进迭时空分类。</para></summary>
    Fastboot,

    /// <summary>Allwinner FEL or FES. <para>全志 FEL 或 FES。</para></summary>
    Sunxi
}

/// <summary>
/// The protocol families the page treats with a narrower shared-action set than the mode table alone:
/// ADB and fastboot endpoints answer only the shared flash action, HDC endpoints answer no action at all,
/// while every other protocol keeps the full table behavior.
/// <para>页面以比模式表更窄的通用动作集合对待的协议族：ADB 与 fastboot 端点只保留通用烧录动作，
/// HDC 端点不保留任何动作，其余协议维持完整的表行为。</para>
/// </summary>
internal enum FlashDeviceKind
{
    /// <summary>The protocol has no additional restriction: the mode table alone decides.
    /// <para>该协议没有额外限制：仅由模式表决定。</para></summary>
    Unknown,

    /// <summary>An ADB endpoint; the page offers only the shared flash action, and the view model keeps
    /// it disabled until a noninteractive root shell is confirmed.
    /// <para>ADB 端点；页面只提供通用烧录动作，且视图模型在确认非交互 root shell 之前保持其禁用。</para>
    /// </summary>
    Adb,

    /// <summary>A fastboot endpoint; the page offers only the shared flash action.
    /// <para>fastboot 端点；页面只提供通用烧录动作。</para></summary>
    Fastboot,

    /// <summary>An HDC endpoint; the page has no HDC flash path, so it offers no control at all.
    /// <para>HDC 端点；页面没有 HDC 烧录路径，因此不提供任何控件。</para></summary>
    Hdc,

    /// <summary>An Allwinner endpoint; only the Sunxi section is offered because no image plan drives the shared
    /// flash actions yet. <para>全志端点；由于尚无镜像计划驱动通用烧录动作，只提供全志分类。</para></summary>
    Sunxi
}

/// <summary>
/// The stage a Rockchip endpoint is proven to be in. The stage is evidence, not a guess: mask ROM and the
/// usbplug loader share one product ID, and the descriptor bit that tells them apart is often unreadable
/// through the Windows rockusb driver, so an endpoint whose stage was never proven stays
/// <see cref="Unknown"/> and keeps the commands the device itself can refuse.
/// <para>瑞芯微端点被证实的阶段。阶段是证据而不是猜测：mask ROM 与 usbplug loader 共用同一产品 ID，而区分两者的
/// 描述符位经常无法经 Windows rockusb 驱动读出，因此阶段从未被证实的端点保持 <see cref="Unknown"/>，并保留那些
/// 设备自己能拒绝的命令。</para>
/// </summary>
internal enum RockchipStage
{
    /// <summary>No stage was proven: the descriptor carried no readable <c>bcdUSB</c> value and the reported serial
    /// carried no loader marker. Both storage actions and the loader download stay offered, because only the device's
    /// own answers can settle the stage, and refusing up front is what blocks a loader-stage board with an unreadable
    /// descriptor.
    /// <para>阶段未被证实：描述符没有可读的 <c>bcdUSB</c> 取值，上报序列号也没有 loader 标记。存储动作与 loader
    /// 下载都保持提供，因为只有设备自己的应答能确定阶段，而提前拒绝正是让描述符不可读的 loader 阶段板子无法烧录的原因。</para>
    /// </summary>
    Unknown,

    /// <summary>The descriptor proved mask ROM, which answers information commands only.
    /// <para>描述符证实处于 mask ROM，该阶段只应答信息类命令。</para>
    /// </summary>
    Maskrom,

    /// <summary>The loader (usbplug) is running, so the storage commands the page uses are answerable.
    /// <para>loader（usbplug）正在运行，因此页面使用的存储命令可被应答。</para>
    /// </summary>
    Loader
}

/// <summary>
/// The single source of truth for which flash-page controls are usable and which device mode each of them needs.
/// <para>烧录页面哪些控件可用、以及每个控件需要哪种设备模式的唯一来源。</para>
/// </summary>
/// <remarks>
/// The page applies this table only to device operations in the right action pane. Shared operations require a
/// selected device; protocol-specific sections are additionally limited to their matching device mode, and the kind
/// dimension narrows the shared set to the flash action alone for ADB and fastboot endpoints and to no operation for
/// HDC endpoints.
/// <para>页面只对右侧操作区中的设备操作应用这张表。通用操作需要选中设备；协议专属分类还会进一步限制为匹配的设备模式，
/// 而种类维度把通用集合收窄为仅烧录动作（ADB 与 fastboot 端点）或完全不提供操作（HDC 端点）。</para>
/// </remarks>
internal static class FlashDeviceUiPolicy
{
    /// <summary>Modes of the Qualcomm EDL section controls. <para>高通 EDL 分类控件适用的模式。</para></summary>
    private static readonly FlashDeviceMode[] EdlOnly = [FlashDeviceMode.Edl];

    /// <summary>Modes of the MediaTek section controls. <para>联发科分类控件适用的模式。</para></summary>
    private static readonly FlashDeviceMode[] MtkOnly = [FlashDeviceMode.Mtk];

    /// <summary>Modes of the Rockchip section controls. <para>瑞芯微分类控件适用的模式。</para></summary>
    private static readonly FlashDeviceMode[] RkdlOnly = [FlashDeviceMode.Rkdl];

    /// <summary>Modes of the SpacemiT section controls. <para>进迭时空分类控件适用的模式。</para></summary>
    private static readonly FlashDeviceMode[] SpacemiTOnly = [FlashDeviceMode.Fastboot];

    /// <summary>Modes of the Sunxi section controls. <para>全志分类控件适用的模式。</para></summary>
    private static readonly FlashDeviceMode[] SunxiOnly = [FlashDeviceMode.Sunxi];

    /// <summary>
    /// Modes whose devices can load a boot loader through the page: the EDL programmer upload, the MTK
    /// preloader (DA) package and the Rockchip RKDL loader.
    /// <para>设备可经页面加载引导的模式：EDL programmer 上传、MTK preloader（DA）包与瑞芯微 RKDL loader。</para>
    /// </summary>
    private static readonly FlashDeviceMode[] LoaderOnly =
        [FlashDeviceMode.Edl, FlashDeviceMode.Mtk, FlashDeviceMode.Rkdl];

    /// <summary>
    /// Every device-gated control with the modes it is limited to; <see langword="null"/> marks an action shared by
    /// every selected device. Entries follow the page layout so the table reads like the page; protocol sections keep
    /// their expander headers usable while restricting the inner panes to the matching device mode.
    /// <para>每个受设备门控的控件及其限定的模式；<see langword="null"/> 表示所有已选设备共用的动作。条目顺序与
    /// 页面布局一致，使这张表可以按页面顺序阅读；各协议分类的展开器表头保持可用，仅将内部面板限制为匹配的设备模式。</para>
    /// </summary>
    private static readonly (string Name, FlashDeviceMode[]? Modes)[] Entries =
    [
        ("LoadBootButton", LoaderOnly),
        ("ReadInfoButton", null),
        ("ReadbackSelectedButton", null),
        ("FlashSelectedButton", null),
        ("EraseSelectedButton", null),
        ("SwitchSlotButton", null),
        ("EdlPane", EdlOnly),
        ("MtkPane", MtkOnly),
        ("RkPane", RkdlOnly),
        ("SpacemiTPane", SpacemiTOnly),
        ("SpacemiTChoosePackageButton", SpacemiTOnly),
        ("SpacemiTFlashButton", SpacemiTOnly),
        ("SunxiPane", SunxiOnly),
        ("SunxiRefreshButton", SunxiOnly),
        ("SunxiChooseFes1Button", SunxiOnly),
        ("SunxiChooseUbootButton", SunxiOnly),
        ("SunxiBootFesButton", SunxiOnly),
        ("SunxiReadMemoryButton", SunxiOnly),
        ("SunxiWriteMemoryButton", SunxiOnly),
        ("SunxiExecuteButton", SunxiOnly),
        ("ImagePane", null)
    ];

    /// <summary>Mode lookup of <see cref="Entries"/>. <para><see cref="Entries"/> 的模式查找表。</para></summary>
    private static readonly Dictionary<string, FlashDeviceMode[]?> ModeIndex = BuildModeIndex();

    /// <summary>
    /// The controls of the SpacemiT section, gated on top of the mode table by the selected device being the
    /// SpacemiT fastboot gadget (VID 0x361C / PID 0x1001).
    /// <para>进迭时空分类的控件，在模式表之上再按所选设备为进迭时空 fastboot gadget（VID 0x361C / PID 0x1001）
    /// 门控。</para>
    /// </summary>
    internal static IReadOnlyList<string> SpacemiTControls { get; } =
        ["SpacemiTPane", "SpacemiTChoosePackageButton", "SpacemiTFlashButton"];

    /// <summary>
    /// The controls of the Sunxi section; the page offers them to an Allwinner endpoint only, and offers no shared
    /// action there until an image plan exists.
    /// <para>全志分类的控件；页面只把它们提供给全志端点，并在镜像计划出现之前不在该端点提供通用动作。</para>
    /// </summary>
    internal static IReadOnlyList<string> SunxiControls { get; } =
    [
        "SunxiPane", "SunxiRefreshButton", "SunxiChooseFes1Button", "SunxiChooseUbootButton",
        "SunxiBootFesButton", "SunxiReadMemoryButton", "SunxiWriteMemoryButton", "SunxiExecuteButton"
    ];

    /// <summary>The gated controls in presentation order. <para>按界面顺序排列的受控控件。</para></summary>
    internal static IReadOnlyList<string> GatedControls { get; } = [.. Entries.Select(entry => entry.Name)];

    /// <summary>
    /// Controls turned off while a page operation runs: the shared actions of the side action pane, its flash and
    /// AVB options, and the panes of its protocol sections. Everything else on the page keeps its own state, so the
    /// page stays scrollable and the package bar, the partition table and the log stay usable during a run.
    /// <para>页面操作运行期间关闭的控件：侧边操作区的通用动作、其刷写与 AVB 选项，以及各协议分类的面板。
    /// 页面其余部分保持自身状态，因此运行期间页面仍可滚动，刷机包栏、分区表与日志仍可操作。</para>
    /// </summary>
    internal static IReadOnlyList<string> BusyDisabledControls { get; } =
    [
        "LoadBootButton",
        "ReadInfoButton",
        "ReadbackSelectedButton",
        "FlashSelectedButton",
        "EraseSelectedButton",
        "SwitchSlotButton",
        "DisableVbmetaBox",
        "AddRootBox",
        "EraseDataBox",
        "RebootAfterBox",
        "ShowAllPartitionsBox",
        "DeleteCowBox",
        "AvbOptionsPanel",
        "RootOptionsPanel",
        "EdlPane",
        "MtkPane",
        "RkPane",
        "SpacemiTPane",
        "SunxiPane",
        "ImagePane"
    ];

    /// <summary>The busy-state lookup of <see cref="BusyDisabledControls"/>.
    /// <para><see cref="BusyDisabledControls"/> 的忙碌状态查找表。</para>
    /// </summary>
    private static readonly HashSet<string> BusyDisabledIndex =
        new(BusyDisabledControls, StringComparer.Ordinal);

    /// <summary>
    /// Returns whether the control is turned off while an operation runs.
    /// <para>返回该控件是否在操作运行期间被关闭。</para>
    /// </summary>
    /// <param name="name">Name of the XAML control. <para>XAML 控件名称。</para></param>
    /// <returns><see langword="true"/> for a control owned by the busy state.
    /// <para>属于忙碌状态的控件返回 <see langword="true"/>。</para></returns>
    internal static bool IsBusyDisabled(string name) => BusyDisabledIndex.Contains(name);

    /// <summary>
    /// Controls a Rockchip device offers only once the loader is proven to run: the shared flash actions and the
    /// Rockchip section, whose commands all read the storage that mask ROM cannot answer.
    /// <para>瑞芯微设备只有在 loader 被证实运行后才提供的控件：通用刷写动作与瑞芯微分类，它们的命令都会读取
    /// Mask ROM 无法应答的存储。</para>
    /// </summary>
    /// <remarks>
    /// This is a second availability dimension on top of <see cref="IsAvailable(string, FlashDeviceMode)"/> rather
    /// than a mode of its own: it only restricts the <see cref="FlashDeviceMode.Rkdl"/> mode, and a page state that
    /// already made a control unusable stays unusable. Only a stage proven to be mask ROM is excluded; an unproven
    /// stage keeps these controls, so the device's own answers decide instead of an unreadable descriptor field.
    /// <para>这是叠加在 <see cref="IsAvailable(string, FlashDeviceMode)"/> 之上的第二个可用性维度，而不是独立
    /// 模式：只约束 <see cref="FlashDeviceMode.Rkdl"/> 模式，页面状态本已禁用的控件仍然不可用。只有被证实为
    /// mask ROM 的阶段被排除；未被证实的阶段保留这些控件，使设备自己的应答而不是不可读的描述符字段作出判断。</para>
    /// </remarks>
    internal static IReadOnlyList<string> RockchipLoaderControls { get; } =
    [
        "ReadInfoButton",
        "ReadbackSelectedButton",
        "FlashSelectedButton",
        "EraseSelectedButton",
        "RkPane"
    ];

    /// <summary>
    /// Controls no Rockchip stage can serve: A/B slot switching is a fastboot operation, so neither a bootrom nor a
    /// loader endpoint can answer it.
    /// <para>任何瑞芯微阶段都无法服务的控件：A/B 槽位切换是 fastboot 操作，bootrom 与 loader 端点都无法应答。</para>
    /// </summary>
    internal static IReadOnlyList<string> RockchipUnsupportedControls { get; } = ["SwitchSlotButton"];

    /// <summary>
    /// Maps the selected device's protocol to the mode whose controls the page offers.
    /// <para>把所选设备的协议映射为页面据此提供控件的模式。</para>
    /// </summary>
    /// <param name="protocol">Protocol of the selected device; <see langword="null"/> means nothing is selected.
    /// <para>所选设备的协议；<see langword="null"/> 表示未选中设备。</para></param>
    /// <returns>The mode; protocols without a dedicated section map to <see cref="FlashDeviceMode.Shared"/>.
    /// <para>对应模式；没有专属分类的协议映射为 <see cref="FlashDeviceMode.Shared"/>。</para></returns>
    internal static FlashDeviceMode ModeOf(DeviceProtocolKind? protocol) => protocol switch
    {
        null => FlashDeviceMode.None,
        DeviceProtocolKind.Edl => FlashDeviceMode.Edl,
        DeviceProtocolKind.Mtk => FlashDeviceMode.Mtk,
        DeviceProtocolKind.Rkdl => FlashDeviceMode.Rkdl,
        DeviceProtocolKind.Fastboot => FlashDeviceMode.Fastboot,
        DeviceProtocolKind.Sunxi => FlashDeviceMode.Sunxi,
        _ => FlashDeviceMode.Shared
    };

    /// <summary>
    /// Maps the selected device's protocol to the restricted-kind dimension: ADB and fastboot narrow the shared set
    /// to the flash action, HDC narrows it to nothing, and every other protocol stays unrestricted.
    /// <para>把所选设备协议映射到受限种类维度：ADB 与 fastboot 把通用集合收窄为烧录动作，HDC 收窄为空，
    /// 其余协议不受限。</para>
    /// </summary>
    /// <param name="protocol">Protocol of the selected device; <see langword="null"/> means nothing is selected.
    /// <para>所选设备的协议；<see langword="null"/> 表示未选中设备。</para>
    /// </param>
    /// <returns>The restricted kind of the protocol. <para>该协议对应的受限种类。</para></returns>
    internal static FlashDeviceKind KindOf(DeviceProtocolKind? protocol) => protocol switch
    {
        DeviceProtocolKind.Adb => FlashDeviceKind.Adb,
        DeviceProtocolKind.Fastboot => FlashDeviceKind.Fastboot,
        DeviceProtocolKind.Hdc => FlashDeviceKind.Hdc,
        DeviceProtocolKind.Sunxi => FlashDeviceKind.Sunxi,
        _ => FlashDeviceKind.Unknown,
    };

    /// <summary>
    /// The controls an ADB endpoint offers: only the shared flash action; the view model additionally keeps
    /// it disabled until its root-shell probe confirms a noninteractive su.
    /// <para>ADB 端点可用的控件：仅通用烧录动作；视图模型在其 root shell 探测确认非交互 su 之前额外保持禁用。</para>
    /// </summary>
    internal static IReadOnlyList<string> AdbPermittedControls { get; } = ["FlashSelectedButton"];

    /// <summary>
    /// The controls a fastboot endpoint offers: only the shared flash action.
    /// <para>fastboot 端点可用的控件：仅通用烧录动作。</para>
    /// </summary>
    internal static IReadOnlyList<string> FastbootPermittedControls { get; } = ["FlashSelectedButton"];

    /// <summary>
    /// Reports whether the named control additionally waits for the view model's ADB root-shell verdict
    /// before it may become enabled on an ADB device.
    /// <para>判断该控件在 ADB 设备上是否还需等待视图模型的 root shell 探测结论才能启用。</para>
    /// </summary>
    /// <param name="controlName">Name of the XAML control. <para>XAML 控件名称。</para></param>
    /// <returns><see langword="true"/> when the control needs the confirmed root shell.
    /// <para>控件需要已确认的 root shell 时为 <see langword="true"/>。</para></returns>
    internal static bool RequiresAdbSu(string controlName)
        => string.Equals(controlName, "FlashSelectedButton", StringComparison.Ordinal);

    /// <summary>
    /// Reports whether a control may be used in the given mode; controls outside the table are always allowed.
    /// <para>判断控件在给定模式下是否可用；表中没有的控件始终允许。</para>
    /// </summary>
    /// <param name="controlName">Name of the XAML control. <para>XAML 控件名称。</para></param>
    /// <param name="mode">The mode currently offered by the page. <para>页面当前所处的模式。</para></param>
    /// <returns><see langword="true"/> when the control is not gated or its mode matches.
    /// <para>控件未受门控或模式匹配时返回 <see langword="true"/>。</para></returns>
    internal static bool IsAvailable(string controlName, FlashDeviceMode mode)
    {
        ArgumentNullException.ThrowIfNull(controlName);
        if (!ModeIndex.TryGetValue(controlName, out var modes))
        {
            return true;
        }

        return modes is null ? mode != FlashDeviceMode.None : Array.IndexOf(modes, mode) >= 0;
    }

    /// <summary>
    /// Reports whether a control may be used in the given mode and Rockchip stage: a stage proven to be mask ROM gets
    /// only the shared info actions, while a proven loader stage and an unproven stage additionally get the storage
    /// actions and the Rockchip section; fastboot slot switching stays unusable on every Rockchip stage. The SpacemiT
    /// section is offered only to a SpacemiT fastboot endpoint regardless of mode and stage.
    /// <para>判断控件在给定模式与瑞芯微阶段下是否可用：被证实为 mask ROM 的阶段只有通用信息动作，被证实的 loader
    /// 阶段与未被证实的阶段还有存储动作与瑞芯微分类；fastboot 槽位切换在任何瑞芯微阶段都不可用。进迭时空分类
    /// 只对进迭时空 fastboot 端点提供，与模式和阶段无关。</para>
    /// </summary>
    /// <param name="controlName">Name of the XAML control. <para>XAML 控件名称。</para></param>
    /// <param name="mode">The mode currently offered by the page. <para>页面当前所处的模式。</para></param>
    /// <param name="stage">The stage proven for the selected Rockchip endpoint; a device of another protocol passes
    /// <see cref="RockchipStage.Unknown"/>, which no other mode consults.
    /// <para>所选瑞芯微端点被证实的阶段；其他协议的设备传入 <see cref="RockchipStage.Unknown"/>，其他模式都不读取它。</para></param>
    /// <param name="kind">The restricted protocol kind of the selected device; ADB and fastboot narrow the shared
    /// set to the flash action and HDC to no operation.
    /// <para>所选设备的受限协议种类；ADB 与 fastboot 把通用集合收窄为烧录动作，HDC 收窄为空。</para></param>
    /// <param name="isSpacemiTFastboot">Whether the selected fastboot endpoint is the SpacemiT gadget; only a
    /// <see langword="true"/> verdict offers <see cref="SpacemiTControls"/>.
    /// <para>所选 fastboot 端点是否为进迭时空 gadget；只有 <see langword="true"/> 才提供
    /// <see cref="SpacemiTControls"/>。</para></param>
    /// <returns><see langword="true"/> when the mode, the device's stage and the kind all offer the control.
    /// <para>模式、设备阶段与种类都提供该控件时为 <see langword="true"/>。</para></returns>
    internal static bool IsAvailable(string controlName, FlashDeviceMode mode, RockchipStage stage,
        FlashDeviceKind kind = FlashDeviceKind.Unknown, bool isSpacemiTFastboot = false)
    {
        if (SpacemiTControls.Contains(controlName, StringComparer.Ordinal))
        {
            return kind == FlashDeviceKind.Fastboot && isSpacemiTFastboot;
        }

        if (!IsAvailable(controlName, mode))
        {
            return false;
        }

        if (kind == FlashDeviceKind.Adb)
        {
            return AdbPermittedControls.Contains(controlName, StringComparer.Ordinal);
        }

        if (kind == FlashDeviceKind.Fastboot)
        {
            return FastbootPermittedControls.Contains(controlName, StringComparer.Ordinal);
        }

        if (kind == FlashDeviceKind.Hdc)
        {
            return false;
        }

        if (kind == FlashDeviceKind.Sunxi)
        {
            return SunxiControls.Contains(controlName, StringComparer.Ordinal);
        }

        if (mode != FlashDeviceMode.Rkdl)
        {
            return true;
        }

        if (RockchipUnsupportedControls.Contains(controlName, StringComparer.Ordinal))
        {
            return false;
        }

        return stage != RockchipStage.Maskrom ||
            !RockchipLoaderControls.Contains(controlName, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reports whether a device in the observed protocol stage can still accept a boot loader: an EDL endpoint
    /// accepts a programmer only before the loader transition completed (unknown or Sahara), an MTK link that
    /// only logs accepts nothing, and a Rockchip endpoint accepts a loader until the loader stage is proven,
    /// because the boot download rewrites the reported serial to the vendor's <c>ROCKCHIP</c> value and only that
    /// stage answers the storage commands the page would then use instead.
    /// <para>判断设备所处的协议阶段是否仍可接受引导：EDL 端点只在加载器切换完成前（未知或 Sahara）接受
    /// programmer，仅日志的 MTK 链路不接受任何加载，瑞芯微端点在 loader 阶段被证实之前接受 loader，因为引导
    /// 下载会把上报序列号改写为厂商 <c>ROCKCHIP</c> 值，而只有该阶段此后应答页面改用的存储命令。</para>
    /// </summary>
    /// <param name="protocol">Protocol of the selected device. <para>所选设备的协议。</para></param>
    /// <param name="edlStage">The EDL wire protocol discovery observed. <para>发现层观测到的 EDL 线协议。</para></param>
    /// <param name="mtkStage">The MTK boot stage token; "logging" marks a link that cannot be upgraded.
    /// <para>联发科启动阶段标记；"logging" 表示无法升级的链路。</para></param>
    /// <param name="stage">The stage proven for the selected Rockchip endpoint; an unproven stage keeps the loader
    /// download available so a boot ROM whose descriptor is unreadable can still be moved into loader mode.
    /// <para>所选瑞芯微端点被证实的阶段；未被证实的阶段保留 loader 下载，使描述符不可读的 Boot ROM 仍能被带入
    /// loader 模式。</para></param>
    /// <returns><see langword="true"/> when the stage can accept a loader.
    /// <para>该阶段可以接受 loader 时为 <see langword="true"/>。</para></returns>
    internal static bool IsLoaderStageLoadable(DeviceProtocolKind protocol, EdlProtocolState edlStage,
        string mtkStage, RockchipStage stage = RockchipStage.Unknown)
        => protocol switch
        {
            DeviceProtocolKind.Edl => edlStage is EdlProtocolState.Unknown or EdlProtocolState.Sahara,
            DeviceProtocolKind.Mtk => !string.Equals(mtkStage, MtkLoggingStage, StringComparison.Ordinal),
            DeviceProtocolKind.Rkdl => stage != RockchipStage.Loader,
            _ => false,
        };

    private const string MtkLoggingStage = "logging";

    private static Dictionary<string, FlashDeviceMode[]?> BuildModeIndex()
    {
        var index = new Dictionary<string, FlashDeviceMode[]?>(Entries.Length, StringComparer.Ordinal);
        foreach (var (name, modes) in Entries)
        {
            index.Add(name, modes);
        }

        return index;
    }
}