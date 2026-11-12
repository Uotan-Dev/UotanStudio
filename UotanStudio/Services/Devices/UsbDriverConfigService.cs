using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
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
    /// Gets whether driver configuration is supported here: Windows with the driver backend available, or
    /// Linux with a usable privilege-escalation launcher for the udev access configuration.
    /// <para>获取当前环境是否支持驱动配置：Windows 需驱动后端可用；Linux 需可用于 udev 访问配置的提权启动器。</para>
    /// </summary>
    /// <value><see langword="true"/> on a platform whose configuration backend is available.
    /// <para>平台配置后端可用时为 <see langword="true"/>。</para></value>
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
    /// in-box <c>WinUSB</c> service to it. The operation always runs in an elevated relaunch of the
    /// application — the UAC prompt appears only when the host is not elevated — because the install may
    /// fall back to the synchronous PnP install API, which is only allowed in the helper and would block
    /// the host process for a long time.
    /// <para>通过安装新生成的自签名 WinUSB 包接管设备，把系统内置 <c>WinUSB</c> 服务绑定到该设备。操作一律在
    /// 应用的提权重启中执行——仅宿主未提权时才出现 UAC 提示——因为安装可能回退到同步 PnP 安装 API，该 API 只
    /// 允许在助手进程中调用，否则会长时间阻塞宿主进程。</para>
    /// </summary>
    /// <param name="request">The takeover parameters.<para>接管参数。</para></param>
    /// <returns>The operation result; never throws and fails outside Windows.
    /// <para>操作结果；绝不抛出，非 Windows 下返回失败。</para></returns>
    WinUsbDriverOperationResult InstallTakeover(WinUsbTakeoverRequest request);

    /// <summary>
    /// Ends the takeover of a device: uninstalls the self-signed package from the devices using it, deletes
    /// the takeover certificate and lets the device fall back to a system driver. The operation always runs
    /// in an elevated relaunch of the application because the driver uninstall can block device enumeration.
    /// <para>结束设备接管：从使用该自签名包的设备上卸载它、删除接管证书，并让设备回退系统驱动。驱动卸载可能
    /// 长时间占用设备枚举，因此操作一律在应用的提权重启中执行。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id.<para>设备实例 ID。</para></param>
    /// <returns>
    /// The operation result; never throws and fails outside Windows. A device that still runs the takeover
    /// driver reports <see cref="WinUsbDriverOperationStatus.RestartRequired"/>.
    /// <para>操作结果；绝不抛出，非 Windows 下返回失败。设备仍在运行接管驱动时返回
    /// <see cref="WinUsbDriverOperationStatus.RestartRequired"/>。</para>
    /// </returns>
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

    /// <summary>
    /// Reports whether the Linux udev access configuration for this vendor/product pair is installed. The
    /// check is read-only, needs no elevation, and reports <see langword="false"/> outside Linux.
    /// <para>报告该厂商/产品组合的 Linux udev 访问配置是否已安装。检查只读、无需提权，Linux 之外返回
    /// <see langword="false"/>。</para>
    /// </summary>
    /// <param name="vendorId">The USB vendor id. <para>USB 厂商 ID。</para></param>
    /// <param name="productId">The USB product id. <para>USB 产品 ID。</para></param>
    /// <returns><see langword="true"/> when the access rule is installed.
    /// <para>访问规则已安装时为 <see langword="true"/>。</para></returns>
    bool IsDeviceAccessConfigured(ushort vendorId, ushort productId);

    /// <summary>
    /// Installs the Linux udev access configuration for this vendor/product pair: the elevated child
    /// process writes the per-device rule and the usb-serial module block, unbinds the current claimant
    /// and re-applies the rules. Fails outside Linux.
    /// <para>为该厂商/产品组合安装 Linux udev 访问配置：提权子进程写入按设备规则与 usb-serial 模块屏蔽、
    /// 解绑当前占用者并重新应用规则。Linux 之外返回失败。</para>
    /// </summary>
    /// <param name="vendorId">The USB vendor id. <para>USB 厂商 ID。</para></param>
    /// <param name="productId">The USB product id. <para>USB 产品 ID。</para></param>
    /// <returns>The operation result; never throws. <para>操作结果；绝不抛出。</para></returns>
    LinuxDeviceAccessResult ConfigureDeviceAccess(ushort vendorId, ushort productId);

    /// <summary>
    /// Removes the Linux udev access configuration for this vendor/product pair: the elevated child
    /// process deletes the per-device rule and the usb-serial module block and re-applies the rules.
    /// Fails outside Linux.
    /// <para>移除该厂商/产品组合的 Linux udev 访问配置：提权子进程删除按设备规则与 usb-serial 模块屏蔽并
    /// 重新应用规则。Linux 之外返回失败。</para>
    /// </summary>
    /// <param name="vendorId">The USB vendor id. <para>USB 厂商 ID。</para></param>
    /// <param name="productId">The USB product id. <para>USB 产品 ID。</para></param>
    /// <returns>The operation result; never throws. <para>操作结果；绝不抛出。</para></returns>
    LinuxDeviceAccessResult UnconfigureDeviceAccess(ushort vendorId, ushort productId);
}

/// <summary>
/// Default <see cref="IUsbDriverConfigService"/> implementation wrapping the single
/// <see cref="WinUsbDriverInstaller"/> instance. Windows-only members are attributed so the installer
/// is only reached through guarded paths, while the class itself stays constructible on every platform.
/// Taking over a device and ending that takeover both run through an elevated relaunch of the same
/// executable, because the install may fall back to the synchronous PnP install API and the uninstall can
/// block device enumeration; the vendor package operations stay in-process when the host already holds an
/// administrator token.
/// <para><see cref="IUsbDriverConfigService"/> 的默认实现，内部唯一持有
/// <see cref="WinUsbDriverInstaller"/> 实例。Windows 专属成员带平台标注，安装器只经守卫路径触达，
/// 类本身在任何平台都可构造。接管安装与解除接管一律通过以管理员身份重启同一个可执行文件完成：安装可能回退到
/// 同步 PnP 安装 API，卸载可能阻塞设备枚举；宿主已持管理员令牌时，厂商驱动包操作在本进程内执行。</para>
/// </summary>
public sealed class UsbDriverConfigService : IUsbDriverConfigService
{
    private const int ErrorCancelled = 1223;

    /// <summary>The longest the host waits for the elevated helper before it terminates it.
    /// <para>宿主等待提权助手的最长时间，超时后终止该子进程。</para></summary>
    private static readonly TimeSpan ElevatedTimeout = TimeSpan.FromSeconds(180);

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
    public bool IsSupported =>
        (OperatingSystem.IsWindows() && WinUsbDriverInstaller.IsSupported) ||
        (OperatingSystem.IsLinux() && LinuxUdevDeviceAccess.IsLauncherAvailable);

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

        List<string> arguments =
        [
            UsbDriverElevatedCommand.TakeoverSwitch, request.InstanceId, request.HardwareId
        ];
        if (request.DeviceName is { Length: > 0 } deviceName)
        {
            arguments.Add(deviceName);
        }

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

    /// <inheritdoc cref="IUsbDriverConfigService.IsDeviceAccessConfigured" />
    public bool IsDeviceAccessConfigured(ushort vendorId, ushort productId)
        => OperatingSystem.IsLinux() && LinuxUdevDeviceAccess.IsConfigured(vendorId, productId);

    /// <inheritdoc cref="IUsbDriverConfigService.ConfigureDeviceAccess" />
    public LinuxDeviceAccessResult ConfigureDeviceAccess(ushort vendorId, ushort productId)
        => OperatingSystem.IsLinux()
            ? LinuxUdevDeviceAccess.Configure(vendorId, productId)
            : new LinuxDeviceAccessResult(LinuxDeviceAccessStatus.Failed, "Linux only");

    /// <inheritdoc cref="IUsbDriverConfigService.UnconfigureDeviceAccess" />
    public LinuxDeviceAccessResult UnconfigureDeviceAccess(ushort vendorId, ushort productId)
        => OperatingSystem.IsLinux()
            ? LinuxUdevDeviceAccess.Unconfigure(vendorId, productId)
            : new LinuxDeviceAccessResult(LinuxDeviceAccessStatus.Failed, "Linux only");

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
    /// operation, and returns the outcome the child reported through the result file. The wait is bounded by
    /// <see cref="ElevatedTimeout"/>, which covers a forced PnP install of a USB devnode: a helper that PnP
    /// leaves blocked past that bound is terminated, so a stuck device can never freeze the caller.
    /// <para>以 <c>runas</c> 重启应用，等待提权子进程完成驱动操作，并返回子进程经结果文件上报的结果。等待受
    /// <see cref="ElevatedTimeout"/> 限制，该上限覆盖 USB 设备节点的一次强制 PnP 安装：超过上限仍被 PnP
    /// 阻塞的助手会被终止，卡住的设备因此不会冻结调用方。</para>
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

            if (!process.WaitForExit((int)ElevatedTimeout.TotalMilliseconds))
            {
                var killed = TryKillElevated(process);
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    $"The elevated driver operation did not finish within {ElevatedTimeout.TotalSeconds:0} seconds " +
                    $"and was {(killed ? "terminated" : "left running because it could not be terminated")}; " +
                    "the device state may be incomplete.");
            }

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
    private static bool TryKillElevated(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
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
        nameof(WinUsbDriverOperationStatus.RestartRequired) => WinUsbDriverOperationStatus.RestartRequired,
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