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
    /// it is published. Binding proceeds asynchronously: the call waits at most
    /// <see cref="BindingWaitTimeout"/> for the observed binding to become the takeover package and reports
    /// success once the package is staged even when the device has not come back yet.
    /// <para>通过暂存新生成的自签名 WinUSB 驱动包并请求 PnP 绑定来接管设备。需要管理员令牌；该包把系统内置
    /// <c>WinUSB</c> 服务绑定到 <paramref name="request"/> 中的单个硬件 ID，使设备可由 MediaTek 预加载器
    /// 与下载模式访问所用的 WinUSB 路径打开。证书会被加入本机 <c>Root</c> 与 <c>TrustedPublisher</c>
    /// 存储，并在发布前销毁私钥。绑定异步完成：调用最多等待 <see cref="BindingWaitTimeout"/> 观测绑定是否
    /// 变为接管包，只要包已暂存即报告成功，即使设备尚未重新枚举。</para>
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
            Directory.CreateDirectory(workDirectory);
            var package = WinUsbSelfSignedDriverPackage.Create(workDirectory, request.HardwareId,
                request.InstanceId);
            if (!WinUsbDriverNativeMethods.SetupCopyOEMInfW(package.InfPath, IntPtr.Zero,
                    WinUsbDriverNativeMethods.SpOemSourcePath, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    WinUsbDriverNativeMethods.DescribeLastWin32Error("SetupCopyOEMInf failed"));
            }

            RequestRebind(request.InstanceId);
            var bound = WaitForTakeoverBinding(request.InstanceId);
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                bound
                    ? $"Staged and bound the self-signed WinUSB package for '{request.HardwareId}'."
                    : $"Staged the self-signed WinUSB package for '{request.HardwareId}'; the device binds on its next enumeration.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException or InvalidDataException)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The WinUSB takeover package could not be prepared: {ex.Message}");
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

            _ = WinUsbDriverNativeMethods.CmReenumerateDevNode(devInst,
                WinUsbDriverNativeMethods.CmReenumerateRetryInstallation);
        }

        var certificateRemoved = WinUsbSelfSignedDriverPackage.RemoveCertificateFromStores();
        return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
            certificateRemoved
                ? $"Removed the takeover package '{infName}' and its certificate; the device returns to a system driver."
                : $"Removed the takeover package '{infName}' but the certificate stores could not be cleaned.");
    }

    private static bool IsTakeoverBindingState(string? service, string? provider)
        => string.Equals(service, WinUsbService, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(provider, WinUsbSelfSignedDriverPackage.ProviderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks PnP to install the freshly staged package without waiting for a device start: removing a devnode
    /// whose package is staged has nothing to veto, and re-enumerating the parent brings the device back so
    /// the PnP service performs the install. Devices that stall their start therefore never block this call.
    /// </summary>
    /// <param name="instanceId">The device instance id.<para>设备实例 ID。</para></param>
    private static void RequestRebind(string instanceId)
    {
        if (WinUsbDriverNativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId,
                WinUsbDriverNativeMethods.CmLocateDevnodeNormal) != WinUsbDriverNativeMethods.CrSuccess)
        {
            return;
        }

        var removed = WinUsbDriverNativeMethods.CmQueryAndRemoveSubTree(devInst, IntPtr.Zero, IntPtr.Zero, 0, 0)
            == WinUsbDriverNativeMethods.CrSuccess;
        if (removed && WinUsbDriverNativeMethods.CmGetParent(out var parentDevInst, devInst, 0)
            == WinUsbDriverNativeMethods.CrSuccess)
        {
            _ = WinUsbDriverNativeMethods.CmReenumerateDevNode(parentDevInst,
                WinUsbDriverNativeMethods.CmReenumerateRetryInstallation);
        }
        else if (!removed)
        {
            _ = WinUsbDriverNativeMethods.CmReenumerateDevNode(devInst,
                WinUsbDriverNativeMethods.CmReenumerateRetryInstallation);
        }
    }

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