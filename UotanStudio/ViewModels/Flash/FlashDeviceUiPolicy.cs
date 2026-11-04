using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// The device modes the flash page distinguishes when it decides which controls a selected endpoint offers.
/// <para>烧录页面在决定所选端点提供哪些控件时区分的设备模式。</para>
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
    /// <para>已选 fastboot 端点，在通用烧录动作之外还拥有进迭时空分类。</para>
    /// </summary>
    Fastboot,

    /// <summary>Allwinner FEL or FES. <para>全志 FEL 或 FES。</para></summary>
    Sunxi
}

/// <summary>
/// The protocol families the page treats with a narrower shared-action set than the mode table alone:
/// ADB and fastboot endpoints answer only the shared flash action, HDC endpoints answer no action at all,
/// while every other protocol keeps the full behavior.
/// <para>页面以比模式表更窄的通用动作集合对待的协议族：ADB 与 fastboot 端点只保留通用烧录动作，
/// HDC 端点不保留任何动作，其余协议维持完整行为。</para>
/// </summary>
internal enum FlashDeviceKind
{
    /// <summary>The protocol has no additional restriction: the mode and each control's own condition decide.
    /// <para>该协议没有额外限制：由模式与每个控件自身的条件决定。</para>
    /// </summary>
    Unknown,

    /// <summary>An ADB endpoint; the page offers only the shared flash action, and the view model keeps
    /// it disabled until a noninteractive root shell is confirmed.
    /// <para>ADB 端点；页面只提供通用烧录动作，且视图模型在确认非交互 root shell 之前保持其禁用。</para>
    /// </summary>
    Adb,

    /// <summary>A fastboot endpoint; the page offers only the shared flash action.
    /// <para>fastboot 端点；页面只提供通用烧录动作。</para>
    /// </summary>
    Fastboot,

    /// <summary>An HDC endpoint; the page has no HDC flash path, so it offers no control at all.
    /// <para>HDC 端点；页面没有 HDC 烧录路径，因此不提供任何控件。</para>
    /// </summary>
    Hdc,

    /// <summary>An Allwinner endpoint; only the Sunxi section is offered because no image plan drives the shared
    /// flash actions yet. <para>全志端点；由于尚无镜像计划驱动通用烧录动作，只提供全志分类。</para>
    /// </summary>
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
/// Protocol mapping primitives the page needs before it asks each control its own condition: the mode and restricted
/// kind a selected endpoint belongs to, plus the stage verdict for the loader download.
/// <para>页面在询问每个控件自身条件之前所需的协议映射基础：所选端点所属的模式与受限种类，以及引导下载的阶段结论。</para>
/// </summary>
/// <remarks>
/// Which controls each endpoint offers is no longer a page-wide table: every control carries a
/// <see cref="FlashControlGate"/> that holds its own condition. This type keeps only the shared mapping rules those
/// conditions read.
/// <para>每个端点提供哪些控件不再由页面级表决定：每个控件自带持有自身条件的 <see cref="FlashControlGate"/>。本类型只保留
/// 这些条件读取的公共映射规则。</para>
/// </remarks>
internal static class FlashDeviceUiPolicy
{
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
}
