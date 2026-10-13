using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Keys;

/// <summary>
/// Provides the ability to close the main window and restart the application in a new process, so all
/// state can be saved before restarting and the solution that was open can be reopened.
/// <para>提供通过关闭主窗口并以新进程重启应用程序的能力，可在重启前保存全部状态并重新打开关闭前的解决方案。</para>
/// </summary>
internal static class ApplicationRestartService
{
    /// <summary>
    /// Initiates a restart: records the solution path, registers a one-shot handler on the main window's
    /// Closed event that launches a new instance reopening that solution, and closes the main window.
    /// <para>发起重启：记录解决方案路径，在主窗口 Closed 事件上注册一次性处理器以在新实例中重新打开该解决方案，
    /// 然后关闭主窗口。</para>
    /// </summary>
    /// <returns>Whether the restart request was initiated; false when no main window exists.
    /// <para>是否已发起重启请求；主窗口不存在时为 false。</para></returns>
    internal static bool RestartWithSolution()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } mainWindow)
        {
            return false;
        }

        string? solutionPath = ProjectContextService.Instance.CurrentSolutionPath;

        mainWindow.Closed += (_, _) =>
        {
            if (Environment.ProcessPath is not { } processPath)
            {
                return;
            }

            Process.Start(new ProcessStartInfo(processPath)
            {
                UseShellExecute = false,
                Arguments = string.IsNullOrWhiteSpace(solutionPath) ? string.Empty : solutionPath,
            });
        };

        mainWindow.Close();
        return true;
    }
}