using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Composes the standard discovery sources, route-validating opener, and session factories into one controller.
/// <para>将标准发现源、路由校验开启器与会话工厂组合成单一控制器。</para>
/// Desktop and headless consumers must build their controller through this factory so the route rules and
/// endpoint identity semantics stay identical across hosts.
/// <para>桌面与 headless 消费者必须经此工厂构建控制器，使路由规则与端点身份语义在各宿主间保持一致。</para>
/// </summary>
public static class DeviceControllerFactory
{
    /// <summary>
    /// Creates the default controller over the shared communication kit, so callers need not name <c>CommKit</c>.
    /// Creation fixes the persisted route mode, probes the default adb and HDC endpoints once and applies
    /// <para>用共享通信套件与标准本机调试桥端点创建默认控制器，使调用方无需直接命名 CommKit。
    /// 创建时先固定持久化路由模式，再对默认 adb 与 HDC 端点各做一次启动探测，把结论作为同一份快照应用，
    /// 然后由 server 监视循环保持更新（启动冻结模式除外）。</para>
    /// both outcomes as one snapshot; the server-state watch then keeps them fresh except in
    /// <see cref="DebugBridgeRouteMode.StartupFrozen"/> mode.</summary>
    /// <returns>A controller with removal and server-state watches already running.
    /// <para>已启动移除监视与 server 状态监视的控制器。</para></returns>
    public static DeviceController CreateDefault() => CreateDefault(CommKit.Default);

    /// <summary>Creates the default controller over the supplied communication kit and the standard localhost debug-bridge endpoints.</summary>
    internal static DeviceController CreateDefault(ICommKit kit)
    {
        ArgumentNullException.ThrowIfNull(kit);
        DebugBridgeRoute.ConfigureMode(DeviceCommunicationConfiguration.Current.RouteMode);
        DebugBridgeRoute.InitializeDefaultEndpoints();
        var controller = new DeviceController(
            [
                new UsbDiscoverySource(kit, UsbApiKind.Auto),
                new AdbServerDiscoverySource(),
                new HdcServerDiscoverySource(),
                new UsbmuxdDiscoverySource()
            ],
            new CommDeviceTransportOpener(kit),
            DefaultDeviceSessionFactories.All,
            kit);
        controller.StartRemovalWatch();
        controller.StartServerStateWatch();
        return controller;
    }
}