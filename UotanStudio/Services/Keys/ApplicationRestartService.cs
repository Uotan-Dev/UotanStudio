using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Keys;

/// <summary>
/// <para>提供通过关闭主窗口并以新进程重启应用程序的能力，可在重启前保存全部状态并重新打开关闭前的解决方案。</para>
/// Provides the ability to close the main window and restart the application in a new process, so all
/// state can be saved before restarting and the solution that was open can be reopened.
/// </summary>
/// <remarks>
/// <para>AOT 兼容：不使用反射，仅通过系统进程 API 启动新的程序实例。</para>
/// AOT compatible: no reflection is used; a new process instance is started only via system process APIs.
/// </remarks>
internal static class ApplicationRestartService
{
    /// <summary>
    /// <para>发起重启：先保存关闭前的解决方案路径，在主窗口真正关闭（Closed）后启动新实例并重新打开该解决方案，然后关闭主窗口。主窗口关闭即退出应用，无需显式 Shutdown。</para>
    /// Initiates a restart: first records the solution path, then registers a one-shot handler on the main
    /// window's Closed event (after the window is really closed) that launches a new instance which reopens
    /// that solution, and finally closes the main window. Closing the main window exits the app, so no
    /// explicit Shutdown is needed.
    /// </summary>
    /// <remarks>
    /// <para>必须挂在 Closed 而不是 Close() 调用之后：既有关闭流程（MainWindow_Closing）可能异步取消或延迟关闭，只有 Closed 触发才代表窗口真正关闭。若用户取消关闭或保存失败，Closed 不触发，重启自然中止。</para>
    /// The logic must hang off Closed rather than right after calling Close(): the existing closing flow
    /// (MainWindow_Closing) may asynchronously cancel or delay the close, and only Closed means the window
    /// is really gone. If the user cancels or saving fails, Closed never fires and the restart aborts.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <para>是否已发起重启请求（主窗口存在且已触发关闭流程）；主窗口为 null 时返回 false。</para>
    /// Whether the restart request was initiated (a main window exists and the closing flow was triggered);
    /// returns false when the main window is null.
    /// </returns>
    internal static bool RestartWithSolution()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } mainWindow)
        {
            return false;
        }

        // Record the solution to reopen in the new instance. 读取关闭前解决方案路径，供新实例重新打开。
        string? solutionPath = ProjectContextService.Instance.CurrentSolutionPath;

        mainWindow.Closed += (_, _) =>
        {
            // Environment.ProcessPath 为 null 时不启动新实例，只退出。
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

        // Trigger the existing closing flow (FlushAutoSaveAsync persists panels/documents/session).
        // 触发既有关闭流程（FlushAutoSaveAsync 保存面板/文档/会话）；取消或失败时 Closed 不触发，重启中止。
        mainWindow.Close();
        return true;
    }
}
