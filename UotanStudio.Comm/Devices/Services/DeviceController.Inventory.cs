using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// <para>L4 原始 USB 承载：向服务层暴露枚举、热插拔监视与按键重开，使宿主无需直接命名
/// <c>CommKit</c>。这些方法只是对控制器所用同一通信套件的行为等价转发，不改变任何路由或
/// 物理关联规则。</para>
/// L4 raw-USB surface: exposes enumeration, hotplug monitoring and reopen-by-key so hosts need not
/// name <c>CommKit</c>. Each method is a behavior-identical forward to the same communication kit the
/// controller already uses; it changes no routing or physical-association rule.
/// </summary>
public sealed partial class DeviceController
{
    private IDisposable? _removalWatch;

    /// <summary>Enumerates the raw USB inventory behind the controller's endpoints. 枚举控制器端点背后的原始 USB 清单。</summary>
    public IReadOnlyList<UsbDeviceInfo> EnumerateUsb(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null)
        => RequiredKit.EnumerateUsbDevices(apiKind, filter);

    /// <summary>Monitors raw USB additions/removals. 监视原始 USB 新增与移除。</summary>
    public IDisposable WatchUsb(
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        TimeSpan? pollInterval = null,
        bool fireInitialSnapshot = false,
        Action<Exception>? onError = null)
        => RequiredKit.MonitorUsbDevices(onChanged, apiKind, filter, pollInterval, fireInitialSnapshot, onError);

    /// <summary>Reopens the physical device session matching the stable key. 按稳定设备键重开物理设备会话。</summary>
    internal IUsbDeviceSession? OpenUsbSessionByKey(string deviceKey) => RequiredKit.OpenUsbDeviceSessionByKey(deviceKey);

    /// <summary>
    /// <para>创建一条经本机 adb server 交互的 ADB 会话门面。</para>
    /// Creates an ADB session façade routed through the local adb server.
    /// </summary>
    public IAdbDeviceSession OpenServerAdbSession(string serial, string host, int port)
        => AdbSession.ForServer(serial, host, port);

    /// <summary>
    /// <para>创建一条经本机 HDC server 交互的 server 客户端门面，供需要 server 级操作
    /// （而非单设备会话）的调用方使用；设备级会话应优先经租约获得。</para>
    /// Creates an HDC server-client façade for callers that need server-level operations
    /// rather than a single device session; device-level sessions should preferably be
    /// obtained through leases.
    /// </summary>
    public HdcServerClient OpenServerHdcClient(string host, int port) => new(host, port);

    /// <summary>
    /// <para>Best-effort 读取 Fastboot <c>getvar serialno</c>，用于把 server 目标关联到物理 USB 设备；
    /// 认领/getvar 机制在 Comm 发现层，宿主只消费结果。</para>
    /// Best-effort Fastboot <c>getvar serialno</c> read for associating a server target with a physical USB
    /// device; the claim/getvar mechanism lives in the discovery layer and the host only consumes the result.
    /// </summary>
    public string? ReadFastbootSerial(UsbDeviceInfo info)
        => UotanStudio.Comm.Devices.Discovery.FastbootSerialReader.Read(RequiredKit, info);

    private ICommKit RequiredKit => _kit
        ?? throw new NotSupportedException("This controller was composed without a communication kit and cannot expose the raw USB surface.");

    /// <summary>
    /// Starts the OS-driven USB removal watch: on device removal every open lease bound to
    /// that physical device is disposed (queued commands cancelled first), its cached
    /// properties are dropped and <see cref="DeviceController.DeviceRemoved"/> fires; on a
    /// metadata change (mode switch) leases on the old interface are released and
    /// <see cref="DeviceController.DeviceChanged"/> fires with the cache retained.
    /// <para>启动操作系统驱动的 USB 移除监视：设备移除时释放绑定该物理设备的所有租约
    /// （先取消排队指令）、丢弃缓存参数并触发 <see cref="DeviceController.DeviceRemoved"/>；
    /// 元数据变化（模式切换）时释放旧接口租约、保留缓存并触发
    /// <see cref="DeviceController.DeviceChanged"/>。</para>
    /// </summary>
    public void StartRemovalWatch(Action<Exception>? onError = null)
    {
        lock (_gate)
        {
            if (_kit is null || _removalWatch is not null)
            {
                return;
            }

            _removalWatch = _kit.MonitorUsbDevices(
                changes =>
                {
                    foreach (var change in changes)
                    {
                        if (change.Kind == UsbDeviceChangeKind.Removed)
                        {
                            HandleDeviceRemoved(change.Device, metadataChanged: false);
                        }
                        else if (change.Kind == UsbDeviceChangeKind.Changed)
                        {
                            HandleDeviceRemoved(change.Device, metadataChanged: true);
                        }
                    }
                },
                onError: onError);
        }
    }
}