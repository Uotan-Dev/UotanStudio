using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using UotanStudio.Comm.Transports.Usb.Core;

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

    /// <summary>
    /// The package was removed but the running driver stack still needs a restart or a replug before the
    /// device stops using the takeover driver.
    /// <para>驱动包已移除，但运行中的驱动栈仍需重启计算机或重新插拔设备，设备才会停止使用接管驱动。</para>
    /// </summary>
    RestartRequired,

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

    /// <summary>The upper bound a takeover waits for a WinUSB-openable interface after the binding changes.
    /// <para>绑定变化后，接管等待出现可由 WinUSB 打开的接口的上界。</para></summary>
    private static readonly TimeSpan InterfaceWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The upper bound a release waits for the device to stop reporting the takeover binding.
    /// <para>解除接管时等待设备不再上报接管绑定的上界。</para></summary>
    private static readonly TimeSpan ReleaseBindingTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The in-box USB device interface class, a last candidate when a WinUSB stack publishes no
    /// node-level DeviceInterfaceGUIDs value.
    /// <para>系统内置 USB 设备接口类；当 WinUSB 栈没有节点级 DeviceInterfaceGUIDs 值时作为最后候选。</para></summary>
    private static readonly Guid UsbDeviceInterfaceGuid = new("a5dcbf10-6530-11d2-901f-00c04fb951ed");

    /// <summary>The cached invalid file-name characters used to reject devnode-reported INF names.
    /// <para>缓存的非法文件名字符集，用于拒绝设备节点上报的 INF 名。</para></summary>
    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>The wait shared by the takeover and vendor package installs for the PnP installation queue
    /// to drain before they queue their own work.
    /// <para>接管与厂商包安装在排入自己的工作前，共享的 PnP 安装队列排空等待上限。</para></summary>
    internal static readonly TimeSpan PendingInstallWaitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The cap on how many of this application's staged packages one release may remove.
    /// <para>一次解除最多可移除的本应用暂存包数量上限。</para></summary>
    private const int MaximumOwnStagedRemovals = 64;

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
    /// takeover package. The result then requires a WinUSB-openable device interface on that instance,
    /// because a registry-level binding alone does not prove the running driver stack switched; a missing
    /// interface triggers one asynchronous re-enumeration and is reported as a replug requirement. Before
    /// anything is staged, the PnP installation queue must drain within
    /// <see cref="PendingInstallWaitTimeout"/>: queueing onto a busy queue blocks every other device
    /// install, so a busy queue is refused. A devnode that is pending removal is rejected too.
    /// <para>通过暂存新生成的自签名 WinUSB 驱动包并请求 PnP 绑定来接管设备。需要管理员令牌；该包把系统内置
    /// <c>WinUSB</c> 服务绑定到 <paramref name="request"/> 中的单个硬件 ID，使设备可由 MediaTek 预加载器
    /// 与下载模式访问所用的 WinUSB 路径打开。证书会被加入本机 <c>Root</c> 与 <c>TrustedPublisher</c>
    /// 存储，并在发布前销毁私钥。此前安装失败的设备节点经异步重试安装完成绑定，其余在位节点必须经 PnP 在本次
    /// 调用内完成的强制安装，两条路径都最多等待 <see cref="BindingWaitTimeout"/> 观测绑定是否变为接管包。
    /// 绑定后还要求该实例存在可由 WinUSB 打开的设备接口：仅注册表层面的绑定不能证明运行中的驱动栈已切换；
    /// 接口缺失时触发一次异步重枚举，仍缺失则如实上报需要拔插或重启，而不报成功。
    /// 在暂存任何内容之前，PnP 安装队列必须在 <see cref="PendingInstallWaitTimeout"/> 内排空：向繁忙的
    /// 队列排队会阻塞所有其他设备安装，因此队列繁忙时直接拒绝；处于移除中的设备节点同样会被拒绝。</para>
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

            if (!WinUsbDriverNativeMethods.WaitForNoPendingInstallation(PendingInstallWaitTimeout))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    "Another device installation is pending; the takeover does not queue onto a busy PnP queue. Finish that installation or replug the device and retry.");
            }

            var current = QueryState(request.InstanceId);
            if (current is { DevicePresent: true, IsTakeoverBinding: true } && problem == 0 &&
                (WaitForWinUsbInterface(request.InstanceId, InterfaceWaitTimeout) ||
                 (RequestDriverRestart(request.InstanceId) &&
                  WaitForWinUsbInterface(request.InstanceId, InterfaceWaitTimeout))))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                    $"'{request.HardwareId}' already runs the self-signed WinUSB package and its WinUSB interface is openable.");
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

            var rebootRequired = 0;
            var bound = current.IsTakeoverBinding;
            if (!bound && ReadDevNodeStatus(request.InstanceId).Present)
            {
                bound = WinUsbDriverNativeMethods.UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero,
                        request.HardwareId, package.InfPath,
                        WinUsbDriverNativeMethods.InstallFlagForce |
                        WinUsbDriverNativeMethods.InstallFlagNonInteractive,
                        out rebootRequired) &&
                    WaitForTakeoverBinding(request.InstanceId);
            }

            if (!bound && RequestRebind(request.InstanceId))
            {
                bound = WaitForTakeoverBinding(request.InstanceId);
            }

            var (stillPresent, _, stillProblem) = ReadDevNodeStatus(request.InstanceId);
            if (!stillPresent)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                    $"Staged the self-signed WinUSB package for '{request.HardwareId}'; the device is no longer present and binds on its next enumeration.");
            }

            var interfaceReady = WaitForWinUsbInterface(request.InstanceId, InterfaceWaitTimeout);
            if (!interfaceReady && RequestDriverRestart(request.InstanceId))
            {
                interfaceReady = WaitForWinUsbInterface(request.InstanceId, InterfaceWaitTimeout);
            }

            if (rebootRequired != 0)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.RestartRequired,
                    $"Bound the self-signed WinUSB package for '{request.HardwareId}', but Windows requires a reboot before the driver can start; restart the computer or replug the device.");
            }

            if (bound && stillProblem == 0 && interfaceReady)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                    $"Staged and bound the self-signed WinUSB package for '{request.HardwareId}'; its WinUSB interface is openable.");
            }

            var pendingNote = WinUsbDriverNativeMethods.WaitForNoPendingInstallation(TimeSpan.Zero)
                ? string.Empty
                : " The PnP installation queue still reports a pending install; unplug the device if Device Manager or other devices stop responding.";
            if (bound && stillProblem == 0)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    $"Bound the self-signed WinUSB package for '{request.HardwareId}', but no WinUSB device interface is openable: the running driver stack is still the vendor driver. Replug the device (or reboot) so WinUSB starts, then retry.{pendingNote}");
            }

            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                bound
                    ? $"Bound the self-signed WinUSB package for '{request.HardwareId}' but the device did not start (problem 0x{stillProblem:X}).{pendingNote}"
                    : $"Staged the self-signed WinUSB package for '{request.HardwareId}' but the device did not bind (problem 0x{stillProblem:X}).{pendingNote}");
        }
        catch (Exception ex)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The WinUSB takeover package could not be prepared: {Describe(ex)}");
        }
        finally
        {
            UsbCommunicationLayer.ResetUsbEnumerationInventory();
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    /// Ends the takeover of a device: uninstalls the self-signed package from the devices using it so PnP
    /// installs another matching driver in place, removes the takeover certificate, and reads the binding
    /// back before reporting success. Requires an administrator token and only acts on a binding this
    /// application installed.
    /// <para>结束设备接管：从使用该自签名包的设备上卸载该包，让 PnP 就地改装其它匹配驱动，移除接管证书，
    /// 并在报告成功前读回绑定。需要管理员令牌，且只作用于本应用安装的绑定。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id.<para>设备实例 ID。</para></param>
    /// <returns>
    /// The operation result; never throws. <see cref="WinUsbDriverOperationStatus.Succeeded"/> requires an
    /// observed binding that is no longer the takeover driver, and
    /// <see cref="WinUsbDriverOperationStatus.RestartRequired"/> reports a device that still needs a restart
    /// or replug.
    /// <para>操作结果；绝不抛出。只有观测到绑定已不再是接管驱动才返回
    /// <see cref="WinUsbDriverOperationStatus.Succeeded"/>；仍需要重启或重新插拔的设备返回
    /// <see cref="WinUsbDriverOperationStatus.RestartRequired"/>。</para>
    /// </returns>
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

        if (!located)
        {
            var certificateRemoved = WinUsbSelfSignedDriverPackage.RemoveCertificateFromStores();
            var removed = RemoveOwnStagedPackages();
            if (!certificateRemoved)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.DeviceNotFound,
                    $"No device instance matches '{instanceId}' and the takeover certificate could not be removed.");
            }

            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                $"No device instance matches '{instanceId}'; removed {removed.Count} staged takeover package(s)" +
                (removed.Count > 0 ? $" ({string.Join(", ", removed)})" : string.Empty) +
                " and the takeover certificate. The device binds no takeover driver on its next enumeration.");
        }

        if (!IsTakeoverBindingState(service, provider))
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "The current driver was not installed by this application's takeover; removal is refused to protect other drivers.");
        }

        var parent = 0u;
        var hasParent = WinUsbDriverNativeMethods.CmGetParent(out parent, devInst, 0) ==
            WinUsbDriverNativeMethods.CrSuccess;
        var stagedInfPath = ResolveOwnStagedInfPath(infName);
        var rebootRequired = 0;
        if (stagedInfPath is not null)
        {
            if (!WinUsbDriverNativeMethods.DiUninstallDriverW(IntPtr.Zero, stagedInfPath, 0,
                    out rebootRequired))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    WinUsbDriverNativeMethods.DescribeLastWin32Error("DiUninstallDriver failed"));
            }
        }
        else if (!RemoveDeviceNode(instanceId, out rebootRequired, out var removalError))
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed, removalError);
        }

        var certificateCleaned = WinUsbSelfSignedDriverPackage.RemoveCertificateFromStores();
        _ = WinUsbDriverNativeMethods.WaitForNoPendingInstallation(PendingInstallWaitTimeout);
        _ = WinUsbDriverNativeMethods.CmReenumerateDevNode(devInst,
            WinUsbDriverNativeMethods.CmReenumerateRetryInstallation |
            WinUsbDriverNativeMethods.CmReenumerateAsynchronous);
        if (hasParent)
        {
            _ = WinUsbDriverNativeMethods.CmReenumerateDevNode(parent,
                WinUsbDriverNativeMethods.CmReenumerateRetryInstallation |
                WinUsbDriverNativeMethods.CmReenumerateAsynchronous);
        }

        var status = ClassifyReleaseOutcome(rebootRequired != 0,
            WaitForBindingRelease(instanceId, ReleaseBindingTimeout));
        var action = stagedInfPath is not null
            ? $"Uninstalled the self-signed WinUSB package '{infName}' from the device and the driver store."
            : "Removed the devnode that still carried the takeover binding; its staged package was already gone.";
        var certificateNote = certificateCleaned
            ? " The takeover certificate was removed."
            : " The takeover certificate stores could not be cleaned.";
        return status == WinUsbDriverOperationStatus.Succeeded
            ? new WinUsbDriverOperationResult(status,
                $"{action} '{instanceId}' no longer reports the takeover binding.{certificateNote}")
            : new WinUsbDriverOperationResult(status,
                $"{action} '{instanceId}' still needs a restart or a replug before it stops using the takeover driver.{certificateNote}");
    }

    /// <summary>
    /// Resolves the staged INF of this application's takeover package, refusing path-carrying names and
    /// files that another provider now owns. Windows reuses <c>oemN.inf</c> slots, so a name alone does not
    /// prove ownership.
    /// <para>解析本应用接管包的暂存 INF，拒绝带路径的名字以及现属其他提供者的文件。Windows 会复用
    /// <c>oemN.inf</c> 编号，仅凭文件名不能证明归属。</para>
    /// </summary>
    /// <param name="infName">The INF name reported by the devnode. <para>设备节点上报的 INF 名。</para></param>
    /// <returns>The absolute INF path, or <see langword="null"/> when no own staged package exists.
    /// <para>INF 的绝对路径；不存在本应用暂存包时为 <see langword="null"/>。</para></returns>
    private static string? ResolveOwnStagedInfPath(string? infName)
    {
        if (!IsOwnStagedInfName(infName))
        {
            return null;
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "inf", infName!);
        if (!File.Exists(path) || !DriverPackageInfReader.TryReadFile(path, out var metadata) ||
            metadata is null ||
            !string.Equals(metadata.Provider, WinUsbSelfSignedDriverPackage.ProviderName,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return path;
    }

    /// <summary>
    /// Checks that one devnode-reported INF name is a bare <c>oem*.inf</c> file name, so an unexpected
    /// value can never turn into a path outside the Windows INF directory.
    /// <para>校验设备节点上报的 INF 名是纯 <c>oem*.inf</c> 文件名，避免异常值拼出 Windows INF 目录之外的路径。</para>
    /// </summary>
    /// <param name="infName">The INF name to check. <para>待校验的 INF 名。</para></param>
    /// <returns><see langword="true"/> when the name is a bare staged INF file name.
    /// <para>是纯暂存 INF 文件名时为 <see langword="true"/>。</para></returns>
    internal static bool IsOwnStagedInfName(string? infName)
        => infName is { Length: > 0 } &&
            string.Equals(Path.GetFileName(infName), infName, StringComparison.Ordinal) &&
            !infName.AsSpan().ContainsAny(InvalidFileNameChars) &&
            infName.StartsWith("oem", StringComparison.OrdinalIgnoreCase) &&
            infName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes one devnode whose takeover package has already left the driver store, so PnP installs the
    /// best remaining driver when the device is enumerated again. No INF of another provider is touched.
    /// <para>移除接管包已从驱动仓库消失但仍带接管绑定的设备节点，使设备重新枚举时由 PnP 安装剩余的最佳驱动。
    /// 绝不触碰其他提供者的 INF。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id. <para>设备实例 ID。</para></param>
    /// <param name="rebootRequired">Non-zero when Windows needs a restart to finish the removal.
    /// <para>非零表示 Windows 需要重启才能完成移除。</para></param>
    /// <param name="error">The native failure description. <para>原生失败描述。</para></param>
    /// <returns><see langword="true"/> when the devnode was removed.
    /// <para>设备节点已移除时为 <see langword="true"/>。</para></returns>
    private static bool RemoveDeviceNode(string instanceId, out int rebootRequired, out string error)
    {
        rebootRequired = 0;
        error = string.Empty;
        var deviceInfoSet = WinUsbDriverNativeMethods.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == WinUsbDriverNativeMethods.InvalidHandleValue)
        {
            error = WinUsbDriverNativeMethods.DescribeLastWin32Error("SetupDiCreateDeviceInfoList failed");
            return false;
        }

        try
        {
            var data = new WinUsbDriverNativeMethods.SpDevinfoData
            {
                Size = (uint)Marshal.SizeOf<WinUsbDriverNativeMethods.SpDevinfoData>()
            };
            if (!WinUsbDriverNativeMethods.SetupDiOpenDeviceInfoW(deviceInfoSet, instanceId, IntPtr.Zero, 0,
                    ref data))
            {
                error = WinUsbDriverNativeMethods.DescribeLastWin32Error("SetupDiOpenDeviceInfo failed");
                return false;
            }

            if (WinUsbDriverNativeMethods.DiUninstallDevice(IntPtr.Zero, deviceInfoSet, ref data, 0,
                    out rebootRequired))
            {
                return true;
            }

            error = WinUsbDriverNativeMethods.DescribeLastWin32Error("DiUninstallDevice failed");
            return false;
        }
        finally
        {
            _ = WinUsbDriverNativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    /// <summary>
    /// Classifies a completed package removal: a pending reboot or a devnode that still reports the takeover
    /// binding is a restart requirement, never a success.
    /// <para>判定已完成的驱动包移除：需要重启或设备节点仍上报接管绑定时是需要重启，绝不报成功。</para>
    /// </summary>
    /// <param name="rebootRequired">Whether Windows reported a pending restart.
    /// <para>Windows 是否报告需要重启。</para></param>
    /// <param name="bindingReleased">Whether the devnode was observed without the takeover binding.
    /// <para>是否观测到设备节点不再带接管绑定。</para></param>
    /// <returns><see cref="WinUsbDriverOperationStatus.Succeeded"/> only for a verified release.
    /// <para>只有验证通过的解除才返回 <see cref="WinUsbDriverOperationStatus.Succeeded"/>。</para></returns>
    internal static WinUsbDriverOperationStatus ClassifyReleaseOutcome(bool rebootRequired,
        bool bindingReleased)
        => !bindingReleased || rebootRequired
            ? WinUsbDriverOperationStatus.RestartRequired
            : WinUsbDriverOperationStatus.Succeeded;

    /// <summary>
    /// Polls a bounded time until the device no longer reports the takeover binding. A devnode that can no
    /// longer be located was removed from the tree, so it cannot still run the takeover driver either.
    /// <para>有界轮询，直到设备不再上报接管绑定。定位不到的设备节点已从设备树移除，同样不可能仍在运行接管
    /// 驱动。</para>
    /// </summary>
    /// <param name="query">Reads the current binding. <para>读取当前绑定的回调。</para></param>
    /// <param name="timeout">The wait bound. <para>等待上限。</para></param>
    /// <param name="pollInterval">The interval between two observations.
    /// <para>两次观测之间的间隔。</para></param>
    /// <returns><see langword="true"/> when the binding was observed to leave the takeover driver.
    /// <para>观测到绑定离开接管驱动时为 <see langword="true"/>。</para></returns>
    internal static bool WaitForBindingRelease(Func<WinUsbDriverState> query, TimeSpan timeout,
        TimeSpan pollInterval)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (query() is { IsTakeoverBinding: false })
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(pollInterval);
        }
    }

    private static bool WaitForBindingRelease(string instanceId, TimeSpan timeout)
        => WaitForBindingRelease(() => new WinUsbDriverInstaller().QueryState(instanceId), timeout,
            BindingPollInterval);

    /// <summary>
    /// Removes every staged OEM INF whose provider matches the takeover package provider, so an absent
    /// device still loses the packages this application staged. Packages of other providers are untouched.
    /// <para>移除提供者与接管包相符的所有已暂存 OEM INF，使不在位的设备也失去本应用暂存的包。其他提供者的包
    /// 一律不动。</para>
    /// </summary>
    /// <returns>The removed INF file names. <para>被移除的 INF 文件名。</para></returns>
    private static List<string> RemoveOwnStagedPackages()
    {
        var removed = new List<string>();
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "inf");
        if (!Directory.Exists(store))
        {
            return removed;
        }

        foreach (var path in Directory.EnumerateFiles(store, "oem*.inf"))
        {
            if (removed.Count >= MaximumOwnStagedRemovals)
            {
                break;
            }

            if (!DriverPackageInfReader.TryReadFile(path, out var metadata) || metadata is null ||
                !string.Equals(metadata.Provider, WinUsbSelfSignedDriverPackage.ProviderName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileName(path);
            if (WinUsbDriverNativeMethods.SetupUninstallOEMInfW(name,
                    WinUsbDriverNativeMethods.SuoiForceDelete, IntPtr.Zero) ||
                Marshal.GetLastWin32Error() == WinUsbDriverNativeMethods.ErrorFileNotFound)
            {
                removed.Add(name);
            }
        }

        return removed;
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
    /// caller observes the outcome instead of blocking on the PnP install. A leaf-level re-enumeration is a
    /// no-op for a driverless USB devnode, so the parent hub is re-enumerated as well, which re-detects the
    /// port and re-runs the installation without ever removing the devnode.
    /// <para>请求 PnP 绑定刚暂存的包且不等待设备启动。只有报告
    /// <see cref="WinUsbDriverNativeMethods.CmProbFailedInstall"/> 的在位设备节点才带重试安装标志重枚举，
    /// 让 PnP 就地选用暂存的包并启动设备；调用方观测结果而不阻塞在 PnP 安装上。leaf 级重枚举对无驱动的
    /// USB 设备节点是 no-op，因此父集线器也会被重枚举：端口被重新检测、安装被重跑，而设备节点本身绝不被移除。</para>
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

        if (WinUsbDriverNativeMethods.CmReenumerateDevNode(devInst,
                WinUsbDriverNativeMethods.CmReenumerateRetryInstallation |
                WinUsbDriverNativeMethods.CmReenumerateAsynchronous) != WinUsbDriverNativeMethods.CrSuccess)
        {
            return false;
        }

        if (WinUsbDriverNativeMethods.CmGetParent(out var parent, devInst, 0) ==
            WinUsbDriverNativeMethods.CrSuccess)
        {
            _ = WinUsbDriverNativeMethods.CmReenumerateDevNode(parent,
                WinUsbDriverNativeMethods.CmReenumerateRetryInstallation |
                WinUsbDriverNativeMethods.CmReenumerateAsynchronous);
        }

        return true;
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


    /// <summary>
    /// Parses the device node's <c>DeviceInterfaceGUIDs</c> registry value, accepting both REG_MULTI_SZ
    /// and a single string and ignoring malformed entries.
    /// <para>解析设备节点的 <c>DeviceInterfaceGUIDs</c> 注册表值，兼容 REG_MULTI_SZ 与单个字符串，忽略非法项。</para>
    /// </summary>
    /// <param name="value">The raw registry value. <para>注册表原始值。</para></param>
    /// <returns>The distinct GUIDs carried by the value. <para>该值携带的去重 GUID。</para></returns>
    internal static IReadOnlyList<Guid> ParseInterfaceGuids(object? value)
    {
        IEnumerable<string> entries = value switch
        {
            string[] list => list,
            string single => [single],
            _ => []
        };

        var result = new List<Guid>();
        foreach (var text in entries)
        {
            if (Guid.TryParse(text.Trim().Trim('{', '}'), out var guid) && !result.Contains(guid))
            {
                result.Add(guid);
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the interface class GUIDs the node registered for its driver, plus the in-box USB device
    /// interface class as a last candidate for stacks that publish no node-level value.
    /// <para>读取节点为驱动注册的接口类 GUID，并补上系统内置 USB 设备接口类，覆盖不写节点级值的驱动栈。</para>
    /// </summary>
    private static IReadOnlyList<Guid> ReadWinUsbInterfaceCandidates(string instanceId)
    {
        var candidates = new List<Guid>();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters");
            candidates.AddRange(ParseInterfaceGuids(key?.GetValue("DeviceInterfaceGUIDs")));
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        if (!candidates.Contains(UsbDeviceInterfaceGuid))
        {
            candidates.Add(UsbDeviceInterfaceGuid);
        }

        return candidates;
    }

    /// <summary>
    /// Polls until one device interface of this exact instance can be opened through
    /// <c>WinUsb_Initialize</c>; a registry binding alone does not prove the running stack serves WinUSB.
    /// <para>有界轮询，直到该精确实例的某个设备接口可由 <c>WinUsb_Initialize</c> 打开；仅注册表绑定
    /// 不能证明运行中的驱动栈已经由 WinUSB 提供服务。</para>
    /// </summary>
    private static bool WaitForWinUsbInterface(string instanceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            foreach (var guid in ReadWinUsbInterfaceCandidates(instanceId))
            {
                if (UsbPnpNative.FindDeviceInterfacePath(guid, instanceId) is { } path &&
                    CanOpenWinUsbInterface(path))
                {
                    return true;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(BindingPollInterval);
        }
    }

    /// <summary>Opens one interface path through WinUSB and releases both handles immediately.
    /// <para>经 WinUSB 打开一条接口路径并立即释放两个句柄。</para></summary>
    private static bool CanOpenWinUsbInterface(string path)
    {
        var file = Win32API.SimpleCreateHandle(path, true);
        if (file == Win32API.INVALID_HANDLE_VALUE)
        {
            return false;
        }

        try
        {
            if (!WinUSBAPI.WinUsb_Initialize(file, out var iface))
            {
                return false;
            }

            using var opened = new SafeWinUsbHandle(iface);
            return true;
        }
        finally
        {
            Win32API.CloseHandle(file);
        }
    }

    /// <summary>
    /// Queues an asynchronous re-enumeration of one devnode and its parent hub so PnP can start the
    /// bound stack without a synchronous wait that a stalled node could hold open indefinitely.
    /// <para>对设备节点及其父集线器排队异步重枚举，使 PnP 启动已绑定驱动栈，同时避免可能被卡住节点
    /// 无限期占住的同步等待。</para>
    /// </summary>
    private static bool RequestDriverRestart(string instanceId)
    {
        if (WinUsbDriverNativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId,
                WinUsbDriverNativeMethods.CmLocateDevnodeNormal) != WinUsbDriverNativeMethods.CrSuccess)
        {
            return false;
        }

        var queued = WinUsbDriverNativeMethods.CmReenumerateDevNode(devInst,
            WinUsbDriverNativeMethods.CmReenumerateRetryInstallation |
            WinUsbDriverNativeMethods.CmReenumerateAsynchronous) == WinUsbDriverNativeMethods.CrSuccess;
        if (WinUsbDriverNativeMethods.CmGetParent(out var parent, devInst, 0) ==
            WinUsbDriverNativeMethods.CrSuccess)
        {
            _ = WinUsbDriverNativeMethods.CmReenumerateDevNode(parent,
                WinUsbDriverNativeMethods.CmReenumerateRetryInstallation |
                WinUsbDriverNativeMethods.CmReenumerateAsynchronous);
        }

        return queued;
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