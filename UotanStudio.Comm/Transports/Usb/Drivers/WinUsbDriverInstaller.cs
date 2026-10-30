using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// The observed driver binding of one device instance.
/// <para>一个设备实例当前驱动绑定的观测结果。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed record WinUsbDriverState
{
    /// <summary>
    /// Whether the device instance is currently present.
    /// <para>设备实例当前是否在位。</para>
    /// </summary>
    public required bool DevicePresent { get; init; }

    /// <summary>
    /// The kernel service name of the bound driver, for example <c>Rockusb</c>.
    /// <para>已绑定驱动的内核服务名，例如 <c>Rockusb</c>。</para>
    /// </summary>
    public string? DriverService { get; init; }

    /// <summary>
    /// The driver INF name backing the device, for example <c>oem42.inf</c>.
    /// <para>设备使用的驱动 INF 名，例如 <c>oem42.inf</c>。</para>
    /// </summary>
    public string? DriverInfPath { get; init; }

    /// <summary>
    /// The driver provider name recorded on the device.
    /// <para>设备上记录的驱动提供者名。</para>
    /// </summary>
    public string? DriverProvider { get; init; }

    /// <summary>
    /// Whether the bound driver is the self-signed WinUSB package this application installed for device
    /// takeover, which is the case when the kernel service is <c>WinUSB</c> and the provider matches
    /// <see cref="WinUsbSelfSignedDriverPackage.ProviderName"/>.
    /// <para>当前绑定是否为本应用为设备接管安装的自签名 WinUSB 包：内核服务为 <c>WinUSB</c> 且提供者与
    /// <see cref="WinUsbSelfSignedDriverPackage.ProviderName"/> 相符时成立。</para>
    /// </summary>
    public bool IsTakeoverBinding { get; init; }
}

/// <summary>
/// The parameters of one device takeover: the device to bind and the hardware id the generated INF
/// matches.
/// <para>一次设备接管的参数：要绑定的设备，以及生成 INF 所匹配的硬件 ID。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed record WinUsbTakeoverRequest
{
    /// <summary>
    /// The device instance id, for example <c>USB\VID_0E8D&amp;PID_2000\5&amp;1A2B3C4D&amp;0&amp;1</c>.
    /// <para>设备实例 ID，例如 <c>USB\VID_0E8D&amp;PID_2000\5&amp;1A2B3C4D&amp;0&amp;1</c>。</para>
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// The hardware id the generated INF matches, for example <c>USB\VID_0E8D&amp;PID_2000</c>.
    /// <para>生成 INF 匹配的硬件 ID，例如 <c>USB\VID_0E8D&amp;PID_2000</c>。</para>
    /// </summary>
    public required string HardwareId { get; init; }

    /// <summary>
    /// The device description the generated INF publishes, normally naming the device mode the caller
    /// recognized, for example <c>UotanStudio WinUSB (Rockchip RKDL)</c>. A missing description falls back
    /// to the hardware id.
    /// <para>生成的 INF 发布的设备描述，通常写明调用方识别到的设备模式，例如
    /// <c>UotanStudio WinUSB (Rockchip RKDL)</c>。未提供时回退为硬件 ID。</para>
    /// </summary>
    public string? DeviceName { get; init; }
}

/// <summary>
/// The outcome of a driver operation.
/// <para>驱动操作的结果状态。</para>
/// </summary>
public enum WinUsbDriverOperationStatus
{
    /// <summary>The operation completed. <para>操作成功完成。</para></summary>
    Succeeded,

    /// <summary>The operation needs an administrator token. <para>操作需要管理员令牌。</para></summary>
    ElevationRequired,

    /// <summary>The target device instance was not found. <para>目标设备实例未找到。</para></summary>
    DeviceNotFound,

    /// <summary>The operation failed; see the message. <para>操作失败；详见消息。</para></summary>
    Failed
}

/// <summary>
/// The result of one driver operation: a status plus an optional human-readable message.
/// <para>一次驱动操作的结果：状态加可选的可读消息。</para>
/// </summary>
/// <param name="Status">The operation outcome. <para>操作结果。</para></param>
/// <param name="Message">Optional detail or failure reason. <para>可选的细节或失败原因。</para></param>
[SupportedOSPlatform("windows")]
public sealed record WinUsbDriverOperationResult(WinUsbDriverOperationStatus Status, string? Message = null);

/// <summary>
/// Takes over a device by staging a generated self-signed WinUSB package and asking PnP to bind it, and
/// releases that binding again. Every other vendor driver package, including the Rockchip DriverAssistant
/// package, is installed and removed through <see cref="DriverPackageInstaller"/>. Both mutating operations
/// require an administrator token, never throw, and report failures through
/// <see cref="WinUsbDriverOperationResult"/>.
/// <para>通过暂存生成的自签名 WinUSB 包并请求 PnP 绑定来接管设备，并在之后释放该绑定。其余厂商驱动包（含
/// 瑞芯微 DriverAssistant 包）的安装与卸载由 <see cref="DriverPackageInstaller"/> 负责。两个变更操作都需要
/// 管理员令牌，绝不抛出，失败经 <see cref="WinUsbDriverOperationResult"/> 返回。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinUsbDriverInstaller
{
    /// <summary>The kernel service name of the in-box WinUSB driver the takeover package binds.
    /// <para>接管包所绑定的系统内置 WinUSB 驱动内核服务名。</para></summary>
    private const string WinUsbService = "WinUSB";

    /// <summary>The upper bound the takeover install waits for the observed binding to switch.
    /// <para>接管安装等待观测绑定切换的上界。</para></summary>
    private static readonly TimeSpan BindingWaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The interval between two binding observations during takeover.
    /// <para>接管期间两次绑定观测之间的间隔。</para></summary>
    private static readonly TimeSpan BindingPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets whether this platform supports the installer; always <see langword="true"/> on Windows.
    /// <para>获取当前平台是否支持安装器;Windows 上恒为 <see langword="true"/>。</para>
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Gets whether the current process runs with an administrator token.
    /// <para>获取当前进程是否以管理员令牌运行。</para>
    /// </summary>
    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is SecurityException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Reads the current driver binding of a device instance without changing anything.
    /// <para>读取一个设备实例当前的驱动绑定，不做任何变更。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id. <para>设备实例 ID。</para></param>
    /// <returns>
    /// The observed state; a device that cannot be located reports
    /// <see cref="WinUsbDriverState.DevicePresent"/> as <see langword="false"/> with null fields.
    /// <para>观测到的状态；定位不到的设备返回 <see cref="WinUsbDriverState.DevicePresent"/> 为
    /// <see langword="false"/> 且其余字段为 null。</para>
    /// </returns>
    public WinUsbDriverState QueryState(string instanceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(instanceId) ||
            (WinUsbDriverNativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId,
                WinUsbDriverNativeMethods.CmLocateDevnodeNormal) != WinUsbDriverNativeMethods.CrSuccess))
        {
            return new WinUsbDriverState { DevicePresent = false };
        }

        var service = WinUsbDriverNativeMethods.ReadDevNodeString(devInst, WinUsbDriverNativeMethods.DriverServiceKey);
        var infPath = WinUsbDriverNativeMethods.ReadDevNodeString(devInst, WinUsbDriverNativeMethods.DriverInfPathKey);
        var provider = WinUsbDriverNativeMethods.ReadDevNodeString(devInst, WinUsbDriverNativeMethods.DriverProviderKey);
        return new WinUsbDriverState
        {
            DevicePresent = true,
            DriverService = service,
            DriverInfPath = infPath,
            DriverProvider = provider,
            IsTakeoverBinding = IsTakeoverBindingState(service, provider)
        };
    }

    /// <summary>
    /// Takes over the device by staging a freshly generated self-signed WinUSB driver package and asking
    /// PnP to bind it. Requires an administrator token; the package binds the in-box <c>WinUSB</c> service
    /// to the one hardware id in <paramref name="request"/>, so the device becomes openable through the
    /// WinUSB path used for MediaTek preloader and download-mode access. The certificate is added to the
    /// local-machine <c>Root</c> and <c>TrustedPublisher</c> stores and its private key is destroyed before
    /// it is published. A devnode whose install previously failed is bound through an asynchronous retry
    /// installation, every other present devnode needs a forced install that PnP completes inside this call,
    /// and both paths wait at most <see cref="BindingWaitTimeout"/> for the observed binding to become the
    /// takeover package. A devnode that is pending removal is rejected before anything is staged.
    /// <para>通过暂存新生成的自签名 WinUSB 驱动包并请求 PnP 绑定来接管设备。需要管理员令牌；该包把系统内置
    /// <c>WinUSB</c> 服务绑定到 <paramref name="request"/> 中的单个硬件 ID，使设备可由 MediaTek 预加载器
    /// 与下载模式访问所用的 WinUSB 路径打开。证书会被加入本机 <c>Root</c> 与 <c>TrustedPublisher</c>
    /// 存储，并在发布前销毁私钥。此前安装失败的设备节点经异步重试安装完成绑定，其余在位节点必须经 PnP 在本次
    /// 调用内完成的强制安装，两条路径都最多等待 <see cref="BindingWaitTimeout"/> 观测绑定是否变为接管包。
    /// 处于移除中的设备节点会在暂存任何内容之前被拒绝。</para>
    /// </summary>
    /// <param name="request">The takeover parameters.<para>接管参数。</para></param>
    /// <returns>The operation result; never throws.<para>操作结果；绝不抛出。</para></returns>
    public WinUsbDriverOperationResult InstallTakeover(WinUsbTakeoverRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "Device takeover is only supported on Windows.");
        }

        if (!IsElevated)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.ElevationRequired,
                "Taking over a device requires an administrator token.");
        }

        if (string.IsNullOrWhiteSpace(request.InstanceId) || string.IsNullOrWhiteSpace(request.HardwareId))
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "The takeover request must carry a device instance id and a hardware id.");
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "UotanStudio.WinUsbTakeover",
            Path.GetRandomFileName());
        try
        {
            var (present, status, problem) = ReadDevNodeStatus(request.InstanceId);
            if (present && IsRemovalPending(status))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    $"The device instance '{request.InstanceId}' is pending removal (devnode status 0x{status:X8}); replug the device before taking it over.");
            }

            var current = QueryState(request.InstanceId);
            if (current is { DevicePresent: true, IsTakeoverBinding: true } && problem == 0)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                    $"'{request.HardwareId}' already runs the self-signed WinUSB package and the device started.");
            }

            WinUsbSelfSignedDriverPackage.PurgeLegacyCertificates();
            Directory.CreateDirectory(workDirectory);
            var package = WinUsbSelfSignedDriverPackage.Create(workDirectory, request.HardwareId,
                request.InstanceId, request.DeviceName);
            if (!WinUsbDriverNativeMethods.SetupCopyOEMInfW(package.InfPath, IntPtr.Zero,
                    WinUsbDriverNativeMethods.SpOemSourcePath, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    WinUsbDriverNativeMethods.DescribeLastWin32Error("SetupCopyOEMInf failed"));
            }

            var bound = current.IsTakeoverBinding;
            if (!bound && RequestRebind(request.InstanceId))
            {
                bound = WaitForTakeoverBinding(request.InstanceId);
            }
            else if (!bound && ReadDevNodeStatus(request.InstanceId).Present)
            {
                bound = WinUsbDriverNativeMethods.UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero,
                        request.HardwareId, package.InfPath,
                        WinUsbDriverNativeMethods.InstallFlagForce |
                        WinUsbDriverNativeMethods.InstallFlagNonInteractive,
                        out _) &&
                    WaitForTakeoverBinding(request.InstanceId);
            }

            var (stillPresent, _, stillProblem) = ReadDevNodeStatus(request.InstanceId);
            if (!stillPresent)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                    $"Staged the self-signed WinUSB package for '{request.HardwareId}'; the device is no longer present and binds on its next enumeration.");
            }

            if (bound && stillProblem == 0)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                    $"Staged and bound the self-signed WinUSB package for '{request.HardwareId}'; the device started.");
            }

            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                bound
                    ? $"Bound the self-signed WinUSB package for '{request.HardwareId}' but the device did not start (problem 0x{stillProblem:X})."
                    : $"Staged the self-signed WinUSB package for '{request.HardwareId}' but the device did not bind (problem 0x{stillProblem:X}).");
        }
        catch (Exception ex)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The WinUSB takeover package could not be prepared: {Describe(ex)}");
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    /// Ends the takeover of a device: removes the staged package with <c>SetupUninstallOEMInf</c>, asks PnP
    /// to re-enumerate the devnode so the device falls back to a system driver, and deletes the takeover
    /// certificate from the local-machine <c>Root</c> and <c>TrustedPublisher</c> stores. Requires an
    /// administrator token and only acts on a binding this application installed.
    /// <para>结束设备接管：经 <c>SetupUninstallOEMInf</c> 移除暂存的包，请求 PnP 重枚举设备节点使设备回退
    /// 系统驱动，并从本机 <c>Root</c> 与 <c>TrustedPublisher</c> 存储删除接管证书。需要管理员令牌，
    /// 且只作用于本应用安装的绑定。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id.<para>设备实例 ID。</para></param>
    /// <returns>The operation result; never throws.<para>操作结果；绝不抛出。</para></returns>
    public WinUsbDriverOperationResult UninstallTakeover(string instanceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "Ending a device takeover is only supported on Windows.");
        }

        if (!IsElevated)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.ElevationRequired,
                "Ending a device takeover requires an administrator token.");
        }

        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "The takeover removal requires a device instance id.");
        }

        try
        {
            return EndTakeover(instanceId);
        }
        catch (Exception ex)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The WinUSB takeover could not be removed: {Describe(ex)}");
        }
    }

    private static WinUsbDriverOperationResult EndTakeover(string instanceId)
    {
        var located = WinUsbDriverNativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId,
            WinUsbDriverNativeMethods.CmLocateDevnodeNormal) == WinUsbDriverNativeMethods.CrSuccess;
        var service = located
            ? WinUsbDriverNativeMethods.ReadDevNodeString(devInst, WinUsbDriverNativeMethods.DriverServiceKey)
            : null;
        var provider = located
            ? WinUsbDriverNativeMethods.ReadDevNodeString(devInst, WinUsbDriverNativeMethods.DriverProviderKey)
            : null;
        var infName = located
            ? WinUsbDriverNativeMethods.ReadDevNodeString(devInst, WinUsbDriverNativeMethods.DriverInfPathKey)
            : null;

        if (!located && !WinUsbSelfSignedDriverPackage.RemoveCertificateFromStores())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.DeviceNotFound,
                $"No device instance matches '{instanceId}' and the takeover certificate could not be removed.");
        }

        if (located && !IsTakeoverBindingState(service, provider))
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "The current driver was not installed by this application's takeover; removal is refused to protect other drivers.");
        }

        if (located)
        {
            if (string.IsNullOrWhiteSpace(infName))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    "The takeover binding does not expose the staged package name; removal is refused.");
            }

            if (!WinUsbDriverNativeMethods.SetupUninstallOEMInfW(infName,
                    WinUsbDriverNativeMethods.SuoiForceDelete, IntPtr.Zero) &&
                Marshal.GetLastWin32Error() != WinUsbDriverNativeMethods.ErrorFileNotFound)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    WinUsbDriverNativeMethods.DescribeLastWin32Error("SetupUninstallOEMInf failed"));
            }
        }

        var certificateRemoved = WinUsbSelfSignedDriverPackage.RemoveCertificateFromStores();
        if (located)
        {
            _ = WinUsbDriverNativeMethods.CmReenumerateDevNode(devInst,
                WinUsbDriverNativeMethods.CmReenumerateRetryInstallation |
                WinUsbDriverNativeMethods.CmReenumerateAsynchronous);
        }
        return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
            certificateRemoved
                ? $"Removed the takeover package '{infName}' and its certificate; the device returns to a system driver."
                : $"Removed the takeover package '{infName}' but the certificate stores could not be cleaned.");
    }

    /// <summary>
    /// Formats a caught exception for a result message, keeping the exception type so a native boundary
    /// failure stays diagnosable.
    /// <para>把捕获的异常格式化为结果消息，并保留异常类型，使原生边界失败仍可诊断。</para>
    /// </summary>
    /// <param name="ex">The caught exception.<para>捕获到的异常。</para></param>
    /// <returns>The type name and message.<para>类型名与消息。</para></returns>
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static bool IsTakeoverBindingState(string? service, string? provider)
        => string.Equals(service, WinUsbService, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(provider, WinUsbSelfSignedDriverPackage.ProviderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks PnP to bind the freshly staged package without waiting for a device start. Only a present devnode
    /// that reports <see cref="WinUsbDriverNativeMethods.CmProbFailedInstall"/> is re-enumerated with the
    /// retry-installation flag, which lets PnP pick the staged package and start the device in place; the
    /// caller observes the outcome instead of blocking on the PnP install.
    /// <para>请求 PnP 绑定刚暂存的包且不等待设备启动。只有报告
    /// <see cref="WinUsbDriverNativeMethods.CmProbFailedInstall"/> 的在位设备节点才带重试安装标志重枚举，
    /// 让 PnP 就地选用暂存的包并启动设备；调用方观测结果而不阻塞在 PnP 安装上。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id.<para>设备实例 ID。</para></param>
    /// <returns><see langword="true"/> when the re-enumeration was queued; <see langword="false"/> when the
    /// devnode is gone, has no failed install or refused the request.
    /// <para>重枚举已排队时为 <see langword="true"/>；节点已消失、安装未失败或请求被拒绝时为
    /// <see langword="false"/>。</para></returns>
    private static bool RequestRebind(string instanceId)
    {
        if (WinUsbDriverNativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId,
                WinUsbDriverNativeMethods.CmLocateDevnodeNormal) != WinUsbDriverNativeMethods.CrSuccess)
        {
            return false;
        }

        if (WinUsbDriverNativeMethods.CmGetDevNodeStatus(out var status, out var problem, devInst, 0) !=
                WinUsbDriverNativeMethods.CrSuccess ||
            (status & WinUsbDriverNativeMethods.DnHasProblem) == 0 ||
            problem != WinUsbDriverNativeMethods.CmProbFailedInstall)
        {
            return false;
        }

        return WinUsbDriverNativeMethods.CmReenumerateDevNode(devInst,
            WinUsbDriverNativeMethods.CmReenumerateRetryInstallation |
            WinUsbDriverNativeMethods.CmReenumerateAsynchronous) == WinUsbDriverNativeMethods.CrSuccess;
    }

    /// <summary>
    /// Reads whether the device instance is still enumerated and, when it is, its devnode status bits and
    /// problem code, so the caller can tell a started device from a stalled, disabled or removing one.
    /// <para>读取该设备实例是否仍在枚举，以及在位时的设备节点状态位与问题码，使调用方能区分已启动、启动受阻、
    /// 已停用与正在移除的设备。</para>
    /// </summary>
    /// <param name="instanceId">The device instance ID.<para>设备实例 ID。</para></param>
    /// <returns><c>Present</c> is <see langword="true"/> when the node is enumerated; the status and
    /// problem values are zero when it is gone.<para>节点仍在枚举时 <c>Present</c> 为
    /// <see langword="true"/>；节点已消失时状态与问题码均为零。</para></returns>
    private static (bool Present, uint Status, uint Problem) ReadDevNodeStatus(string instanceId)
    {
        if (WinUsbDriverNativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId,
                WinUsbDriverNativeMethods.CmLocateDevnodeNormal) != WinUsbDriverNativeMethods.CrSuccess)
        {
            return (false, 0, 0);
        }

        return WinUsbDriverNativeMethods.CmGetDevNodeStatus(out var status, out var problem, devInst, 0) ==
            WinUsbDriverNativeMethods.CrSuccess
            ? (true, status, problem)
            : (false, 0, 0);
    }

    /// <summary>
    /// Tests whether a devnode status carries a pending or active removal, which makes PnP reject a driver
    /// install and would otherwise leave the caller waiting for a device that cannot come back.
    /// <para>判定设备节点状态是否带有挂起或进行中的移除；此时 PnP 会拒绝驱动安装，若继续等待就会为无法恢复的
    /// 设备空等。</para>
    /// </summary>
    /// <param name="status">The devnode status bits.<para>设备节点状态位。</para></param>
    /// <returns><see langword="true"/> when a removal is pending or active.
    /// <para>存在挂起或进行中的移除时为 <see langword="true"/>。</para></returns>
    private static bool IsRemovalPending(uint status)
        => (status & (WinUsbDriverNativeMethods.DnWillBeRemoved |
            WinUsbDriverNativeMethods.DnQueryRemovePending |
            WinUsbDriverNativeMethods.DnQueryRemoveActive)) != 0;

    /// <summary>
    /// Polls the observed binding for at most <see cref="BindingWaitTimeout"/> until it is the takeover
    /// package, so a slow enumeration still reports an accurate outcome.
    /// </summary>
    /// <param name="instanceId">The device instance ID.<para>设备实例 ID。</para></param>
    /// <returns><see langword="true"/> when the takeover binding was observed.
    /// <para>观测到接管绑定时为 <see langword="true"/>。</para></returns>
    private static bool WaitForTakeoverBinding(string instanceId)
    {
        var deadline = DateTime.UtcNow + BindingWaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = new WinUsbDriverInstaller().QueryState(instanceId);
            if (state is { DevicePresent: true, IsTakeoverBinding: true })
            {
                return true;
            }

            Thread.Sleep(BindingPollInterval);
        }

        return false;
    }


    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}