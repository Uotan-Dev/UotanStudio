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

    /// <summary>A selected device that only gets the page's shared actions; fastboot, ADB, HDC and Apple endpoints
    /// all fall in this mode.
    /// <para>只获得页面通用动作的已选设备；fastboot、ADB、HDC 与 Apple 端点都属该模式。</para>
    /// </summary>
    Shared,

    /// <summary>Qualcomm EDL (9008). <para>高通 EDL（9008）。</para></summary>
    Edl,

    /// <summary>MediaTek BootROM or download-agent mode. <para>联发科 BootROM 或下载代理模式。</para></summary>
    Mtk,

    /// <summary>Rockchip bootrom or loader (rockusb). <para>瑞芯微 bootrom/loader（rockusb）。</para></summary>
    Rkdl
}

/// <summary>
/// The single source of truth for which flash-page controls are usable and which device mode each of them needs.
/// <para>烧录页面哪些控件可用、以及每个控件需要哪种设备模式的唯一来源。</para>
/// </summary>
/// <remarks>
/// The page applies this table only to device operations in the right action pane. Shared operations require a
/// selected device; protocol-specific sections are additionally limited to their matching device mode.
/// <para>页面只对右侧操作区中的设备操作应用这张表。通用操作需要选中设备；协议专属分类还会进一步限制为匹配的设备模式。</para>
/// </remarks>
internal static class FlashDeviceUiPolicy
{
    /// <summary>Modes of the Qualcomm EDL section controls. <para>高通 EDL 分类控件适用的模式。</para></summary>
    private static readonly FlashDeviceMode[] EdlOnly = [FlashDeviceMode.Edl];

    /// <summary>Modes of the MediaTek section controls. <para>联发科分类控件适用的模式。</para></summary>
    private static readonly FlashDeviceMode[] MtkOnly = [FlashDeviceMode.Mtk];

    /// <summary>Modes of the Rockchip section controls. <para>瑞芯微分类控件适用的模式。</para></summary>
    private static readonly FlashDeviceMode[] RkdlOnly = [FlashDeviceMode.Rkdl];

    /// <summary>
    /// Modes whose devices can load a boot loader through the page: the EDL programmer upload, the MTK
    /// preloader (DA) package and the Rockchip RKDL loader.
    /// <para>设备可经页面加载引导的模式：EDL programmer 上传、MTK preloader（DA）包与瑞芯微 RKDL loader。</para>
    /// </summary>
    private static readonly FlashDeviceMode[] LoaderOnly =
        [FlashDeviceMode.Edl, FlashDeviceMode.Mtk, FlashDeviceMode.Rkdl];

    /// <summary>
    /// Every device-gated control with the modes it is limited to; <see langword="null"/> marks an action shared by
    /// every selected device. Entries follow the page layout so the table reads like the page.
    /// <para>每个受设备门控的控件及其限定的模式；<see langword="null"/> 表示所有已选设备共用的动作。条目顺序与
    /// 页面布局一致，使这张表可以按页面顺序阅读。</para>
    /// </summary>
    private static readonly (string Name, FlashDeviceMode[]? Modes)[] Entries =
    [
        // 通用操作与刷写选项 / shared actions and flash options
        ("LoadBootButton", LoaderOnly),
        ("ReadInfoButton", null),
        ("ReadbackSelectedButton", null),
        ("FlashSelectedButton", null),
        ("EraseSelectedButton", null),
        ("CancelFlashButton", null),
        ("SwitchSlotButton", null),
        ("RebootMenuButton", null),
        ("DisableVbmetaBox", null),
        ("AddRootBox", null),
        ("EraseDataBox", null),
        ("RebootAfterBox", null),
        ("ShowAllPartitionsBox", null),
        ("DeleteCowBox", null),
        ("RootOptionsPanel", null),
        // 协议分类 / protocol sections
        ("EdlPane", EdlOnly),
        ("MtkPane", MtkOnly),
        ("RkPane", RkdlOnly),
        ("RkSendBootButton", RkdlOnly),
        // 镜像操作分类 / image operations section
        ("ImagePane", null)
    ];

    /// <summary>Mode lookup of <see cref="Entries"/>. <para><see cref="Entries"/> 的模式查找表。</para></summary>
    private static readonly Dictionary<string, FlashDeviceMode[]?> ModeIndex = BuildModeIndex();

    /// <summary>The gated controls in presentation order. <para>按界面顺序排列的受控控件。</para></summary>
    internal static IReadOnlyList<string> GatedControls { get; } = [.. Entries.Select(entry => entry.Name)];

    /// <summary>
    /// Controls a Rockchip device offers only after the loader took over: the shared flash actions and the Rockchip
    /// section, whose commands all read the storage that mask ROM cannot answer. The loader-stage device is the one
    /// whose reported serial the stage switch rewrote to the vendor's <c>ROCKCHIP</c> value.
    /// <para>瑞芯微设备只有在 loader 接管后才提供的控件：通用刷写动作与瑞芯微分类，它们的命令都会读取
    /// Mask ROM 无法应答的存储。loader 阶段设备指阶段切换已把上报序列号改写为厂商 <c>ROCKCHIP</c> 值的设备。</para>
    /// </summary>
    /// <remarks>
    /// This is a second availability dimension on top of <see cref="IsAvailable(string, FlashDeviceMode)"/> rather
    /// than a mode of its own: it only restricts the <see cref="FlashDeviceMode.Rkdl"/> mode, and a page state that
    /// already made a control unusable stays unusable.
    /// <para>这是叠加在 <see cref="IsAvailable(string, FlashDeviceMode)"/> 之上的第二个可用性维度，而不是独立
    /// 模式：只约束 <see cref="FlashDeviceMode.Rkdl"/> 模式，页面状态本已禁用的控件仍然不可用。</para>
    /// </remarks>
    internal static IReadOnlyList<string> RockchipLoaderControls { get; } =
    [
        "ReadInfoButton",
        "ReadbackSelectedButton",
        "FlashSelectedButton",
        "EraseSelectedButton",
        "AddRootBox",
        "RootOptionsPanel",
        "RkPane"
    ];

    /// <summary>
    /// Controls a Rockchip device offers only while it is still in mask ROM: the send-boot action, whose vendor
    /// request a loader-stage device would refuse.
    /// <para>瑞芯微设备仅在仍处于 Mask ROM 时才提供的控件：发送引导动作；loader 阶段设备会拒绝其 vendor 请求。</para>
    /// </summary>
    internal static IReadOnlyList<string> RockchipMaskromControls { get; } = ["RkSendBootButton"];

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
        _ => FlashDeviceMode.Shared
    };

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
    /// Reports whether a control may be used in the given mode and Rockchip stage: a maskrom-stage device gets only
    /// the send-boot action, and a loader-stage device gets the storage actions and the Rockchip section.
    /// <para>判断控件在给定模式与瑞芯微阶段下是否可用：maskrom 阶段设备只有发送引导动作，loader 阶段设备才有
    /// 存储动作与瑞芯微分类。</para>
    /// </summary>
    /// <param name="controlName">Name of the XAML control. <para>XAML 控件名称。</para></param>
    /// <param name="mode">The mode currently offered by the page. <para>页面当前所处的模式。</para></param>
    /// <param name="isRockchipLoaderStage">Whether the selected Rockchip device reports the loader stage
    /// (serial rewritten to <c>ROCKCHIP</c>). <para>所选瑞芯微设备是否处于 loader 阶段（序列号已改写为
    /// <c>ROCKCHIP</c>）。</para></param>
    /// <returns><see langword="true"/> when the mode offers the control and the device's stage offers it too.
    /// <para>模式提供该控件且设备阶段也提供它时为 <see langword="true"/>。</para></returns>
    internal static bool IsAvailable(string controlName, FlashDeviceMode mode, bool isRockchipLoaderStage)
    {
        if (!IsAvailable(controlName, mode))
        {
            return false;
        }

        if (mode != FlashDeviceMode.Rkdl)
        {
            return true;
        }

        return isRockchipLoaderStage
            ? !RockchipMaskromControls.Contains(controlName, StringComparer.Ordinal)
            : !RockchipLoaderControls.Contains(controlName, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reports whether a device in the observed protocol stage can still accept a boot loader: an EDL endpoint
    /// accepts a programmer only before the loader transition completed (unknown or Sahara), an MTK link that
    /// only logs accepts nothing, and a Rockchip endpoint always can because loading a loader is the way out
    /// of mask ROM.
    /// <para>判断设备所处的协议阶段是否仍可接受引导：EDL 端点只在加载器切换完成前（未知或 Sahara）接受
    /// programmer，仅日志的 MTK 链路不接受任何加载，瑞芯微端点始终可以，因为加载 loader 正是脱离
    /// Mask ROM 的途径。</para>
    /// </summary>
    /// <param name="protocol">Protocol of the selected device. <para>所选设备的协议。</para></param>
    /// <param name="edlStage">The EDL wire protocol discovery observed. <para>发现层观测到的 EDL 线协议。</para></param>
    /// <param name="mtkStage">The MTK boot stage token; "logging" marks a link that cannot be upgraded.
    /// <para>联发科启动阶段标记；"logging" 表示无法升级的链路。</para></param>
    /// <returns><see langword="true"/> when the stage can accept a loader.
    /// <para>该阶段可以接受 loader 时为 <see langword="true"/>。</para></returns>
    internal static bool IsLoaderStageLoadable(DeviceProtocolKind protocol, EdlProtocolState edlStage,
        string mtkStage)
        => protocol switch
        {
            DeviceProtocolKind.Edl => edlStage is EdlProtocolState.Unknown or EdlProtocolState.Sahara,
            DeviceProtocolKind.Mtk => !string.Equals(mtkStage, MtkLoggingStage, StringComparison.Ordinal),
            DeviceProtocolKind.Rkdl => true,
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