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
    string OutputDir)
{
    /// <summary>State owned by this sub-project, independent of the active selection.</summary>
    public string StateDirectory => Path.Combine(RootPath, ".uotan");

    /// <summary>
    /// Delivery output directory for this sub-project.
    /// <para>该子项目用于存放交付输出的 <c>delivery/</c> 目录。</para>
    /// </summary>
    public string DeliveryDirectory => Path.Combine(RootPath, "delivery");

    /// <summary>Filesystem and firmware metadata owned by this sub-project.</summary>
    public string MetadataRoot => Path.Combine(StateDirectory, "metadata");
}

/// <summary>
/// Independent solution context, holding the state and directory layout of the opened
/// <c>.uotan</c> solution: data root, sub-projects, node project file, active sub-project state directory
/// <c>.uotan/</c>, and relative/absolute path conversion.
/// <para>独立解决方案上下文，承载已打开 <c>.uotan</c> 解决方案的状态与目录布局：
/// 数据根目录、子项目列表、节点项目文件、活动子项目状态目录 <c>.uotan/</c> 及相对/绝对路径转换。</para>
/// </summary>
public sealed class ProjectContext
{
    /// <summary>Creates an independent solution context.</summary>
    public ProjectContext() { }

    /// <summary>Raised synchronously after context changes; hosts bridge their environment here.</summary>
    public event Action? Changed;

    /// <summary>
    /// Full path of the current solution file (<c>.uotan</c>); <c>null</c> when no solution is open.
    /// <para>当前解决方案文件（<c>.uotan</c>）全路径；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    public string? CurrentSolutionPath { get; private set; }

    internal void SetCompatibilitySolutionPath(string? path) => CurrentSolutionPath = path;

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
    /// when available; <c>null</c> when no sub-project is active.
    /// <para>固件项目文件路径——优先取 <see cref="ActiveSubProject"/> 的 <c>FwProjectPath</c>；
    /// 没有活动子项目时为 <c>null</c>。</para>
    /// </summary>
    public string? FirmwareProjectPath => ActiveSubProject?.FwProjectPath;


    /// <summary>
    /// Node project file path — lives inside the sub-project whose name equals the solution name,
    /// falling back to the first sub-project; <c>null</c> when no sub-project exists.
    /// <para>节点项目文件路径——位于「名为解决方案名的子项目，否则第一个子项目」文件夹内；
    /// 无子项目时为 <c>null</c>。</para>
    /// </summary>
    public string? NodeProjectPath { get; private set; }

    /// <summary>
    /// State directory of the active sub-project; null when no sub-project is active.
    /// <para>当前活动子项目的 .uotan 目录；没有活动子项目时为 null。</para>
    /// </summary>
    public string? StateDirectory => ActiveSubProject?.StateDirectory;

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
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Opens the specified solution: sets the solution path/name and directory layout, scans for
    /// sub-projects (<c>*.uotanfw</c> files). State belongs to each sub-project.
    /// <para>打开指定解决方案：设置解决方案路径/名称与目录布局，扫描子项目（<c>*.uotanfw</c> 文件），
    /// 状态目录由各子项目独立持有。</para>
    /// </summary>
    /// <param name="solutionPath">
    /// Full path of the solution file (<c>.uotan</c>). <para>解决方案文件（<c>.uotan</c>）全路径。</para>
    /// </param>
    /// <param name="snapshot">
    /// The loaded solution snapshot (used to read sub-project references). <para>已加载的解决方案快照（读取子项目引用）。</para>
    /// </param>
    public void OpenSolution(string solutionPath, SolutionSnapshot? snapshot = null)
    {
        var candidate = new ProjectContext();
        candidate.InitializeSolution(solutionPath, snapshot);
        CurrentSolutionPath = candidate.CurrentSolutionPath;
        SolutionName = candidate.SolutionName;
        RootDirectory = candidate.RootDirectory;
        NodeProjectPath = candidate.NodeProjectPath;
        SubProjects = Array.AsReadOnly(candidate.SubProjects.ToArray());
        ActiveSubProject = candidate.ActiveSubProject;
        Changed?.Invoke();
    }

    private void InitializeSolution(string solutionPath, SolutionSnapshot? snapshot)
    {
        if (snapshot is not null) ProjectValidation.ValidateSolution(snapshot);
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException("Solution path must not be empty.  解决方案路径不能为空。", nameof(solutionPath));
        }

        CurrentSolutionPath = Path.GetFullPath(solutionPath);
        RootDirectory = Path.GetFullPath(Path.GetDirectoryName(CurrentSolutionPath)!);
        SolutionName = Path.GetFileNameWithoutExtension(CurrentSolutionPath);
        Directory.CreateDirectory(RootDirectory);

        // Build sub-project list from snapshot or by scanning the root directory
        var subProjects = new List<SubProjectContext>();

        if (snapshot?.SubProjects is { Count: > 0 } spList)
        {
            // v2+ : use explicit sub-project references from the snapshot
            foreach (var dto in spList)
            {
                var fwPath = Path.Combine(RootDirectory, dto.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fwPath)) continue;
                var subRoot = Path.GetDirectoryName(Path.GetFullPath(fwPath))!;
                if (!ProjectValidation.IsDirectSubProjectDirectory(RootDirectory, subRoot)) continue;
                subProjects.Add(new SubProjectContext(
                    dto.Name,
                    subRoot,
                    fwPath,
                    Path.Combine(subRoot, "images"),
                    Path.Combine(subRoot, "output")));
            }
        }
        else if (snapshot is null)
        {
            // Fallback scan: only a directory directly under the solution root that owns a *.uotanfw
            // forms a sub-project. Directories nested inside another sub-project (or reserved folders)
            // are skipped so a stray inner scaffold is never registered as an independent sub-project.
            var candidates = Directory.EnumerateDirectories(RootDirectory)
                .Where(directory => !IsReservedDirectory(directory)
                    && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                .ToArray();
            foreach (var directory in candidates)
            {
                if (candidates.Any(other => IsStrictlyWithinDirectory(directory, other))) continue;
                foreach (var fwFile in Directory.EnumerateFiles(directory, "*.uotanfw"))
                    subProjects.Add(new(Path.GetFileNameWithoutExtension(fwFile), directory, fwFile,
                        Path.Combine(directory, "images"), Path.Combine(directory, "output")));
            }
        }

        SubProjects = subProjects;
        ActiveSubProject = subProjects.FirstOrDefault();
        // The single node project is owned by the sub-project whose name equals the solution name,
        // falling back to the first sub-project.
        var nodeOwnerRoot = subProjects.FirstOrDefault(s => string.Equals(s.Name, SolutionName, StringComparison.OrdinalIgnoreCase))?.RootPath
            ?? subProjects.FirstOrDefault()?.RootPath;
        NodeProjectPath = nodeOwnerRoot is null ? null : Path.Combine(nodeOwnerRoot, SolutionName + ".uotannode");
        Changed?.Invoke();
    }

    /// <summary>Whether <paramref name="candidate"/> is strictly inside <paramref name="parent"/>.</summary>
    private static bool IsStrictlyWithinDirectory(string candidate, string parent)
    {
        if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase))
            return false;
        var parentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(parentRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReservedDirectory(string path)
        => new[] { "images", "output", "delivery", ".uotan", ".deleted", ".git" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Closes the current solution, clearing all state.
    /// <para>关闭当前解决方案，清空全部状态。</para>
    /// </summary>
    public void CloseProject()
    {
        CurrentSolutionPath = null;
        SolutionName = null;
        RootDirectory = null;
        NodeProjectPath = null;
        SubProjects = [];
        ActiveSubProject = null;
        Changed?.Invoke();
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
    /// Returns the active sub-project's metadata root, or null when no sub-project is active.
    /// <para>返回当前活动子项目的元数据根目录（<c>.uotan/metadata/</c>），无活动子项目时返回 <c>null</c>。
    /// 调用方应将其传递给 <c>RepackMetadata.WriteAsync</c>。</para>
    /// </summary>
    public string? GetMetadataRoot()
    {
        return EnsureMetadataRoot(ActiveSubProject);
    }

    /// <summary>Finds the owner of a path without consulting the active selection.</summary>
    public SubProjectContext? FindSubProject(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return SubProjects.Where(project =>
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootPath));
            return string.Equals(full, root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        }).OrderByDescending(project => project.RootPath.Length).FirstOrDefault();
    }

    /// <summary>Returns the metadata root of the path's owning sub-project; unrelated paths have no project root.</summary>
    public string? GetMetadataRoot(string path) => EnsureMetadataRoot(FindSubProject(path));

    private static string? EnsureMetadataRoot(SubProjectContext? project)
    {
        if (project is null) return null;
        Directory.CreateDirectory(project.MetadataRoot);
        return project.MetadataRoot;
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