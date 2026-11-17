using System.Diagnostics;
using UotanStudio.Build;

namespace UotanStudio.Services.Update;

/// <summary>
/// The platform action that hands a verified update package to the operating system.
/// <para>把已校验的更新包交给操作系统的平台动作。</para>
/// </summary>
public enum AppUpdateLaunchOutcome
{
    /// <summary>The installer or package manager was started. <para>安装器或包管理器已启动。</para></summary>
    Started,

    /// <summary>The platform has no known way to open the package. <para>当前平台没有已知方式打开该包。</para></summary>
    Unsupported,

    /// <summary>Starting the platform action failed. <para>启动平台动作失败。</para></summary>
    Failed,
}

/// <summary>
/// The result of handing an update package to the platform.
/// <para>把更新包交给平台的结果。</para>
/// </summary>
/// <param name="Outcome">Whether the platform action started. <para>平台动作是否已启动。</para></param>
/// <param name="Detail">A diagnostic message for the log and the UI. <para>供日志与界面使用的诊断信息。</para></param>
/// <param name="RequiresApplicationExit">Whether the caller should shut the application down so the installer can replace it.
/// <para>调用方是否应关闭应用，以便安装器替换它。</para></param>
public sealed record AppUpdateLaunchResult(
    AppUpdateLaunchOutcome Outcome,
    string Detail,
    bool RequiresApplicationExit)
{
    /// <summary>
    /// Creates a successful result. <para>创建成功结果。</para>
    /// </summary>
    /// <param name="detail">Diagnostic detail. <para>诊断信息。</para></param>
    /// <param name="requiresExit">Whether the application must exit first. <para>应用是否须先退出。</para></param>
    /// <returns>The result. <para>结果。</para></returns>
    public static AppUpdateLaunchResult Started(string detail, bool requiresExit)
        => new(AppUpdateLaunchOutcome.Started, detail, requiresExit);
}

/// <summary>
/// Hands a downloaded update package to the platform installer: the MSIX app installer on Windows, the
/// disk image on macOS, and the distribution package manager on Linux.
/// <para>把下载好的更新包交给平台安装器：Windows 上是 MSIX 应用安装程序，macOS 上是磁盘映像，
/// Linux 上是发行版包管理器。</para>
/// </summary>
/// <remarks>
/// Nothing is installed by the application itself: each platform keeps ownership of its own install and
/// elevation flow, so the application only starts the platform action and reports what happened. macOS
/// requires the running bundle to exit before the disk image replaces it, which the result reports through
/// <see cref="AppUpdateLaunchResult.RequiresApplicationExit"/>.
/// <para>应用自身不安装任何东西：每个平台保留自己的安装与提权流程，应用只启动平台动作并报告结果。
/// macOS 需要在磁盘映像替换前退出正在运行的包，结果通过
/// <see cref="AppUpdateLaunchResult.RequiresApplicationExit"/> 上报。</para>
/// </remarks>
public static class AppUpdateLauncher
{
    /// <summary>
    /// Starts the platform action for a verified package.
    /// <para>为已校验的包启动平台动作。</para>
    /// </summary>
    /// <param name="packagePath">Absolute path of the verified package. <para>已校验包的绝对路径。</para></param>
    /// <param name="kind">The package kind deciding which platform action runs.
    /// <para>决定运行哪一平台动作的包类型。</para></param>
    /// <returns>The launch result describing whether the action started. <para>描述动作是否启动的结果。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="packagePath"/> is empty or whitespace.
    /// <para><paramref name="packagePath"/> 为空或只有空白。</para></exception>
    public static AppUpdateLaunchResult Launch(string packagePath, AppUpdatePackageKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        try
        {
            return kind switch
            {
                AppUpdatePackageKind.Msix => LaunchWindowsMsix(packagePath),
                AppUpdatePackageKind.Dmg => LaunchMacDiskImage(packagePath),
                AppUpdatePackageKind.Deb => LaunchLinuxPackage(packagePath, "deb"),
                AppUpdatePackageKind.Rpm => LaunchLinuxPackage(packagePath, "rpm"),
                AppUpdatePackageKind.Apk => LaunchLinuxPackage(packagePath, "apk"),
                AppUpdatePackageKind.Pacman => LaunchLinuxPackage(packagePath, "pacman"),
                _ => new AppUpdateLaunchResult(AppUpdateLaunchOutcome.Unsupported,
                    "This package is a plain archive and has no platform installer.", false),
            };
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Update",
                $"Starting the update installer for '{packagePath}' failed: {ex.GetType().Name}: {ex.Message}");
            return new AppUpdateLaunchResult(AppUpdateLaunchOutcome.Failed, ex.Message, false);
        }
    }

    /// <summary>
    /// Opens an MSIX package with the Windows app installer, which owns its own elevation prompt.
    /// <para>用 Windows 应用安装程序打开 MSIX 包，由它自行处理提权提示。</para>
    /// </summary>
    /// <param name="packagePath">The MSIX path. <para>MSIX 路径。</para></param>
    /// <returns>The launch result. <para>启动结果。</para></returns>
    private static AppUpdateLaunchResult LaunchWindowsMsix(string packagePath)
    {
        using var process = Process.Start(new ProcessStartInfo(packagePath)
        {
            UseShellExecute = true,
        });
        LogService.Instance.Write(LogLevel.Info, "Update", $"Handed '{packagePath}' to the Windows app installer.");
        return AppUpdateLaunchResult.Started("The Windows app installer was started.", false);
    }

    /// <summary>
    /// Opens a macOS disk image, after which the application exits so the bundle can be replaced.
    /// <para>打开 macOS 磁盘映像，随后应用退出以便替换应用包。</para>
    /// </summary>
    /// <param name="packagePath">The disk image path. <para>磁盘映像路径。</para></param>
    /// <returns>The launch result asking the caller to exit. <para>要求调用方退出的启动结果。</para></returns>
    private static AppUpdateLaunchResult LaunchMacDiskImage(string packagePath)
    {
        using var process = Process.Start(new ProcessStartInfo("open")
        {
            UseShellExecute = false,
            ArgumentList = { packagePath },
        });
        LogService.Instance.Write(LogLevel.Info, "Update", $"Opened update disk image '{packagePath}'.");
        return AppUpdateLaunchResult.Started("The disk image was opened; drag the app into Applications.", true);
    }

    /// <summary>
    /// Opens a Linux distribution package with the distribution's own package manager in a terminal, so the
    /// human keeps control of the elevation prompt.
    /// <para>在终端里用发行版自带的包管理器打开 Linux 发行包，使提权提示始终由用户掌控。</para>
    /// </summary>
    /// <param name="packagePath">The package path. <para>包路径。</para></param>
    /// <param name="flavor">The package flavor deciding the command. <para>决定命令的包类型。</para></param>
    /// <returns>The launch result. <para>启动结果。</para></returns>
    private static AppUpdateLaunchResult LaunchLinuxPackage(string packagePath, string flavor)
    {
        var command = flavor switch
        {
            "deb" => $"sudo apt install --yes '{packagePath}'",
            "rpm" => $"sudo dnf install --assumeyes '{packagePath}'",
            "apk" => $"sudo apk add --allow-untrusted '{packagePath}'",
            _ => $"sudo pacman -U --noconfirm '{packagePath}'",
        };

        var terminal = FindLinuxTerminal();
        if (terminal is null)
        {
            LogService.Instance.Write(LogLevel.Warn, "Update",
                $"No terminal emulator was found to run the package manager for '{packagePath}'.");
            return new AppUpdateLaunchResult(AppUpdateLaunchOutcome.Unsupported,
                "No terminal emulator was found; install the package manually.",
                false);
        }

        using var process = Process.Start(new ProcessStartInfo(terminal.Value.File)
        {
            UseShellExecute = false,
            ArgumentList = { terminal.Value.Argument, command },
        });
        LogService.Instance.Write(LogLevel.Info, "Update",
            $"Opened the package manager for '{packagePath}' in {terminal.Value.File}.");
        return AppUpdateLaunchResult.Started("The package manager was started in a terminal.", false);
    }

    /// <summary>
    /// Locates a terminal emulator able to run a shell command on Linux.
    /// <para>在 Linux 上查找可执行 shell 命令的终端模拟器。</para>
    /// </summary>
    /// <returns>The executable and its "run this command" switch, or null when none is installed.
    /// <para>可执行文件及其「执行该命令」开关；未安装任何终端时为 null。</para></returns>
    private static (string File, string Argument)? FindLinuxTerminal()
    {
        var candidates = new (string File, string Argument)[]
        {
            ("x-terminal-emulator", "-e"),
            ("gnome-terminal", "--"),
            ("konsole", "-e"),
            ("xfce4-terminal", "-e"),
            ("alacritty", "-e"),
            ("kitty", "-e"),
            ("xterm", "-e"),
        };

        foreach (var candidate in candidates)
        {
            if (ExistsOnPath(candidate.File))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an executable name resolves on the current <c>PATH</c>.
    /// <para>可执行文件名能否在当前 <c>PATH</c> 上解析到。</para>
    /// </summary>
    /// <param name="name">The executable name. <para>可执行文件名。</para></param>
    /// <returns>True when the executable exists. <para>可执行文件存在时为真。</para></returns>
    private static bool ExistsOnPath(string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PATH")))
        {
            return false;
        }

        foreach (var directory in Environment.GetEnvironmentVariable("PATH")!
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, name)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return false;
    }
}