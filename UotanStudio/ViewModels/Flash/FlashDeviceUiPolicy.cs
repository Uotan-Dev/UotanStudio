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
/// The page applies this table as a restriction: a control is usable only when the page state enables it and
/// <see cref="IsAvailable"/> allows the current <see cref="FlashDeviceMode"/>. A control listed without a mode is
/// shared by every selected device and stays unusable while no device is selected; a control listed with modes is
/// additionally limited to those modes. Every control that starts an action is listed, so the page is inert until a
/// device is selected; the protocol section headers stay expandable so the options each mode offers remain
/// inspectable, and the log stays readable.
/// <para>页面把这张表作为限制条件应用：只有页面状态启用该控件、且 <see cref="IsAvailable"/> 允许当前
/// <see cref="FlashDeviceMode"/> 时控件才可用。未标注模式的控件由所有已选设备共用，未选中设备时不可用；标注了
/// 模式的控件进一步限定在这些模式内。所有能发起动作的控件都列在表中，因此未选中设备时整页不可用；协议分类标题
/// 仍可展开，以便查看各模式提供的选项，日志保持可读。</para>
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
    /// Every device-gated control with the modes it is limited to; <see langword="null"/> marks an action shared by
    /// every selected device. Entries follow the page layout so the table reads like the page.
    /// <para>每个受设备门控的控件及其限定的模式；<see langword="null"/> 表示所有已选设备共用的动作。条目顺序与
    /// 页面布局一致，使这张表可以按页面顺序阅读。</para>
    /// </summary>
    private static readonly (string Name, FlashDeviceMode[]? Modes)[] Entries =
    [
        // 刷机包栏 / package bar
        ("PackagePathBox", null),
        ("ChooseFileButton", null),
        ("ChooseFolderButton", null),
        ("GetPackageButton", null),
        // 日志 / log
        ("ClearLogButton", null),
        // 分区列表 / partition list
        ("SelectAllBox", null),
        ("PartitionSearchBox", null),
        ("PartitionGrid", null),
        // 通用操作与刷写选项 / shared actions and flash options
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
        // 镜像操作分类 / image operations section
        ("ImagePane", null)
    ];

    /// <summary>Mode lookup of <see cref="Entries"/>. <para><see cref="Entries"/> 的模式查找表。</para></summary>
    private static readonly Dictionary<string, FlashDeviceMode[]?> ModeIndex = BuildModeIndex();

    /// <summary>The gated controls in presentation order. <para>按界面顺序排列的受控控件。</para></summary>
    internal static IReadOnlyList<string> GatedControls { get; } = [.. Entries.Select(entry => entry.Name)];

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