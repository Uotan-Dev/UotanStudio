using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>
/// <para>设备传输类型。</para>
/// Device transport kind.
/// </summary>
public enum DeviceTransportKind
{
    /// <summary>Transport not recognized by this client.</summary>
    Unknown = -1,
    /// <summary>USB 直连。 Direct USB.</summary>
    Usb,
    /// <summary>网络（adb server / TCP）。 Network (adb server / TCP).</summary>
    Tcp,
    /// <summary>串口。 Serial port.</summary>
    Serial,
}

/// <summary>
/// <para>设备协议类型。</para>
/// Device protocol kind.
/// </summary>
public enum DeviceProtocolKind
{
    /// <summary>ADB（system/recovery/sideload）。 ADB mode.</summary>
    Adb,
    /// <summary>Fastboot（bootloader/fastbootd）。 Fastboot mode.</summary>
    Fastboot,
    /// <summary>高通 EDL（9008）。 Qualcomm EDL.</summary>
    Edl,
    /// <summary>瑞芯微 bootrom/loader（rockusb）。 Rockchip bootrom/loader.</summary>
    Rkdl,
    /// <summary>OpenHarmony HDC（USB 直连或经本机 hdc server）。 OpenHarmony HDC (USB direct or via the local hdc server).</summary>
    Hdc,
}

/// <summary>
/// <para>由设备监视服务枚举出的一台设备。</para>
/// A single device enumerated by the device monitor service.
/// </summary>
public sealed class DetectedDevice
{
    /// <summary>稳定键（USB DeviceKey 或 "tcp:host:port"）。 Stable key.</summary>
    public required string Key { get; init; }
    /// <summary>序列号或显示标识。 Serial or display identity.</summary>
    public required string Serial { get; init; }
    /// <summary>协议类型。 Protocol kind.</summary>
    public required DeviceProtocolKind Protocol { get; init; }
    /// <summary>传输类型。 Transport kind.</summary>
    public required DeviceTransportKind Transport { get; init; }
    /// <summary>Verified physical identity shared by protocol endpoints, or null when unavailable.</summary>
    public string? PhysicalId { get; init; }
    /// <summary>
    /// <para>server 为该端点解析出的网络端点，用于把同一设备的多条传输并入一张卡；两条端点共享同一别名时
    /// 才写入，绝不按地址形状推断。</para>
    /// Network endpoint the server resolved for this endpoint, written on every transport that shares
    /// it so several routes to one device merge into a single card; never inferred from address shape.
    /// </summary>
    public string? NetworkAlias { get; init; }
    /// <summary>
    /// <para>本行所属设备的稳定分组键：优先别名，其次 USB 物理身份，最后回退到端点自身键。</para>
    /// Stable grouping key for the device this row belongs to: the alias first, then the USB physical
    /// identity, then the endpoint's own key.
    /// </summary>
    public string GroupId => NetworkAlias ?? PhysicalId ?? Key;
    /// <summary>状态文本（device/offline/fastboot/edl/maskrom/loader…）。 State text.</summary>
    public string State { get; set; } = string.Empty;
    /// <summary>The observed EDL protocol, independent of USB presence.</summary>
    public EdlProtocolState EdlProtocol { get; set; }
    /// <summary>USB 厂商 ID（非 USB 为 0）。 USB vendor ID.</summary>
    public ushort VendorId { get; init; }
    /// <summary>USB 产品 ID（非 USB 为 0）。 USB product ID.</summary>
    public ushort ProductId { get; init; }
    /// <summary>显示名称。 Display name.</summary>
    public string DisplayName => Transport == DeviceTransportKind.Tcp
        ? $"{Serial} (network)"
        : $"{Serial} ({State})";

    /// <summary>Connection choice shown when one physical device exposes several protocols.</summary>
    public string ConnectionLabel => $"{ProtocolLabel} · {Transport} · {Serial}";

    /// <summary>Whether discovery identified a debug bridge server endpoint.</summary>
    public bool UsesServer => Protocol switch
    {
        DeviceProtocolKind.Adb => Key.StartsWith("adb:", StringComparison.Ordinal),
        DeviceProtocolKind.Hdc => Key.StartsWith("hdc:", StringComparison.Ordinal),
        _ => false
    };

    /// <summary>Protocol and route label, independent of the device's physical transport.</summary>
    public string ProtocolLabel => Protocol switch
    {
        DeviceProtocolKind.Adb => UsesServer ? "ADB server" : "ADB",
        DeviceProtocolKind.Hdc => UsesServer ? "HDC server" : "HDC",
        DeviceProtocolKind.Fastboot => "Fastboot",
        DeviceProtocolKind.Edl => "EDL",
        DeviceProtocolKind.Rkdl => "RKDL",
        _ => throw new ArgumentOutOfRangeException(nameof(Protocol), Protocol, "Unexpected protocol kind.")
    };
}

/// <summary>
/// <para>设备监视服务：启动时全量扫描 USB 设备，随后响应 USB 热插拔与 adb server 设备变更，
/// 并提供手动强制刷新。设备枚举与协议会话完全基于 UotanStudio.Comm；与官方 adb server 的
/// 交互（网络设备、状态查询、重启、shell）经 Comm 的 <c>AdbServerServices</c> 完成。</para>
/// Device monitor service: performs a full USB scan on startup, then reacts to USB
/// hotplug events and adb-server device changes, plus a manual force-refresh. Enumeration
/// and protocol sessions go through UotanStudio.Comm; interaction with the official adb
/// server (network devices, state queries, reboots, shell) uses Comm's AdbServerServices.
/// </summary>
public sealed partial class DeviceMonitorService : IDisposable
{
    /// <summary>
    /// <para>创建一个由调用方持有的独立设备监视实例。桌面 GUI 由组合根（<c>App</c>）拥有唯一的
    /// 监视实例；无头/CLI 等独立进程路径各自 <c>Create()</c> 一个，类型本身不提供全局服务定位单例。</para>
    /// Creates a caller-owned monitor instance. The desktop GUI keeps the single instance owned by the
    /// composition root (<c>App</c>); headless/CLI processes each <c>Create()</c> their own, so the type
    /// exposes no global service-locator singleton.
    /// </summary>
    public static DeviceMonitorService Create() => new();

    private readonly DeviceController _controller;
    private static HdcDeviceService HdcOperations => HdcDeviceService.FromSettings();

}