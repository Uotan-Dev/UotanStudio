using System.Runtime.Versioning;
using UotanStudio.Comm.Transports.Usb.Drivers;

namespace UotanStudio.Services.Devices;

/// <summary>
/// The elevated-helper command line used by the USB takeover and the settings driver package actions: the
/// running process relaunches the application with <c>runas</c>, and the new process parses these
/// arguments, runs the takeover, release or vendor package operation, and writes the outcome to the result
/// file for the parent to read.
/// <para>USB 接管与设置页驱动包操作使用的提权助手命令行：当前进程以 <c>runas</c> 重启应用，新进程解析
/// 这些参数，执行接管、解除接管或厂商驱动包操作，并把结果写入父进程读取的结果文件。</para>
/// </summary>
public static class UsbDriverElevatedCommand
{
    /// <summary>
    /// The switch selecting the device takeover; followed by the instance id and hardware id.
    /// <para>选择设备接管的开关；后接实例 ID 与硬件 ID。</para>
    /// </summary>
    public const string TakeoverSwitch = "--usb-driver-takeover";

    /// <summary>
    /// The switch selecting the end of a device takeover; followed by the instance id.
    /// <para>选择结束设备接管的开关；后接实例 ID。</para>
    /// </summary>
    public const string ReleaseSwitch = "--usb-driver-release";

    /// <summary>
    /// The switch selecting a vendor driver package install; followed by the package identifier.
    /// <para>选择厂商驱动包安装的开关；后接包标识。</para>
    /// </summary>
    public const string PackageInstallSwitch = "--usb-driver-package-install";

    /// <summary>
    /// The switch selecting a vendor driver package removal; followed by the package identifier.
    /// <para>选择厂商驱动包移除的开关；后接包标识。</para>
    /// </summary>
    public const string PackageUninstallSwitch = "--usb-driver-package-uninstall";

    /// <summary>
    /// The switch carrying the file the helper writes its outcome to.
    /// <para>携带助手写入结果文件路径的开关。</para>
    /// </summary>
    public const string ResultSwitch = "--usb-driver-result";

    /// <summary>
    /// Gets whether the arguments select the elevated driver helper mode.
    /// <para>获取参数是否选择提权驱动助手模式。</para>
    /// </summary>
    /// <param name="args">The process arguments. <para>进程参数。</para></param>
    /// <returns>
    /// <see langword="true"/> when the first argument selects a takeover, release or package operation.
    /// <para>首个参数选择接管、解除接管或驱动包操作时为 <see langword="true"/>。</para>
    /// </returns>
    public static bool IsRequested(string[] args) =>
        args is [TakeoverSwitch or ReleaseSwitch or PackageInstallSwitch or PackageUninstallSwitch, ..];

    /// <summary>
    /// Runs the requested driver operation in this process and writes its outcome to the result file.
    /// <para>在本进程执行请求的驱动操作，并把结果写入结果文件。</para>
    /// </summary>
    /// <param name="args">The process arguments. <para>进程参数。</para></param>
    /// <returns>0 when the operation succeeded, 1 otherwise. <para>操作成功时为 0，否则为 1。</para></returns>
    public static int Run(string[] args) => OperatingSystem.IsWindows()
        ? RunWindows(args)
        : ReportUnsupported(FindValue(args, ResultSwitch));

    [SupportedOSPlatform("windows")]
    private static int RunWindows(string[] args)
    {
        var result = Execute(args);
        WriteResult(FindValue(args, ResultSwitch), result);
        return result.Status == WinUsbDriverOperationStatus.Succeeded ? 0 : 1;
    }

    private static int ReportUnsupported(string? resultPath)
    {
        WriteResultText(resultPath, WinUsbDriverOperationStatus.Failed,
            "WinUSB driver operations are only supported on Windows.");
        return 1;
    }

    [SupportedOSPlatform("windows")]
    private static WinUsbDriverOperationResult Execute(string[] args)
    {
        var installer = new WinUsbDriverInstaller();
        var values = SelectPositional(args);
        if (args[0] == TakeoverSwitch)
        {
            if (values.Count < 2)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    "The elevated takeover requires an instance id and a hardware id.");
            }

            return installer.InstallTakeover(new WinUsbTakeoverRequest
            {
                InstanceId = values[0],
                HardwareId = values[1]
            });
        }

        if (args[0] == ReleaseSwitch)
        {
            return values.Count < 1
                ? new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    "Ending the takeover requires a device instance id.")
                : installer.UninstallTakeover(values[0]);
        }

        if (args[0] is PackageInstallSwitch or PackageUninstallSwitch)
        {
            if (values.Count < 1)
            {
                return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
                    "The elevated driver package operation requires a package identifier.");
            }

            var packages = new DriverPackageInstaller();
            return args[0] == PackageInstallSwitch
                ? packages.Install(values[0])
                : packages.Uninstall(values[0]);
        }

        return new WinUsbDriverOperationResult(WinUsbDriverOperationStatus.Failed,
            "The elevated helper does not recognise the requested driver operation.");
    }

    private static List<string> SelectPositional(string[] args)
    {
        List<string> values = [];
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is ResultSwitch)
            {
                index++;
                continue;
            }

            values.Add(argument);
        }

        return values;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteResult(string? resultPath, WinUsbDriverOperationResult result)
        => WriteResultText(resultPath, result.Status, result.Message);

    private static void WriteResultText(string? resultPath, WinUsbDriverOperationStatus status, string? message)
    {
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return;
        }

        try
        {
            File.WriteAllText(resultPath, $"{status}\n{message ?? string.Empty}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? FindValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}