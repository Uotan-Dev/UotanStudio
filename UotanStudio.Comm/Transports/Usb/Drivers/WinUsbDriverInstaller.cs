using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;

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
    /// The kernel service name of the bound driver, for example <c>winusb</c>.
    /// <para>已绑定驱动的内核服务名,例如 <c>winusb</c>。</para>
    /// </summary>
    public string? DriverService { get; init; }

    /// <summary>
    /// The driver INF name backing the device, for example <c>oem42.inf</c>.
    /// <para>设备使用的驱动 INF 名,例如 <c>oem42.inf</c>。</para>
    /// </summary>
    public string? DriverInfPath { get; init; }

    /// <summary>
    /// The driver provider name recorded on the device.
    /// <para>设备上记录的驱动提供者名。</para>
    /// </summary>
    public string? DriverProvider { get; init; }

    /// <summary>
    /// Whether the bound driver package was installed by this application, which is the case when
    /// the service is <c>winusb</c> and the provider is <c>UotanStudio</c>.
    /// <para>当前驱动包是否由本应用安装:服务为 <c>winusb</c> 且提供者为 <c>UotanStudio</c> 时成立。</para>
    /// </summary>
    public required bool IsManagedByHost { get; init; }
}

/// <summary>
/// The parameters of one WinUSB driver install.
/// <para>一次 WinUSB 驱动安装的参数。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed record WinUsbDriverInstallRequest
{
    /// <summary>
    /// The device instance id, for example <c>USB\VID_18D1&amp;PID_4EE7&amp;MI_01\6&amp;2d3f...</c>.
    /// <para>设备实例 ID,例如 <c>USB\VID_18D1&amp;PID_4EE7&amp;MI_01\6&amp;2d3f...</c>。</para>
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// The hardware id the INF matches, for example <c>USB\VID_18D1&amp;PID_4EE7&amp;MI_01</c>.
    /// <para>INF 匹配用硬件 ID,例如 <c>USB\VID_18D1&amp;PID_4EE7&amp;MI_01</c>。</para>
    /// </summary>
    public required string HardwareId { get; init; }

    /// <summary>
    /// The device display name written into the INF strings; line breaks are removed and the text
    /// is truncated.
    /// <para>写入 INF 字符串段的设备显示名;换行会被移除并做截断。</para>
    /// </summary>
    public required string DeviceName { get; init; }

    /// <summary>
    /// Optional device interface class GUID override; when omitted
    /// <see cref="WinUsbDriverInstaller.DefaultInterfaceGuid"/> is used.
    /// <para>可选的设备接口类 GUID 覆盖;省略时使用
    /// <see cref="WinUsbDriverInstaller.DefaultInterfaceGuid"/>。</para>
    /// </summary>
    public Guid? DeviceInterfaceGuid { get; init; }
}

/// <summary>
/// The outcome of a WinUSB driver operation.
/// <para>WinUSB 驱动操作的结果状态。</para>
/// </summary>
public enum WinUsbDriverOperationStatus
{
    /// <summary>The operation completed. <para>操作成功完成。</para></summary>
    Succeeded,

    /// <summary>The operation needs an administrator token. <para>操作需要管理员令牌。</para></summary>
    ElevationRequired,

    /// <summary>The target device instance was not found. <para>目标设备实例未找到。</para></summary>
    DeviceNotFound,

    /// <summary>The operation failed; see the message. <para>操作失败;详见消息。</para></summary>
    Failed
}

/// <summary>
/// The result of one WinUSB driver operation: a status plus an optional human-readable message.
/// <para>一次 WinUSB 驱动操作的结果:状态加可选的可读消息。</para>
/// </summary>
/// <param name="Status">The operation outcome. <para>操作结果。</para></param>
/// <param name="Message">Optional detail or failure reason. <para>可选的细节或失败原因。</para></param>
[SupportedOSPlatform("windows")]
public sealed record WinUsbDriverOperationResult(WinUsbDriverOperationStatus Status, string? Message = null);

/// <summary>
/// Installs and removes a self-signed WinUSB driver package for matching USB devices. The install
/// path creates a self-signed code-signing certificate, publishes it to the LocalMachine Root and
/// TrustedPublisher stores, builds and signs a WinUSB catalog, and binds the package to the device
/// with <c>DiInstallDriver</c>; the uninstall path removes only packages this application
/// installed and lets the device fall back to the system default driver. Both mutating operations
/// require an administrator token, never throw, and report failures through
/// <see cref="WinUsbDriverOperationResult"/>.
/// <para>为匹配的 USB 设备安装与卸载自签名 WinUSB 驱动包。安装路径创建自签代码签名证书、发布到
/// LocalMachine 的 Root 与 TrustedPublisher 存储、构建并签名 WinUSB 目录,再用
/// <c>DiInstallDriver</c> 把驱动包绑定到设备;卸载路径只移除本应用安装的驱动包,让设备回退系统
/// 默认驱动。两个变更操作都需要管理员令牌,绝不抛出,失败经
/// <see cref="WinUsbDriverOperationResult"/> 返回。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinUsbDriverInstaller
{
    private const string InfFileName = "uotanstudio_winusb.inf";
    private const string HostProvider = "UotanStudio";
    private const string WinUsbService = "winusb";

    /// <summary>
    /// The default WinUSB device interface class GUID registered by generated INF files when the
    /// request does not override one. It is a fixed WinUSB device interface class GUID: interface
    /// enumeration and WinUSB session openers filter device interfaces by this class.
    /// <para>请求未提供覆盖时,生成的 INF 注册的默认 WinUSB 设备接口类 GUID。它是一个固定的
    /// WinUSB 设备接口类 GUID:接口枚举与 WinUSB 会话开启方按此类过滤设备接口。</para>
    /// </summary>
    public static readonly Guid DefaultInterfaceGuid = new("03adffdb-d865-4370-b81a-4bf800371253");

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
    /// <para>读取一个设备实例当前的驱动绑定,不做任何变更。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id. <para>设备实例 ID。</para></param>
    /// <returns>
    /// The observed state; a device that cannot be located reports
    /// <see cref="WinUsbDriverState.DevicePresent"/> as <see langword="false"/> with null fields.
    /// <para>观测到的状态;定位不到的设备返回 <see cref="WinUsbDriverState.DevicePresent"/> 为
    /// <see langword="false"/> 且其余字段为 null。</para>
    /// </returns>
    public WinUsbDriverState QueryState(string instanceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(instanceId) ||
            (WinUsbDriverNativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId,
                WinUsbDriverNativeMethods.CmLocateDevnodeNormal) != WinUsbDriverNativeMethods.CrSuccess))
        {
            return new WinUsbDriverState { DevicePresent = false, IsManagedByHost = false };
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
            IsManagedByHost = IsHostManagedBinding(service, provider)
        };
    }

    /// <summary>
    /// Installs a self-signed WinUSB driver package and binds it to the device matching the
    /// request hardware id. Requires an administrator token. On success the self-signed
    /// certificate has been added to the LocalMachine Root and TrustedPublisher stores and the
    /// WinUSB package is bound to the device; a reboot may be required, which the message reports.
    /// <para>安装自签名 WinUSB 驱动包并绑定到匹配请求硬件 ID 的设备。需要管理员令牌。成功时自签
    /// 证书已加入 LocalMachine 的 Root 与 TrustedPublisher 存储,WinUSB 驱动包已绑定到设备;可能
    /// 需要重启,消息中会说明。</para>
    /// </summary>
    /// <param name="request">The install parameters. <para>安装参数。</para></param>
    /// <returns>The operation result; never throws. <para>操作结果;绝不抛出。</para></returns>
    public WinUsbDriverOperationResult Install(WinUsbDriverInstallRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "WinUSB driver installation is only supported on Windows.");
        }

        if (!IsElevated)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.ElevationRequired,
                "Installing a WinUSB driver requires an administrator token; relaunch the application as administrator.");
        }

        if (string.IsNullOrWhiteSpace(request.InstanceId) || string.IsNullOrWhiteSpace(request.HardwareId))
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "The install request must carry a device instance id and a hardware id.");
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "UotanStudio.DriverInstall", Path.GetRandomFileName());
        string? verificationNoteOut = null;
        try
        {
            Directory.CreateDirectory(workDirectory);
            var infPath = Path.Combine(workDirectory, InfFileName);
            var interfaceGuid = request.DeviceInterfaceGuid ?? DefaultInterfaceGuid;
            var infText = WinUsbDriverInf.Build(request.HardwareId.Trim(),
                string.IsNullOrWhiteSpace(request.DeviceName) ? request.HardwareId : request.DeviceName,
                interfaceGuid, InfFileName, DateTime.UtcNow);
            File.WriteAllText(infPath, infText, Encoding.Unicode);

            using var certificate = WinUsbDriverCertStore.TryLoadOrCreate(out var certError);
            if (certificate is null)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed, certError);
            }

            if (!WinUsbDriverCertStore.TryInstallToMachineStores(certificate, out var storeError))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed, storeError);
            }

            if (!WinUsbDriverCatalog.TryCreateAndSign(infPath, certificate, out var catalogPath, out var catalogError,
                    out var verificationNote))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed, catalogError);
            }

            File.Copy(catalogPath, Path.ChangeExtension(infPath, ".cat"), overwrite: true);
            verificationNoteOut = verificationNote;

            if (!WinUsbDriverNativeMethods.DiInstallDriverW(IntPtr.Zero, infPath,
                    WinUsbDriverNativeMethods.DiirflagForceInf, out var needReboot))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    WinUsbDriverNativeMethods.DescribeLastWin32Error("DiInstallDriver failed"));
            }

            var message =
                $"Installed the self-signed WinUSB driver package for '{request.HardwareId}'; the device interface class is {interfaceGuid:D}.";
            if (needReboot)
            {
                message += " A reboot is required before the binding takes effect.";
            }

            if (verificationNoteOut is not null)
            {
                message += $" {verificationNoteOut}";
            }

            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The driver package workspace could not be prepared: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    /// Uninstalls the device and the driver package this application installed, letting the device
    /// fall back to the system default driver after re-enumeration. Requires an administrator
    /// token, and only removes packages whose driver provider is <c>UotanStudio</c> and whose
    /// service is <c>winusb</c>; vendor drivers are never removed.
    /// <para>卸载设备与本应用安装的驱动包,设备重新枚举后回退系统默认驱动。需要管理员令牌,且只
    /// 移除驱动提供者为 <c>UotanStudio</c>、服务为 <c>winusb</c> 的驱动包;绝不移除厂商驱动。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id. <para>设备实例 ID。</para></param>
    /// <returns>The operation result; never throws. <para>操作结果;绝不抛出。</para></returns>
    public WinUsbDriverOperationResult Uninstall(string instanceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "WinUSB driver removal is only supported on Windows.");
        }

        if (!IsElevated)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.ElevationRequired,
                "Removing a WinUSB driver requires an administrator token; relaunch the application as administrator.");
        }

        if (string.IsNullOrWhiteSpace(instanceId) ||
            (WinUsbDriverNativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId,
                WinUsbDriverNativeMethods.CmLocateDevnodeNormal) != WinUsbDriverNativeMethods.CrSuccess))
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.DeviceNotFound,
                $"No device instance matches '{instanceId}'.");
        }

        var provider = WinUsbDriverNativeMethods.ReadDevNodeString(devInst, WinUsbDriverNativeMethods.DriverProviderKey);
        var service = WinUsbDriverNativeMethods.ReadDevNodeString(devInst, WinUsbDriverNativeMethods.DriverServiceKey);
        if (!IsHostManagedBinding(service, provider))
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "The current driver was not installed by this application; uninstall is refused to protect vendor drivers.");
        }

        var deviceInfoSet = WinUsbDriverNativeMethods.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (deviceInfoSet == WinUsbDriverNativeMethods.InvalidHandleValue)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                WinUsbDriverNativeMethods.DescribeLastWin32Error("SetupDiCreateDeviceInfoList failed"));
        }

        try
        {
            var deviceInfoData = new WinUsbDriverNativeMethods.SpDevInfoData
            {
                CbSize = (uint)Marshal.SizeOf<WinUsbDriverNativeMethods.SpDevInfoData>()
            };
            if (!WinUsbDriverNativeMethods.SetupDiOpenDeviceInfoW(deviceInfoSet, instanceId, IntPtr.Zero, 0,
                    ref deviceInfoData))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    WinUsbDriverNativeMethods.DescribeLastWin32Error("SetupDiOpenDeviceInfo failed"));
            }

            if (!WinUsbDriverNativeMethods.DiUninstallDevice(IntPtr.Zero, deviceInfoSet, ref deviceInfoData, 0,
                    out var needReboot))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    WinUsbDriverNativeMethods.DescribeLastWin32Error("DiUninstallDevice failed"));
            }

            var message =
                "Removed the device and the WinUSB driver package installed by this application; the device returns to the system default driver when it is re-enumerated.";
            if (needReboot)
            {
                message += " A reboot is required to complete the removal.";
            }

            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded, message);
        }
        finally
        {
            _ = WinUsbDriverNativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static bool IsHostManagedBinding(string? service, string? provider)
        => string.Equals(service, WinUsbService, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(provider, HostProvider, StringComparison.OrdinalIgnoreCase);

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