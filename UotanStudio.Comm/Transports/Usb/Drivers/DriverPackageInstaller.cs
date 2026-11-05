using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// Installs and removes the vendor Windows driver packages listed by <see cref="DriverPackageCatalog"/>.
/// The install path materializes the embedded package into a temporary directory, stages every INF with
/// <c>SetupCopyOEMInf</c>, and asks PnP to retry installation on present USB device nodes that carry problem
/// code 28 and whose hardware ids one of the staged INFs claims, without waiting for the devices to start;
/// a busy PnP installation queue is never added to, so the install refuses while another installation is
/// pending. The uninstall path removes only the staged OEM INFs whose provider and catalog file match the
/// package INFs and refuses when that ownership cannot be established. Both mutating operations require an
/// administrator token, never throw, and report failures through <see cref="WinUsbDriverOperationResult"/>.
/// <para>安装与卸载 <see cref="DriverPackageCatalog"/> 列出的厂商 Windows 驱动包。安装路径把内嵌包物化到
/// 临时目录、用 <c>SetupCopyOEMInf</c> 暂存每个 INF，并请求 PnP 对在位、问题码为 28 且硬件 ID 与某个已暂存
/// INF 声明相符的 USB 设备节点重试安装，而不等待设备启动；绝不向繁忙的 PnP 安装队列追加，因此仍有安装挂起时
/// 直接拒绝。卸载路径只移除提供者与目录文件都与包内 INF 相符的已暂存 OEM INF，无法确认归属时拒绝执行。两个
/// 变更操作都需要管理员令牌，绝不抛出，失败经 <see cref="WinUsbDriverOperationResult"/> 返回。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriverPackageInstaller
{
    /// <summary>The temporary directory family every installation is materialized under.
    /// <para>每次安装物化到其下的临时目录族名。</para></summary>
    private const string WorkDirectoryName = "UotanStudio.DriverPackage";

    /// <summary>The enumerator filter that restricts PnP lookups to USB devices.
    /// <para>把 PnP 查询限定到 USB 设备的枚举器过滤串。</para></summary>
    private const string UsbEnumeratorFilter = "USB";

    private const int MaximumDeviceIdListBytes = 512 * 1024;
    private const int MaximumDeviceCount = 512;
    private const int MaximumReenumeratedDevices = 128;
    private const int MaximumStagedInfFiles = 4096;

    /// <summary>
    /// Gets whether this platform supports driver package installation; always <see langword="true"/> on
    /// Windows.
    /// <para>获取当前平台是否支持驱动包安装；Windows 上恒为 <see langword="true"/>。</para>
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Gets whether the current process runs with an administrator token.
    /// <para>获取当前进程是否以管理员令牌运行。</para>
    /// </summary>
    public static bool IsElevated => WinUsbDriverInstaller.IsElevated;

    /// <summary>
    /// Installs one bundled driver package: the embedded payload is materialized and verified, every INF
    /// file of the package is staged into the driver store, and PnP is asked to retry installation on the
    /// present USB device nodes that have no working driver and whose hardware ids one of the staged INFs
    /// claims. Retrying installation leaves both the enumeration walk and the installation to the PnP
    /// service, so a device that stalls its own start cannot block this call, which synchronous install APIs
    /// do not guarantee. The call refuses while the PnP installation queue reports a pending install,
    /// because staging and retrying onto a busy queue would block every other device install. Requires an
    /// administrator token; the device binding completes asynchronously, so the caller observes devices
    /// through device queries instead of waiting for this call.
    /// <para>安装一个内置驱动包：物化并校验内嵌负载，把包内每个 INF 暂存到驱动仓库，并请求 PnP 对在位但无可用
    /// 驱动、且硬件 ID 与某个已暂存 INF 声明相符的 USB 设备节点重试安装。重试安装把枚举与安装都交给 PnP 服务，
    /// 因此自身启动卡住的设备不会阻塞本次调用，而同步安装 API 不保证这一点。PnP 安装队列仍有挂起安装时本次
    /// 调用直接拒绝，因为向繁忙的队列暂存与重试会阻塞所有其他设备安装。需要管理员令牌；设备绑定异步完成，
    /// 调用方应通过设备查询观察设备，而不是等待本次调用。</para>
    /// </summary>
    /// <param name="packageId">The package id from <see cref="DriverPackageCatalog"/>, for example <c>rockusb</c>.
    /// <para><see cref="DriverPackageCatalog"/> 中的包标识，例如 <c>rockusb</c>。</para></param>
    /// <returns>The operation result; never throws. <para>操作结果；绝不抛出。</para></returns>
    public WinUsbDriverOperationResult Install(string packageId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "Vendor driver package installation is only supported on Windows.");
        }

        if (!IsElevated)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.ElevationRequired,
                "Installing a vendor driver package requires an administrator token; relaunch the application as administrator.");
        }

        var availability = CheckPackage(packageId, "installed");
        if (availability is not null)
        {
            return availability;
        }

        var package = DriverPackageCatalog.Find(packageId)!;
        var workDirectory = Path.Combine(Path.GetTempPath(), WorkDirectoryName, Path.GetRandomFileName());
        try
        {
            if (!WinUsbDriverNativeMethods.WaitForNoPendingInstallation(WinUsbDriverInstaller.PendingInstallWaitTimeout))
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    "Another device installation is pending; the package is not staged onto a busy PnP queue. Finish that installation or replug the device and retry.");
            }

            var materialized = DriverPackageAssets.Materialize(package.Id, workDirectory);
            var staged = new List<string>(package.InfFileNames.Count);
            foreach (var infName in package.InfFileNames)
            {
                if (!materialized.Contains(infName, StringComparer.OrdinalIgnoreCase))
                {
                    return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                        $"The embedded driver package '{package.Id}' does not carry '{infName}'.");
                }

                var infPath = Path.Combine(workDirectory, infName);
                if (!WinUsbDriverNativeMethods.SetupCopyOEMInfW(infPath, IntPtr.Zero,
                        WinUsbDriverNativeMethods.SpOemSourcePath, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                        WinUsbDriverNativeMethods.DescribeLastWin32Error($"SetupCopyOEMInf failed for '{infName}'"));
                }

                staged.Add(infName);
            }

            var hardwareIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var infName in staged)
            {
                if (DriverPackageInfReader.TryReadFile(Path.Combine(workDirectory, infName), out var metadata) &&
                    metadata is not null)
                {
                    foreach (var hardwareId in metadata.HardwareIds)
                    {
                        hardwareIds.Add(hardwareId);
                    }
                }
            }

            var retried = RetryDriverlessDeviceInstallation(hardwareIds);
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                $"Staged {staged.Count} INF file(s) of '{package.Id}' ({string.Join(", ", staged)}) and asked PnP to retry installation on {retried} matching device node(s); installation completes asynchronously.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException or InvalidDataException)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The driver package '{packageId}' could not be prepared: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    /// Removes the staged OEM INFs that belong to one bundled driver package, matching the provider and
    /// catalog file the package INFs declare, and lets the affected devices fall back to a system driver on
    /// re-enumeration. Requires an administrator token. Packages installed by another provider are never
    /// removed: when ownership of a staged package cannot be established the call reports
    /// <see cref="WinUsbDriverOperationStatus.Failed"/> instead of guessing.
    /// <para>移除某个内置驱动包对应的已暂存 OEM INF，匹配依据是包内 INF 声明的提供者与目录文件，并让受影响的
    /// 设备在重新枚举后回退系统驱动。需要管理员令牌。绝不移除由其他提供者安装的包：无法确认已暂存包的归属时，
    /// 调用返回 <see cref="WinUsbDriverOperationStatus.Failed"/> 而不是猜测。</para>
    /// </summary>
    /// <param name="packageId">The package id from <see cref="DriverPackageCatalog"/>, for example <c>ftdi-cdm</c>.
    /// <para><see cref="DriverPackageCatalog"/> 中的包标识，例如 <c>ftdi-cdm</c>。</para></param>
    /// <returns>The operation result; never throws. <para>操作结果；绝不抛出。</para></returns>
    public WinUsbDriverOperationResult Uninstall(string packageId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                "Vendor driver package removal is only supported on Windows.");
        }

        if (!IsElevated)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.ElevationRequired,
                "Removing a vendor driver package requires an administrator token; relaunch the application as administrator.");
        }

        var availability = CheckPackage(packageId, "removed");
        if (availability is not null)
        {
            return availability;
        }

        var package = DriverPackageCatalog.Find(packageId)!;
        try
        {
            var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var catalogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var infName in package.InfFileNames)
            {
                var inf = DriverPackageAssets.TryReadFile(package.Id, infName);
                if (inf is null)
                {
                    return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                        $"The embedded driver package '{package.Id}' does not carry '{infName}'.");
                }

                if (!DriverPackageInfReader.TryRead(inf, out var metadata) || metadata is null)
                {
                    continue;
                }

                providers.Add(metadata.Provider);
                foreach (var catalog in metadata.CatalogFileNames)
                {
                    catalogs.Add(Path.GetFileName(catalog));
                }
            }

            if (providers.Count == 0 || catalogs.Count == 0)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    $"The INF files of '{package.Id}' declare no provider or no catalog file, so a staged package cannot be attributed to it; removal is refused to protect other drivers.");
            }

            var stagedInfs = ReadStagedInfFiles();
            if (stagedInfs is null)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    "The driver repository could not be enumerated, so no staged package can be attributed; removal is refused.");
            }

            var matches = new List<string>();
            var foreign = new List<string>();
            foreach (var staged in stagedInfs)
            {
                var declared = staged.CatalogFileNames.Any(
                    catalog => catalogs.Contains(Path.GetFileName(catalog)));
                var rewritten = staged.CatalogFileNames.Any(
                    catalog => string.Equals(Path.GetFileName(catalog),
                        Path.GetFileNameWithoutExtension(staged.FileName) + ".cat", StringComparison.OrdinalIgnoreCase));
                if ((!declared && !rewritten) || string.IsNullOrWhiteSpace(staged.Provider))
                {
                    continue;
                }

                if (providers.Contains(staged.Provider))
                {
                    matches.Add(staged.FileName);
                }
                else if (declared)
                {
                    foreign.Add(staged.FileName);
                }
            }

            if (matches.Count == 0)
            {
                if (foreign.Count > 0)
                {
                    return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                        $"The staged package(s) {string.Join(", ", foreign)} use the catalog file of '{package.Id}' but declare another provider, so they belong to another vendor; removal is refused.");
                }

                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                    $"No staged OEM INF matches the provider(s) {string.Join(", ", providers)} and catalog file(s) {string.Join(", ", catalogs)} of '{package.Id}'; nothing was removed.");
            }

            var removed = new List<string>(matches.Count);
            foreach (var infName in matches)
            {
                if (!WinUsbDriverNativeMethods.SetupUninstallOEMInfW(infName,
                        WinUsbDriverNativeMethods.SuoiForceDelete, IntPtr.Zero)
                    && Marshal.GetLastWin32Error() != WinUsbDriverNativeMethods.ErrorFileNotFound)
                {
                    return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                        WinUsbDriverNativeMethods.DescribeLastWin32Error($"SetupUninstallOEMInf failed for '{infName}'"));
                }

                removed.Add(infName);
            }

            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Succeeded,
                $"Removed {removed.Count} staged driver package(s) of '{package.Id}' ({string.Join(", ", removed)}); the devices return to a system driver when they are re-enumerated.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException or InvalidDataException)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The driver package '{packageId}' could not be inspected: {ex.Message}");
        }
    }

    private static WinUsbDriverOperationResult? CheckPackage(string packageId, string pastTenseAction)
    {
        var package = DriverPackageCatalog.Find(packageId);
        if (package is null)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"No driver package matches '{packageId}'.");
        }

        if (package.InfFileNames.Count == 0)
        {
            var reason = package.UnavailableReasonKey ?? "NotBundled";
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The driver package '{package.Id}' is not bundled with this application ({reason}), so it cannot be {pastTenseAction}.");
        }

        if (!package.IsAvailable)
        {
            return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                $"The driver package '{package.Id}' is not usable here: its embedded assets are incomplete or the platform is not Windows.");
        }

        return null;
    }

    /// <summary>
    /// Asks PnP to retry installation on the present USB device nodes that have no working driver and whose
    /// hardware ids the staged INFs claim. Every re-enumeration is queued only while the PnP installation
    /// queue is drained; a busy queue stops the walk, because queueing onto it would worsen the blockage all
    /// device installs share.
    /// <para>请求 PnP 对在位、无可用驱动且硬件 ID 与已暂存 INF 声明相符的 USB 设备节点重试安装。每次重枚举都只在
    /// PnP 安装队列已排空时排队；队列繁忙时停止遍历，因为继续排队会加剧所有设备安装共享的堵塞。</para>
    /// </summary>
    /// <param name="hardwareIds">The hardware ids the staged INFs claim; an empty set keeps the retry
    /// unrestricted for INFs that declare none.
    /// <para>已暂存 INF 声明的硬件 ID；集合为空时对未声明硬件 ID 的 INF 保持不限定重试。</para></param>
    /// <returns>The number of re-enumerations queued. <para>已排队的重枚举数量。</para></returns>
    private static int RetryDriverlessDeviceInstallation(HashSet<string> hardwareIds)
    {
        if (WinUsbDriverNativeMethods.CmGetDeviceIdListSizeW(out var size, UsbEnumeratorFilter,
                WinUsbDriverNativeMethods.CmGetIdListFilterPresent) != WinUsbDriverNativeMethods.CrSuccess ||
            size == 0 || size > MaximumDeviceIdListBytes)
        {
            return 0;
        }

        var buffer = new byte[size];
        if (WinUsbDriverNativeMethods.CmGetDeviceIdListW(buffer, size, UsbEnumeratorFilter,
                WinUsbDriverNativeMethods.CmGetIdListFilterPresent) != WinUsbDriverNativeMethods.CrSuccess)
        {
            return 0;
        }

        var retried = 0;
        foreach (var instanceId in DecodeDeviceIdList(buffer))
        {
            if (retried == MaximumReenumeratedDevices)
            {
                break;
            }

            if (!WinUsbDriverNativeMethods.WaitForNoPendingInstallation(TimeSpan.Zero))
            {
                break;
            }

            if (WinUsbDriverNativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId,
                    WinUsbDriverNativeMethods.CmLocateDevnodeNormal) != WinUsbDriverNativeMethods.CrSuccess)
            {
                continue;
            }

            if (!HasNoWorkingDriver(devInst) || !MatchesStagedHardwareIds(devInst, hardwareIds))
            {
                continue;
            }

            if (WinUsbDriverNativeMethods.CmReenumerateDevNode(devInst,
                    WinUsbDriverNativeMethods.CmReenumerateRetryInstallation |
                    WinUsbDriverNativeMethods.CmReenumerateAsynchronous) ==
                WinUsbDriverNativeMethods.CrSuccess)
            {
                retried++;
            }
        }

        return retried;
    }

    /// <summary>
    /// Tests whether one driverless devnode is covered by the staged package: either the package INFs declare
    /// no hardware id and every driverless node stays eligible, or the devnode's hardware ids intersect the
    /// declared ones.
    /// <para>判断一个无驱动设备节点是否被已暂存包覆盖：包内 INF 未声明硬件 ID 时所有无驱动节点仍然参与，否则
    /// 设备节点的硬件 ID 必须与声明相交。</para>
    /// </summary>
    /// <param name="devInst">The devnode handle. <para>设备节点句柄。</para></param>
    /// <param name="hardwareIds">The hardware ids the staged INFs claim.
    /// <para>已暂存 INF 声明的硬件 ID。</para></param>
    /// <returns><see langword="true"/> when the devnode may be re-enumerated for this package.
    /// <para>该设备节点可为本包重枚举时为 <see langword="true"/>。</para></returns>
    private static bool MatchesStagedHardwareIds(uint devInst, HashSet<string> hardwareIds)
    {
        if (hardwareIds.Count == 0)
        {
            return true;
        }

        return WinUsbDriverNativeMethods.ReadDevNodeStringList(devInst,
            WinUsbDriverNativeMethods.DeviceHardwareIdsKey).Any(hardwareIds.Contains);
    }

    private static bool HasNoWorkingDriver(uint devInst)
    {
        if (WinUsbDriverNativeMethods.CmGetDevNodeStatus(out var status, out var problemNumber, devInst, 0)
            != WinUsbDriverNativeMethods.CrSuccess)
        {
            return false;
        }

        return (status & WinUsbDriverNativeMethods.DnHasProblem) != 0 &&
            problemNumber == WinUsbDriverNativeMethods.CmProbFailedInstall;
    }

    private static List<(string FileName, string? Provider, IReadOnlyList<string> CatalogFileNames)>? ReadStagedInfFiles()
    {
        var driverStore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "inf");
        var staged = new List<(string, string?, IReadOnlyList<string>)>();
        try
        {
            if (!Directory.Exists(driverStore))
            {
                return staged;
            }

            foreach (var path in Directory.EnumerateFiles(driverStore, "oem*.inf"))
            {
                if (staged.Count == MaximumStagedInfFiles)
                {
                    break;
                }

                var fileName = Path.GetFileName(path);
                if (!DriverPackageInfReader.TryReadFile(path, out var metadata) || metadata is null)
                {
                    staged.Add((fileName, null, []));
                    continue;
                }

                staged.Add((fileName, metadata.Provider, metadata.CatalogFileNames));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return staged;
    }

    private static List<string> DecodeDeviceIdList(byte[] buffer)
    {
        var instanceIds = new List<string>();
        var start = -1;
        for (var offset = 0; offset + 1 < buffer.Length; offset += 2)
        {
            var character = (char)(buffer[offset] | (buffer[offset + 1] << 8));
            if (character == '\0')
            {
                if (start < 0)
                {
                    continue;
                }

                var instanceId = Encoding.Unicode.GetString(buffer, start, offset - start);
                if (instanceId.Length > 0)
                {
                    instanceIds.Add(instanceId);
                }

                start = -1;
                if (instanceIds.Count == MaximumDeviceCount)
                {
                    break;
                }

                continue;
            }

            if (start < 0)
            {
                start = offset;
            }
        }

        return instanceIds;
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