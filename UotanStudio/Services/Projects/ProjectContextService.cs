namespace UotanStudio.Services.Projects;

/// <summary>
/// Per-sub-project directory context.
/// <para>每个子项目的目录上下文。</para>
/// </summary>
/// <param name="Name">Sub-project display name. <para>子项目显示名称。</para></param>
/// <param name="RootPath">Absolute path of the sub-project root directory. <para>子项目根目录绝对路径。</para></param>
/// <param name="FwProjectPath">Absolute path of the <c>.uotanfw</c> file. <para><c>.uotanfw</c> 文件绝对路径。</para></param>
/// <param name="ImagesDir">Absolute path of the <c>images/</c> directory. <para><c>images/</c> 目录绝对路径。</para></param>
/// <param name="OutputDir">Absolute path of the <c>output/</c> directory. <para><c>output/</c> 目录绝对路径。</para></param>
public sealed record SubProjectContext(
    string Name,
    string RootPath,
    string FwProjectPath,
    string ImagesDir,
    string OutputDir);

/// <summary>
/// Current solution context service (singleton), holding the state and directory layout of the opened
/// <c>.uotan</c> solution: data root, sub-projects, node project file, session state directory
/// <c>.uotan/</c>, and relative/absolute path conversion.
/// <para>当前解决方案上下文服务（单例），承载已打开 <c>.uotan</c> 解决方案的状态与目录布局：
/// 数据根目录、子项目列表、节点项目文件、会话状态目录 <c>.uotan/</c> 及相对/绝对路径转换。</para>
/// </summary>
public sealed class ProjectContextService
{
    private static readonly Lazy<ProjectContextService> LazyInstance = new(() => new ProjectContextService());

    /// <summary>
    /// Gets the singleton instance of the solution context service.
    /// <para>获取解决方案上下文单例实例。</para>
    /// </summary>
    public static ProjectContextService Instance => LazyInstance.Value;

    private ProjectContextService()
    {
    }

    /// <summary>
    /// Full path of the current solution file (<c>.uotan</c>); <c>null</c> when no solution is open.
    /// <para>当前解决方案文件（<c>.uotan</c>）全路径；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    public string? CurrentSolutionPath { get; private set; }

    /// <summary>
    /// Solution name (without extension); <c>null</c> when no solution is open.
    /// <para>解决方案名（不含扩展名）；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    public string? SolutionName { get; private set; }

    /// <summary>
    /// Data root directory (a directory sharing the solution file's base name); <c>null</c> when no solution is open.
    /// <para>数据根目录（与解决方案文件同名的目录）；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    public string? RootDirectory { get; private set; }

    /// <summary>
    /// Firmware project file path — resolves to <see cref="ActiveSubProject"/>.<c>FwProjectPath</c>
    /// when available; falls back to legacy single-project path. <c>null</c> when no solution is open.
    /// <para>固件项目文件路径——优先取 <see cref="ActiveSubProject"/> 的 <c>FwProjectPath</c>；
    /// 回退到旧版单项目路径。未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    public string? FirmwareProjectPath => ActiveSubProject?.FwProjectPath ?? LegacyFirmwareProjectPath;

    private string? LegacyFirmwareProjectPath { get; set; }

    /// <summary>
    /// Node project file path (<c>RootDirectory/&lt;SolutionName&gt;.uotannode</c>); <c>null</c> when no solution is open.
    /// <para>节点项目文件路径（<c>RootDirectory/&lt;SolutionName&gt;.uotannode</c>）；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    public string? NodeProjectPath { get; private set; }

    /// <summary>
    /// Session state directory (<c>RootDirectory/.uotan</c>, like VS's <c>.vs/</c>); <c>null</c> when no solution is open.
    /// <para>会话状态目录（<c>RootDirectory/.uotan</c>，类似 VS 的 <c>.vs/</c>）；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    public string? StateDirectory { get; private set; }

    /// <summary>
    /// All sub-projects discovered in the current solution.
    /// <para>当前解决方案中发现的所有子项目。</para>
    /// </summary>
    public IReadOnlyList<SubProjectContext> SubProjects { get; private set; } = [];

    /// <summary>
    /// The active (startup) sub-project used for pack/extract operations; <c>null</c> when no sub-projects exist.
    /// <para>当前活动（启动）子项目，用于打包/解包操作；无子项目时为 <c>null</c>。</para>
    /// </summary>
    public SubProjectContext? ActiveSubProject { get; private set; }

    /// <summary>
    /// Whether a solution is currently open.
    /// <para>是否已打开解决方案。</para>
    /// </summary>
    public bool IsProjectOpen => CurrentSolutionPath is not null;

    /// <summary>
    /// Sets the active sub-project by name.
    /// <para>按名称设置活动子项目。</para>
    /// </summary>
    /// <param name="name">Sub-project name. <para>子项目名称。</para></param>
    /// <returns><c>true</c> if the sub-project was found and activated; <c>false</c> otherwise.</returns>
    public bool SetActiveSubProject(string name)
    {
        var match = SubProjects.FirstOrDefault(sp =>
            string.Equals(sp.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        ActiveSubProject = match;
        return true;
    }

    /// <summary>
    /// Opens the specified solution: sets the solution path/name and directory layout, scans for
    /// sub-projects (<c>*.uotanfw</c> files), and ensures the <c>.uotan/</c> state directory exists.
    /// <para>打开指定解决方案：设置解决方案路径/名称与目录布局，扫描子项目（<c>*.uotanfw</c> 文件），
    /// 并确保 <c>.uotan/</c> 会话状态目录存在。</para>
    /// </summary>
    /// <param name="solutionPath">
    /// Full path of the solution file (<c>.uotan</c>). <para>解决方案文件（<c>.uotan</c>）全路径。</para>
    /// </param>
    /// <param name="snapshot">
    /// The loaded solution snapshot (used to read sub-project references). <para>已加载的解决方案快照（读取子项目引用）。</para>
    /// </param>
    public void OpenSolution(string solutionPath, SolutionSnapshot? snapshot = null)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException("Solution path must not be empty.  解决方案路径不能为空。", nameof(solutionPath));
        }

        CurrentSolutionPath = Path.GetFullPath(solutionPath);
        RootDirectory = Path.ChangeExtension(CurrentSolutionPath, null);
        SolutionName = Path.GetFileName(RootDirectory);
        NodeProjectPath = Path.Combine(RootDirectory, SolutionName + ".uotannode");
        StateDirectory = Path.Combine(RootDirectory, ".uotan");
        Directory.CreateDirectory(StateDirectory);

        // Build sub-project list from snapshot or by scanning the root directory
        var subProjects = new List<SubProjectContext>();

        if (snapshot?.SubProjects is { Count: > 0 } spList)
        {
            // v2+ : use explicit sub-project references from the snapshot
            foreach (var dto in spList)
            {
                var fwPath = Path.Combine(RootDirectory, dto.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fwPath)) continue;
                var subRoot = Path.GetDirectoryName(fwPath)!;
                subProjects.Add(new SubProjectContext(
                    dto.Name,
                    subRoot,
                    fwPath,
                    Path.Combine(subRoot, "images"),
                    Path.Combine(subRoot, "output")));
            }
        }
        else if (snapshot is not null && !string.IsNullOrEmpty(snapshot.FirmwareProjectPath))
        {
            // v1 legacy: single firmware project — auto-migrate to sub-project
            var fwPath = Path.Combine(RootDirectory, snapshot.FirmwareProjectPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fwPath))
            {
                LegacyFirmwareProjectPath = fwPath;
                var subRoot = Path.GetDirectoryName(fwPath)!;
                var subName = Path.GetFileName(subRoot);
                subProjects.Add(new SubProjectContext(
                    subName,
                    subRoot,
                    fwPath,
                    Path.Combine(subRoot, "images"),
                    Path.Combine(subRoot, "output")));
            }
        }
        else
        {
            // Fallback: scan root directory for *.uotanfw files
            foreach (var fwFile in Directory.EnumerateFiles(RootDirectory, "*.uotanfw"))
            {
                var subRoot = Path.GetDirectoryName(fwFile)!;
                var subName = Path.GetFileName(subRoot);
                subProjects.Add(new SubProjectContext(
                    subName,
                    subRoot,
                    fwFile,
                    Path.Combine(subRoot, "images"),
                    Path.Combine(subRoot, "output")));
            }
        }

        SubProjects = subProjects;
        ActiveSubProject = subProjects.FirstOrDefault();
    }

    /// <summary>
    /// Closes the current solution, clearing all state.
    /// <para>关闭当前解决方案，清空全部状态。</para>
    /// </summary>
    public void CloseProject()
    {
        CurrentSolutionPath = null;
        SolutionName = null;
        RootDirectory = null;
        LegacyFirmwareProjectPath = null;
        NodeProjectPath = null;
        StateDirectory = null;
        SubProjects = [];
        ActiveSubProject = null;
    }

    /// <summary>
    /// Ensures the active sub-project's <c>images/</c> directory exists and returns its path.
    /// <para>确保当前活动子项目的 <c>images/</c> 目录存在并返回其路径。</para>
    /// </summary>
    public string EnsureImagesDirectory()
    {
        if (ActiveSubProject is null)
        {
            throw new InvalidOperationException("No solution is open or no sub-project is active.  尚未打开解决方案或无活动子项目。");
        }

        Directory.CreateDirectory(ActiveSubProject.ImagesDir);
        return ActiveSubProject.ImagesDir;
    }

    /// <summary>
    /// Ensures the active sub-project's <c>output/</c> directory exists and returns its path.
    /// <para>确保当前活动子项目的 <c>output/</c> 目录存在并返回其路径。</para>
    /// </summary>
    public string EnsureOutputDirectory()
    {
        if (ActiveSubProject is null)
        {
            throw new InvalidOperationException("No solution is open or no sub-project is active.  尚未打开解决方案或无活动子项目。");
        }

        Directory.CreateDirectory(ActiveSubProject.OutputDir);
        return ActiveSubProject.OutputDir;
    }

    /// <summary>
    /// Resolves a solution-root-relative path to an absolute path; returns <c>null</c> when no solution is open.
    /// <para>将相对解决方案根目录的路径解析为绝对路径；未打开解决方案时返回 <c>null</c>。</para>
    /// </summary>
    public string? ResolvePath(string relativePath)
    {
        if (RootDirectory is null || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(RootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// Converts an absolute path to a solution-root-relative path; returns <c>null</c> when the path is
    /// outside the solution root or no solution is open.
    /// <para>将绝对路径转换为相对解决方案根目录的路径；路径不在解决方案根目录下或未打开解决方案时返回 <c>null</c>。</para>
    /// </summary>
    public string? RelativePath(string absolutePath)
    {
        if (RootDirectory is null || string.IsNullOrWhiteSpace(absolutePath))
        {
            return null;
        }

        var full = Path.GetFullPath(absolutePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootDirectory));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
    }
}