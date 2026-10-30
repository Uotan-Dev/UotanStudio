using System.ComponentModel;
using System.Diagnostics;
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
    /// Gets whether the current process runs with an administrator token, which the takeover and package
    /// operations require.
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
    /// Takes over the device by installing a freshly generated self-signed WinUSB package that binds the
    /// in-box <c>WinUSB</c> service to it. The operation requires an administrator token; a non-elevated
    /// process raises a UAC prompt and installs from an elevated child process.
    /// <para>通过安装新生成的自签名 WinUSB 包接管设备，把系统内置 <c>WinUSB</c> 服务绑定到该设备。操作需要
    /// 管理员令牌；进程未提权时会弹出 UAC 提示，并由提权后的子进程完成安装。</para>
    /// </summary>
    /// <param name="request">The takeover parameters.<para>接管参数。</para></param>
    /// <returns>The operation result; never throws and fails outside Windows.
    /// <para>操作结果；绝不抛出，非 Windows 下返回失败。</para></returns>
    WinUsbDriverOperationResult InstallTakeover(WinUsbTakeoverRequest request);

    /// <summary>
    /// Ends the takeover of a device: removes the self-signed package this app installed, deletes the
    /// takeover certificate and lets the device fall back to a system driver. Requires an administrator
    /// token obtained the same way as the install.
    /// <para>结束设备接管：移除本应用安装的自签名包、删除接管证书，并让设备回退系统驱动。需要与管理器安装
    /// 相同方式获得的管理员令牌。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id.<para>设备实例 ID。</para></param>
    /// <returns>The operation result; never throws and fails outside Windows.
    /// <para>操作结果；绝不抛出，非 Windows 下返回失败。</para></returns>
    WinUsbDriverOperationResult UninstallTakeover(string instanceId);

    /// <summary>
    /// Stages one vendor driver package into the driver store and asks PnP to install it on every present
    /// device that currently has no driver. The operation requires an administrator token; a non-elevated
    /// process raises a UAC prompt and stages from an elevated child process.
    /// <para>把一个厂商驱动包暂存到驱动仓库，并请求 PnP 在所有当前无驱动的在位设备上安装。操作需要管理员
    /// 令牌；进程未提权时会弹出 UAC 提示，并由提权后的子进程完成暂存。</para>
    /// </summary>
    /// <param name="packageId">The package identifier.<para>驱动包标识。</para></param>
    /// <returns>The operation result; never throws and fails outside Windows.
    /// <para>操作结果；绝不抛出，非 Windows 下返回失败。</para></returns>
    WinUsbDriverOperationResult InstallPackage(string packageId);

    /// <summary>
    /// Removes the staged driver packages of one vendor package, leaving packages this application did not
    /// stage untouched. Requires an administrator token obtained the same way as the install.
    /// <para>移除一个厂商包已暂存的驱动包，绝不动非本应用暂存的包。需要与安装相同方式获得的管理员令牌。</para>
    /// </summary>
    /// <param name="packageId">The package identifier.<para>驱动包标识。</para></param>
    /// <returns>The operation result; never throws and fails outside Windows.
    /// <para>操作结果；绝不抛出，非 Windows 下返回失败。</para></returns>
    WinUsbDriverOperationResult UninstallPackage(string packageId);
}

/// <summary>
/// Default <see cref="IUsbDriverConfigService"/> implementation wrapping the single
/// <see cref="WinUsbDriverInstaller"/> instance. Windows-only members are attributed so the installer
/// is only reached through guarded paths, while the class itself stays constructible on every platform.
/// A non-elevated Windows process runs every mutating operation through an elevated relaunch of the same
/// executable, so the driver work always happens with an administrator token.
/// <para><see cref="IUsbDriverConfigService"/> 的默认实现，内部唯一持有
/// <see cref="WinUsbDriverInstaller"/> 实例。Windows 专属成员带平台标注，安装器只经守卫路径触达，
/// 类本身在任何平台都可构造。Windows 上进程未提权时，安装与卸载通过以管理员身份重启同一个可执行
/// 文件完成，驱动操作始终持有管理员令牌。</para>
/// </summary>
public sealed class UsbDriverConfigService : IUsbDriverConfigService
{
    private const int ErrorCancelled = 1223;

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
            return new WinUsbDriverState { DevicePresent = false };
        }

        return Installer.QueryState(instanceId);
    }


    /// <inheritdoc cref="IUsbDriverConfigService.InstallTakeover" />
    [SupportedOSPlatform("windows")]
    public WinUsbDriverOperationResult InstallTakeover(WinUsbTakeoverRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed, "Windows only");
        }

        if (WinUsbDriverInstaller.IsElevated)
        {
            return Installer.InstallTakeover(request);
        }

        List<string> arguments =
        [
            UsbDriverElevatedCommand.TakeoverSwitch, request.InstanceId, request.HardwareId
        ];

        return RunElevated(arguments);
    }

    /// <inheritdoc cref="IUsbDriverConfigService.UninstallTakeover" />
    [SupportedOSPlatform("windows")]
    public WinUsbDriverOperationResult UninstallTakeover(string instanceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed, "Windows only");
        }

        if (WinUsbDriverInstaller.IsElevated)
        {
            return Installer.UninstallTakeover(instanceId);
        }

        return RunElevated([UsbDriverElevatedCommand.ReleaseSwitch, instanceId]);
    }

    /// <inheritdoc cref="IUsbDriverConfigService.InstallPackage" />
    [SupportedOSPlatform("windows")]
    public WinUsbDriverOperationResult InstallPackage(string packageId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed, "Windows only");
        }

        if (WinUsbDriverInstaller.IsElevated)
        {
            return new DriverPackageInstaller().Install(packageId);
        }

        return RunElevated([UsbDriverElevatedCommand.PackageInstallSwitch, packageId]);
    }

    /// <inheritdoc cref="IUsbDriverConfigService.UninstallPackage" />
    [SupportedOSPlatform("windows")]
    public WinUsbDriverOperationResult UninstallPackage(string packageId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed, "Windows only");
        }

        if (WinUsbDriverInstaller.IsElevated)
        {
            return new DriverPackageInstaller().Uninstall(packageId);
        }

        return RunElevated([UsbDriverElevatedCommand.PackageUninstallSwitch, packageId]);
    }

    /// <summary>
    /// Relaunches the application with <c>runas</c>, waits for the elevated child to finish the driver
    /// operation, and returns the outcome the child reported through the result file. The helper only
    /// invokes driver-store operations (<c>SetupCopyOEMInf</c>, <c>CM_Reenumerate_DevNode</c>,
    /// <c>SetupUninstallOEMInf</c>) that terminate on their own, so the wait has no timeout.
    /// <para>以 <c>runas</c> 重启应用，等待提权子进程完成驱动操作，并返回子进程经结果文件上报的结果。
    /// 助手只调用会自行结束的驱动仓库操作（<c>SetupCopyOEMInf</c>、
    /// <c>CM_Reenumerate_DevNode</c>、<c>SetupUninstallOEMInf</c>），因此等待不设超时。</para>
    /// </summary>
    /// <param name="arguments">The helper arguments describing the operation.
    /// <para>描述该操作的助手参数。</para></param>
    /// <returns>
    /// The child's result; <see cref="WinUsbDriverOperationStatus.ElevationRequired"/> when the UAC prompt
    /// is cancelled or the host process cannot relaunch itself.
    /// <para>子进程的结果；UAC 提示被取消或宿主进程无法重启自身时为
    /// <see cref="WinUsbDriverOperationStatus.ElevationRequired"/>。</para>
    /// </returns>
    [SupportedOSPlatform("windows")]
    private static WinUsbDriverOperationResult RunElevated(IReadOnlyList<string> arguments)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath) ||
            string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.ElevationRequired,
                "The driver operation requires an administrator token and this host cannot relaunch itself; " +
                "start the application as administrator.");
        }

        var resultPath = Path.Combine(Path.GetTempPath(), "UotanStudio.DriverInstall",
            Path.GetRandomFileName() + ".result");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add(UsbDriverElevatedCommand.ResultSwitch);
            startInfo.ArgumentList.Add(resultPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    "The elevated driver operation could not be started.");
            }

            process.WaitForExit();
            return ReadElevatedResult(resultPath, process.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.ElevationRequired,
                "The administrator prompt was cancelled; the driver operation did not run.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The elevated driver operation could not be completed: {ex.Message}");
        }
        finally
        {
            TryDeleteResultFile(resultPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private static WinUsbDriverOperationResult ReadElevatedResult(string resultPath, int exitCode)
    {
        try
        {
            if (File.Exists(resultPath))
            {
                var text = File.ReadAllText(resultPath);
                var separator = text.IndexOf('\n');
                var statusText = (separator < 0 ? text : text[..separator]).Trim();
                var message = separator < 0 ? null : text[(separator + 1)..].Trim();
                if (ParseStatus(statusText) is { } status)
                {
                    return new WinUsbDriverOperationResult(status,
                        string.IsNullOrEmpty(message) ? null : message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return exitCode == 0
            ? new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                "The driver operation completed in the elevated process.")
            : new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The elevated driver operation exited with code {exitCode} without reporting a result.");
    }

    [SupportedOSPlatform("windows")]
    private static WinUsbDriverOperationStatus? ParseStatus(string statusText) => statusText switch
    {
        nameof(WinUsbDriverOperationStatus.Succeeded) => WinUsbDriverOperationStatus.Succeeded,
        nameof(WinUsbDriverOperationStatus.ElevationRequired) => WinUsbDriverOperationStatus.ElevationRequired,
        nameof(WinUsbDriverOperationStatus.DeviceNotFound) => WinUsbDriverOperationStatus.DeviceNotFound,
        nameof(WinUsbDriverOperationStatus.Failed) => WinUsbDriverOperationStatus.Failed,
        _ => null
    };

    private static void TryDeleteResultFile(string resultPath)
    {
        try
        {
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}