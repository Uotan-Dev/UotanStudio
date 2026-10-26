using System.Runtime.Versioning;
using UotanStudio.Comm.Transports.Usb.Drivers;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Platform-neutral facade over the WinUSB driver installer used by the USB tree view. Every method is
/// safe to call on any platform: on non-Windows systems queries report an absent device and mutating
/// operations fail without touching the system.
/// <para>USB 树视图使用的 WinUSB 驱动安装器平台中立面。所有方法在任意平台都可安全调用：非 Windows
/// 系统上查询上报设备不在位，变更操作直接失败且不触碰系统。</para>
/// </summary>
public interface IUsbDriverConfigService
{
    /// <summary>
    /// Gets whether driver configuration is supported here, which requires Windows.
    /// <para>获取当前环境是否支持驱动配置；仅 Windows 支持。</para>
    /// </summary>
    /// <value><see langword="true"/> only on Windows with the driver backend available.
    /// <para>仅在 Windows 且驱动后端可用时为 <see langword="true"/>。</para></value>
    bool IsSupported { get; }

    /// <summary>
    /// Gets whether the current process runs with an administrator token, which the install and
    /// uninstall operations require.
    /// <para>获取当前进程是否持有管理员令牌；安装与卸载操作需要该令牌。</para>
    /// </summary>
    /// <value><see langword="true"/> when the process is elevated on Windows.
    /// <para>Windows 上进程已提权时为 <see langword="true"/>。</para></value>
    bool IsElevated { get; }

    /// <summary>
    /// Reads the current driver binding of one device instance without changing anything. The query
    /// never throws; a device that cannot be located reports <see cref="WinUsbDriverState.DevicePresent"/>
    /// as <see langword="false"/>.
    /// <para>读取一个设备实例当前的驱动绑定，不做任何变更。查询绝不抛出；定位不到的设备返回
    /// <see cref="WinUsbDriverState.DevicePresent"/> 为 <see langword="false"/>。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id.<para>设备实例 ID。</para></param>
    /// <returns>The observed driver state; an absent-device state outside Windows.
    /// <para>观测到的驱动状态；非 Windows 下为设备不在位的状态。</para></returns>
    WinUsbDriverState QueryState(string instanceId);

    /// <summary>
    /// Installs a self-signed WinUSB driver package and binds it to the device. The operation requires
    /// an administrator token and adds the app's certificate to the local machine trusted stores.
    /// <para>安装自签名 WinUSB 驱动包并绑定到设备。操作需要管理员令牌，并把应用证书加入本机受信任
    /// 存储。</para>
    /// </summary>
    /// <param name="request">The install parameters.<para>安装参数。</para></param>
    /// <returns>The operation result; never throws and fails outside Windows.
    /// <para>操作结果；绝不抛出，非 Windows 下返回失败。</para></returns>
    WinUsbDriverOperationResult Install(WinUsbDriverInstallRequest request);

    /// <summary>
    /// Removes the WinUSB driver package installed by this app from the device and restores the system
    /// default driver. Requires an administrator token and never removes vendor drivers.
    /// <para>卸载本应用为设备安装的 WinUSB 驱动包并恢复系统默认驱动。需要管理员令牌，且绝不移除
    /// 厂商驱动。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id.<para>设备实例 ID。</para></param>
    /// <returns>The operation result; never throws and fails outside Windows.
    /// <para>操作结果；绝不抛出，非 Windows 下返回失败。</para></returns>
    WinUsbDriverOperationResult Uninstall(string instanceId);
}

/// <summary>
/// Default <see cref="IUsbDriverConfigService"/> implementation wrapping the single
/// <see cref="WinUsbDriverInstaller"/> instance. Windows-only members are attributed so the installer
/// is only reached through guarded paths, while the class itself stays constructible on every platform.
/// <para><see cref="IUsbDriverConfigService"/> 的默认实现，内部唯一持有
/// <see cref="WinUsbDriverInstaller"/> 实例。Windows 专属成员带平台标注，安装器只经守卫路径触达，
/// 类本身在任何平台都可构造。</para>
/// </summary>
public sealed class UsbDriverConfigService : IUsbDriverConfigService
{
    private static WinUsbDriverInstaller? _installer;

    /// <summary>
    /// Gets the shared default service instance used when no implementation is injected.
    /// <para>获取未注入实现时使用的共享默认服务实例。</para>
    /// </summary>
    /// <value>The default service instance.<para>默认服务实例。</para></value>
    public static IUsbDriverConfigService Default { get; } = new UsbDriverConfigService();

    [SupportedOSPlatform("windows")]
    private static WinUsbDriverInstaller Installer => _installer ??= new WinUsbDriverInstaller();

    /// <summary>Whether driver configuration is supported here. <para>当前环境是否支持驱动配置。</para></summary>
    public bool IsSupported => OperatingSystem.IsWindows() && WinUsbDriverInstaller.IsSupported;

    /// <summary>Whether the process runs with an administrator token. <para>进程是否持有管理员令牌。</para></summary>
    public bool IsElevated => OperatingSystem.IsWindows() && WinUsbDriverInstaller.IsElevated;

    /// <inheritdoc cref="IUsbDriverConfigService.QueryState" />
    [SupportedOSPlatform("windows")]
    public WinUsbDriverState QueryState(string instanceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverState { DevicePresent = false, IsManagedByHost = false };
        }

        return Installer.QueryState(instanceId);
    }

    /// <inheritdoc cref="IUsbDriverConfigService.Install" />
    [SupportedOSPlatform("windows")]
    public WinUsbDriverOperationResult Install(WinUsbDriverInstallRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed, "Windows only");
        }

        return Installer.Install(request);
    }

    /// <inheritdoc cref="IUsbDriverConfigService.Uninstall" />
    [SupportedOSPlatform("windows")]
    public WinUsbDriverOperationResult Uninstall(string instanceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed, "Windows only");
        }

        return Installer.Uninstall(instanceId);
    }
}