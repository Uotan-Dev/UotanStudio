using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Device transport kind.
/// <para>设备传输类型。</para>
/// </summary>
public enum DeviceTransportKind
{
    /// <summary>Transport not recognized here.<para>未被识别出的传输类型。</para></summary>
    Unknown = -1,
    /// <summary>Direct USB connection.<para>USB 直连。</para></summary>
    Usb,
    /// <summary>Network transport through an adb server or TCP.<para>经 adb server 或 TCP 的网络传输。</para></summary>
    Tcp,
    /// <summary>Serial port connection.<para>串口连接。</para></summary>
    Serial,
}

/// <summary>
/// Device protocol kind.
/// <para>设备协议类型。</para>
/// </summary>
public enum DeviceProtocolKind
{
    /// <summary>ADB mode (system, recovery or sideload).<para>ADB 模式（system、recovery、sideload）。</para></summary>
    Adb,
    /// <summary>Fastboot mode (bootloader or fastbootd).<para>Fastboot 模式（bootloader 或 fastbootd）。</para></summary>
    Fastboot,
    /// <summary>Qualcomm EDL (9008).<para>高通 EDL（9008）。</para></summary>
    Edl,
    /// <summary>Rockchip bootrom or loader (rockusb).<para>瑞芯微 bootrom/loader（rockusb）。</para></summary>
    Rkdl,
    /// <summary>MediaTek BootROM or download-agent mode.<para>联发科 BootROM/DA（mtk）模式。</para></summary>
    Mtk,
    /// <summary>OpenHarmony HDC, over direct USB or the local hdc server.<para>OpenHarmony HDC，USB 直连或经本机 hdc server。</para></summary>
    Hdc,
    /// <summary>Apple iPhone/iPad over usbmuxd or the direct USB multiplexer.<para>经 usbmuxd 或直连 USB 复用接口的 Apple iPhone/iPad。</para></summary>
    Ios,
}

/// <summary>
/// A single device enumerated by the device monitor service.
/// <para>由设备监视服务枚举出的一台设备。</para>
/// </summary>
public sealed class DetectedDevice
{
    /// <summary>Stable key: the USB device key or <c>tcp:host:port</c>.<para>稳定键：USB 设备键或 <c>tcp:host:port</c>。</para></summary>
    /// <value>The key that re-opens this endpoint.<para>用于重新打开该端点的键。</para></value>
    public required string Key { get; init; }
    /// <summary>Serial number or display identity.<para>序列号或显示标识。</para></summary>
    /// <value>The serial text of this endpoint.<para>该端点的序列号文本。</para></value>
    public required string Serial { get; init; }
    /// <summary>Protocol kind.<para>协议类型。</para></summary>
    /// <value>The protocol this endpoint speaks.<para>该端点使用的协议。</para></value>
    public required DeviceProtocolKind Protocol { get; init; }
    /// <summary>Transport kind.<para>传输类型。</para></summary>
    /// <value>The transport this endpoint is reached over.<para>访问该端点所使用的传输。</para></value>
    public required DeviceTransportKind Transport { get; init; }
    /// <summary>Verified physical identity shared by protocol endpoints, or <c>null</c> when unavailable.<para>已核实的物理身份，由多个协议端点共享；不可得时为 <c>null</c>。</para></summary>
    /// <value>The shared physical device identity, or <c>null</c>.<para>共享的物理设备身份，或为 <c>null</c>。</para></value>
    public string? PhysicalId { get; init; }
    /// <summary>
    /// Network endpoint the server resolved for this endpoint, written on every transport that shares
    /// it so several routes to one device merge into one group; never inferred from address shape.
    /// <para>server 为该端点解析出的网络端点，共享同一别名的多条传输并入同一分组；绝不按地址形状推断。</para>
    /// </summary>
    /// <value>
    /// The resolved network alias, or <c>null</c>.
    /// <para>解析出的网络别名，或为 <c>null</c>。</para>
    /// </value>
    public string? NetworkAlias { get; init; }
    /// <summary>
    /// Stable grouping key for the device this row belongs to: the alias first, then the USB physical
    /// identity, then the endpoint's own key.
    /// <para>本行所属设备的稳定分组键：优先别名，其次 USB 物理身份，最后回退到端点自身键。</para>
    /// </summary>
    /// <value>
    /// The alias, physical identity or own key used to group endpoints.
    /// <para>用于归并端点的别名、物理身份或自身键。</para>
    /// </value>
    public string GroupId => NetworkAlias ?? PhysicalId ?? Key;
    /// <summary>State text (device, offline, fastboot, edl, maskrom, loader and so on).<para>状态文本（device、offline、fastboot、edl、maskrom、loader 等）。</para></summary>
    /// <value>The state string obtained from enumeration.<para>枚举得到的状态字符串。</para></value>
    public string State { get; set; } = string.Empty;
    /// <summary>The observed EDL protocol, independent of USB presence.<para>观察到的 EDL 协议，与 USB 在位状态无关。</para></summary>
    /// <value>The EDL protocol state observed for this device.<para>为该设备观察到的 EDL 协议状态。</para></value>
    public EdlProtocolState EdlProtocol { get; set; }
    /// <summary>USB vendor ID, zero for non-USB endpoints.<para>USB 厂商 ID，非 USB 端点为 0。</para></summary>
    /// <value>The USB vendor identifier.<para>USB 厂商标识。</para></value>
    public ushort VendorId { get; init; }
    /// <summary>USB product ID, zero for non-USB endpoints.<para>USB 产品 ID，非 USB 端点为 0。</para></summary>
    /// <value>The USB product identifier.<para>USB 产品标识。</para></value>
    public ushort ProductId { get; init; }
    /// <summary>Display name of this endpoint.<para>该端点的显示名称。</para></summary>
    /// <value>The display text of this endpoint.<para>该端点的显示文本。</para></value>
    public string DisplayName => Transport == DeviceTransportKind.Tcp
        ? $"{Serial} (network)"
        : $"{Serial} ({State})";

    /// <summary>Connection choice for one physical device that exposes several protocols.<para>一台物理设备暴露多种协议时用于区分连接的标签。</para></summary>
    /// <value>The protocol, transport and serial label.<para>协议、传输与序列号组成的标签。</para></value>
    public string ConnectionLabel => $"{ProtocolLabel} · {Transport} · {Serial}";

    /// <summary>Whether discovery identified a debug bridge server endpoint.<para>发现过程是否识别出调试桥 server 端点。</para></summary>
    /// <value><see langword="true"/> when the endpoint was found through a server.<para>端点经 server 发现时为 <see langword="true"/>。</para></value>
    public bool UsesServer => Protocol switch
    {
        DeviceProtocolKind.Adb => Key.StartsWith("adb:", StringComparison.Ordinal),
        DeviceProtocolKind.Hdc => Key.StartsWith("hdc:", StringComparison.Ordinal),
        _ => false
    };

    /// <summary>Protocol and route label, independent of the device's physical transport.<para>协议与路由标签，与设备的物理传输方式无关。</para></summary>
    /// <value>The protocol and route label text.<para>协议与路由标签文本。</para></value>
    public string ProtocolLabel => Protocol switch
    {
        DeviceProtocolKind.Adb => UsesServer ? "ADB server" : "ADB",
        DeviceProtocolKind.Hdc => UsesServer ? "HDC server" : "HDC",
        DeviceProtocolKind.Fastboot => "Fastboot",
        DeviceProtocolKind.Edl => "EDL",
        DeviceProtocolKind.Rkdl => "RKDL",
        DeviceProtocolKind.Mtk => "MTK",
        DeviceProtocolKind.Ios => "iOS",
        _ => throw new ArgumentOutOfRangeException(nameof(Protocol), Protocol, "Unexpected protocol kind.")
    };
}

/// <summary>
/// Device monitor service: performs a full USB scan on startup, then reacts to USB hotplug
/// events and adb-server device changes, plus a manual force-refresh.
/// <para>设备监视服务：启动时全量扫描 USB 设备，随后响应 USB 热插拔与 adb server 设备变更，并提供手动强制刷新。</para>
/// </summary>
public sealed partial class DeviceMonitorService : IDisposable
{
    /// <summary>
    /// Creates an independent monitor instance owned by the requesting code; the type exposes no
    /// global service-locator singleton.
    /// <para>创建一个由请求代码独立持有的监视实例；该类型不暴露全局服务定位单例。</para>
    /// </summary>
    /// <returns>A new monitor instance.<para>新建的监视实例。</para></returns>
    public static DeviceMonitorService Create() => new();

    private readonly DeviceController _controller;
    private static HdcDeviceService HdcOperations => HdcDeviceService.FromSettings();

}