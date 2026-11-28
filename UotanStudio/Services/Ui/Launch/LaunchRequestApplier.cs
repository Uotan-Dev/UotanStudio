using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Services.Launch;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Services.Ui.Launch;

/// <summary>
/// Applies one desktop launch to the workspace: solutions open as projects, standalone project files open
/// the solution that owns them, and every other file opens through the document registry instead of the
/// image list, so a launch works even when no project is open.
/// <para>把一个桌面启动请求应用到工作区：解决方案按工程打开，独立工程文件打开其所属解决方案，其余文件经文档
/// 注册表打开而不是导入镜像清单，因此即使没有打开工程也能生效。</para>
/// </summary>
internal static class LaunchRequestApplier
{
    /// <summary>
    /// Opens every existing path of the launch in the desktop main model and switches to the workspace when
    /// a document or project became visible.
    /// <para>在桌面主模型中打开启动请求里的每个现存路径，并在文档或工程变为可见时切到工作区。</para>
    /// </summary>
    /// <param name="main">The desktop main model receiving the launch.<para>接收启动请求的桌面主模型。</para></param>
    /// <param name="files">The launch paths; paths that no longer exist are ignored.<para>启动路径；已不存在的路径被忽略。</para></param>
    /// <returns>A task that completes after every open attempt.<para>全部打开尝试结束后完成的任务。</para></returns>
    public static async Task ApplyAsync(MainViewModel main, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(main);
        if (files.Count == 0)
        {
            return;
        }

        var targets = await Task.Run(() => ClassifyAll(files)).ConfigureAwait(true);
        if (targets.Length == 0)
        {
            return;
        }

        var opened = await OpenSolutionsAsync(main, targets).ConfigureAwait(true);
        var navigate = opened.Count > 0;
        foreach (var target in targets)
        {
            switch (target.Kind)
            {
                case LaunchFileKind.Solution:
                    break;
                case LaunchFileKind.FirmwareProject when target.OwningSolution is { } firmwareSolution
                    && opened.Contains(firmwareSolution):
                    if (await ProjectOperationsService.Instance.SwitchSubProjectAsync(target.ProjectName ?? string.Empty)
                        .ConfigureAwait(true))
                    {
                        navigate = true;
                    }

                    break;
                case LaunchFileKind.NodeProject when target.OwningSolution is { } nodeSolution
                    && opened.Contains(nodeSolution):
                    await main.MenuVM.OpenNodeProjectAsync(target.ProjectName ?? string.Empty).ConfigureAwait(true);
                    break;
                default:
                    await main.WorkspaceVM.OpenDocumentAsync(target.Path).ConfigureAwait(true);
                    navigate = true;
                    break;
            }
        }

        if (navigate)
        {
            WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
        }
    }

    private static async Task<HashSet<string>> OpenSolutionsAsync(MainViewModel main, LaunchFileTarget[] targets)
    {
        var solutions = new List<string>();
        var requested = targets.LastOrDefault(target => target.Kind == LaunchFileKind.Solution);
        if (requested is not null)
        {
            solutions.Add(requested.Path);
        }

        foreach (var target in targets)
        {
            if (target.OwningSolution is { } owning && !solutions.Contains(owning, StringComparer.OrdinalIgnoreCase))
            {
                solutions.Add(owning);
            }
        }

        HashSet<string> opened = new(StringComparer.OrdinalIgnoreCase);
        foreach (var solution in solutions)
        {
            if (await main.MenuVM.OpenProjectFromPathAsync(solution).ConfigureAwait(true))
            {
                opened.Add(solution);
            }
        }

        return opened;
    }

    private static LaunchFileTarget[] ClassifyAll(IReadOnlyList<string> files)
    {
        var targets = new List<LaunchFileTarget>(files.Count);
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                targets.Add(LaunchFileTarget.Classify(file));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                LogService.Instance.Write(LogLevel.Warn, "Launch", $"Skipping launch path '{file}': {ex.Message}");
            }
        }

        return [.. targets];
    }
}