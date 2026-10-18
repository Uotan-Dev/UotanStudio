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
    /// <summary>
    /// State owned by this sub-project, independent of the active selection.
    /// <para>该子项目独立拥有的状态目录，与当前选中项无关。</para>
    /// </summary>
    /// <value>Absolute path of the sub-project's <c>.uotan</c> directory.
    /// <para>子项目 <c>.uotan</c> 目录的绝对路径。</para></value>
    public string StateDirectory => Path.Combine(RootPath, ".uotan");

    /// <summary>
    /// Delivery output directory for this sub-project.
    /// <para>该子项目用于存放交付输出的 <c>delivery/</c> 目录。</para>
    /// </summary>
    /// <value>Absolute path of the sub-project's <c>delivery</c> directory.
    /// <para>子项目 <c>delivery</c> 目录的绝对路径。</para></value>
    public string DeliveryDirectory => Path.Combine(RootPath, "delivery");

    /// <summary>
    /// Filesystem and firmware metadata owned by this sub-project.
    /// <para>该子项目独立持有的文件系统与固件元数据。</para>
    /// </summary>
    /// <value>Absolute path of <c>.uotan/metadata</c> inside this sub-project; the directory is created by
    /// <see cref="ProjectContext.GetMetadataRoot(string)"/> rather than by this property.
    /// <para>该子项目内 <c>.uotan/metadata</c> 的绝对路径；目录由 <see cref="ProjectContext.GetMetadataRoot(string)"/>
    /// 负责创建，本属性不做创建。</para></value>
    public string MetadataRoot => Path.Combine(StateDirectory, "metadata");
}

/// <summary>
/// Per-node-project context: a registered <c>.uotannode</c> file of the current solution.
/// <para>单个节点项目的上下文：当前解决方案中已登记的 <c>.uotannode</c> 文件。</para>
/// </summary>
/// <param name="Name">Node project display name. <para>节点项目显示名称。</para></param>
/// <param name="RelativePath">Path of the <c>.uotannode</c> file, relative to the solution data root and
/// <c>/</c>-separated. <para><c>.uotannode</c> 文件相对解决方案数据根的路径，以 <c>/</c> 分隔。</para></param>
/// <param name="IsOpen">Whether the editor keeps this node project open. <para>编辑器是否保持该节点项目打开。</para></param>
/// <param name="IsActive">Whether this node project is the editor's active canvas.
/// <para>该节点项目是否为编辑器的活动画布。</para></param>
public sealed record NodeProjectContext(string Name, string RelativePath, bool IsOpen, bool IsActive);

/// <summary>
/// Independent solution context, holding the state and directory layout of the opened
/// <c>.uotan</c> solution: data root, sub-projects, node projects, active sub-project state directory
/// <c>.uotan/</c>, and relative/absolute path conversion.
/// <para>独立解决方案上下文，承载已打开 <c>.uotan</c> 解决方案的状态与目录布局：
/// 数据根目录、子项目列表、节点项目列表、活动子项目状态目录 <c>.uotan/</c> 及相对/绝对路径转换。</para>
/// </summary>
public sealed class ProjectContext
{
    /// <summary>
    /// Creates an independent solution context.
    /// <para>创建一个独立的解决方案上下文。</para>
    /// </summary>
    /// <remarks>The context starts closed: <see cref="IsProjectOpen"/> is <see langword="false"/> until
    /// <see cref="OpenSolution"/> runs. <para>上下文初始为关闭状态：在 <see cref="OpenSolution"/> 执行之前
    /// <see cref="IsProjectOpen"/> 为 <see langword="false"/>。</para></remarks>
    public ProjectContext() { }

    /// <summary>
    /// Raised synchronously after context changes; hosts bridge their environment here.
    /// <para>上下文变更后同步触发；宿主在此桥接其运行环境。</para>
    /// </summary>
    /// <remarks>Handlers run on the thread that performed the change and always see fully applied state:
    /// <see cref="OpenSolution"/> prepares a candidate context and copies it in before raising the event.
    /// <para>处理程序在执行变更的线程上运行，并且总是看到已完整应用的状态：<see cref="OpenSolution"/>
    /// 会先准备候选上下文，复制完成后再触发事件。</para></remarks>
    public event Action? Changed;

    /// <summary>
    /// Full path of the current solution file (<c>.uotan</c>); <c>null</c> when no solution is open.
    /// <para>当前解决方案文件（<c>.uotan</c>）全路径；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    /// <value>The normalized full path produced by <see cref="OpenSolution"/>, and the flag that makes
    /// <see cref="IsProjectOpen"/> true. <para>由 <see cref="OpenSolution"/> 得到的规范化全路径，
    /// 也是使 <see cref="IsProjectOpen"/> 为真的依据。</para></value>
    public string? CurrentSolutionPath { get; private set; }

    internal void SetCompatibilitySolutionPath(string? path) => CurrentSolutionPath = path;

    /// <summary>
    /// Solution name (without extension); <c>null</c> when no solution is open.
    /// <para>解决方案名（不含扩展名）；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    /// <value>The base name used for sub-project matching and script metadata.
    /// <para>用于子项目匹配与脚本元数据的基名。</para></value>
    public string? SolutionName { get; private set; }

    /// <summary>
    /// Data root directory (a directory sharing the solution file's base name); <c>null</c> when no solution is open.
    /// <para>数据根目录（与解决方案文件同名的目录）；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    /// <value>The absolute directory all stored project paths are resolved against by <see cref="ResolvePath"/>
    /// and shortened by <see cref="RelativePath"/>. <para>绝对目录：<see cref="ResolvePath"/> 以其为基准解析存储的
    /// 项目路径，<see cref="RelativePath"/> 则把路径缩短为相对它的路径。</para></value>
    public string? RootDirectory { get; private set; }

    /// <summary>
    /// Firmware project file path — resolves to <see cref="ActiveSubProject"/>.<c>FwProjectPath</c>
    /// when available; <c>null</c> when no sub-project is active.
    /// <para>固件项目文件路径——优先取 <see cref="ActiveSubProject"/> 的 <c>FwProjectPath</c>；
    /// 没有活动子项目时为 <c>null</c>。</para>
    /// </summary>
    /// <value>Absolute path of the active <c>.uotanfw</c> file; the value follows selection changes made by
    /// <see cref="SetActiveSubProject"/>. <para>活动 <c>.uotanfw</c> 文件的绝对路径；取值随
    /// <see cref="SetActiveSubProject"/> 的选择变更。</para></value>
    public string? FirmwareProjectPath => ActiveSubProject?.FwProjectPath;


    /// <summary>
    /// Node projects registered by the current solution, in registration order.
    /// <para>当前解决方案已登记的节点项目清单，按登记顺序排列。</para>
    /// </summary>
    /// <value>A read-only list, empty while no solution is open or the solution registers none. Paths come
    /// from the solution file and are never inferred from the sub-project layout; a <c>.uotannode</c> file
    /// on disk that the solution does not register stays unowned.
    /// <para>只读列表；未打开解决方案或解决方案未登记节点项目时为空。路径全部来自解决方案文件，不再由子项目
    /// 布局推断；磁盘上未被登记的 <c>.uotannode</c> 文件不属于本上下文。</para></value>
    public IReadOnlyList<NodeProjectContext> NodeProjects { get; private set; } = [];

    /// <summary>
    /// The node project the editor presents as active; <c>null</c> when the solution registers none as active.
    /// <para>编辑器呈现为活动的节点项目；没有活动项时为 <c>null</c>。</para>
    /// </summary>
    /// <value>An entry of <see cref="NodeProjects"/> whose <c>IsActive</c> is set; it always follows
    /// <see cref="SetActiveNodeProject"/>. <para><see cref="NodeProjects"/> 中 <c>IsActive</c> 为真的条目；
    /// 随 <see cref="SetActiveNodeProject"/> 变更。</para></value>
    public NodeProjectContext? ActiveNodeProject
        => NodeProjects.FirstOrDefault(node => node.IsActive);

    /// <summary>
    /// Absolute path of the solution's <c>nodes/</c> directory, where node project files live.
    /// <para>解决方案 <c>nodes/</c> 目录的绝对路径，节点项目文件存放于此。</para>
    /// </summary>
    /// <value>Absolute path under <see cref="RootDirectory"/>, or <c>null</c> when no solution is open. The
    /// directory is created when the first node project is written.
    /// <para><see cref="RootDirectory"/> 之下的绝对路径；未打开解决方案时为 <c>null</c>。目录在写入首个节点
    /// 项目时创建。</para></value>
    public string? NodeProjectsDirectory => RootDirectory is null ? null : Path.Combine(RootDirectory, "nodes");

    /// <summary>
    /// State directory of the active sub-project; null when no sub-project is active.
    /// <para>当前活动子项目的 .uotan 目录；没有活动子项目时为 null。</para>
    /// </summary>
    /// <value>Absolute path of <c>&lt;sub-project&gt;/.uotan</c>, where solution state such as the delivery
    /// plan is stored. <para>绝对路径 <c>&lt;子项目&gt;/.uotan</c>，用于存放交付计划等解决方案状态。</para></value>
    public string? StateDirectory => ActiveSubProject?.StateDirectory;

    /// <summary>
    /// All sub-projects discovered in the current solution.
    /// <para>当前解决方案中发现的所有子项目。</para>
    /// </summary>
    /// <value>A read-only list in discovery order, empty while no solution is open; entries whose
    /// <c>.uotanfw</c> file is missing or that are not direct subdirectories are left out.
    /// <para>按发现顺序给出的只读列表，未打开解决方案时为空；<c>.uotanfw</c> 文件缺失或不是直接子目录的条目会被排除。</para></value>
    public IReadOnlyList<SubProjectContext> SubProjects { get; private set; } = [];

    /// <summary>
    /// The active (startup) sub-project used for pack/extract operations; <c>null</c> when no sub-projects exist.
    /// <para>当前活动（启动）子项目，用于打包/解包操作；无子项目时为 <c>null</c>。</para>
    /// </summary>
    /// <value>The sub-project directories <see cref="EnsureImagesDirectory"/>, <see cref="EnsureOutputDirectory"/>
    /// and <see cref="GetMetadataRoot()"/> act on. <para><see cref="EnsureImagesDirectory"/>、
    /// <see cref="EnsureOutputDirectory"/> 与 <see cref="GetMetadataRoot()"/> 所操作的子项目。</para></value>
    public SubProjectContext? ActiveSubProject { get; private set; }

    /// <summary>
    /// Whether a solution is currently open.
    /// <para>是否已打开解决方案。</para>
    /// </summary>
    /// <value><see langword="true"/> between <see cref="OpenSolution"/> and <see cref="CloseProject"/>.
    /// <para>在 <see cref="OpenSolution"/> 与 <see cref="CloseProject"/> 之间为 <see langword="true"/>。</para></value>
    public bool IsProjectOpen => CurrentSolutionPath is not null;

    /// <summary>
    /// Sets the active sub-project by name.
    /// <para>按名称设置活动子项目。</para>
    /// </summary>
    /// <param name="name">Sub-project name. <para>子项目名称。</para></param>
    /// <returns><c>true</c> if the sub-project was found and activated; <c>false</c> otherwise.
    /// <para>找到并激活该子项目时返回 <c>true</c>，否则返回 <c>false</c>。</para></returns>
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
    /// Makes the named node project the editor's active canvas, provided it is registered and open.
    /// <para>把指定节点项目设为编辑器的活动画布，要求其已登记且处于打开状态。</para>
    /// </summary>
    /// <param name="name">Node project name. <para>节点项目名称。</para></param>
    /// <returns><c>true</c> when the project was found and activated; <c>false</c> otherwise.
    /// <para>找到并激活该节点项目时返回 <c>true</c>，否则返回 <c>false</c>。</para></returns>
    public bool SetActiveNodeProject(string name)
    {
        var projects = NodeProjects.ToList();
        var index = projects.FindIndex(node =>
            string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || !projects[index].IsOpen) return false;
        for (var position = 0; position < projects.Count; position++)
            projects[position] = projects[position] with { IsActive = position == index };
        NodeProjects = [.. projects];
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Registers a node project, replacing an existing registration of the same name.
    /// <para>登记一个节点项目，同名既有登记会被替换。</para>
    /// </summary>
    /// <param name="nodeProject">Registration to store. <para>要存储的登记项。</para></param>
    /// <remarks>The first registration of a solution becomes the active canvas.</remarks>
    internal void AddNodeProject(NodeProjectContext nodeProject)
    {
        var projects = NodeProjects.ToList();
        projects.RemoveAll(existing =>
            string.Equals(existing.Name, nodeProject.Name, StringComparison.OrdinalIgnoreCase));
        var registration = projects.Count == 0 && nodeProject.IsOpen
            ? nodeProject with { IsActive = true }
            : nodeProject;
        if (registration.IsActive)
            for (var position = 0; position < projects.Count; position++)
                if (projects[position].IsActive)
                    projects[position] = projects[position] with { IsActive = false };
        projects.Add(registration);
        NodeProjects = [.. projects];
        Changed?.Invoke();
    }

    /// <summary>
    /// Unregisters a node project without touching its file on disk.
    /// <para>注销一个节点项目，但不改动其磁盘文件。</para>
    /// </summary>
    /// <param name="name">Node project name. <para>节点项目名称。</para></param>
    /// <returns><c>true</c> when a registration was removed. <para>确实移除了登记项时返回 <c>true</c>。</para></returns>
    /// <remarks>When the removed entry was active, the first remaining open entry takes over.</remarks>
    internal bool RemoveNodeProject(string name)
    {
        var projects = NodeProjects.ToList();
        var index = projects.FindIndex(node =>
            string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        var wasActive = projects[index].IsActive;
        projects.RemoveAt(index);
        if (wasActive && projects.FindIndex(node => node.IsOpen) is >= 0 and var nextIndex)
            projects[nextIndex] = projects[nextIndex] with { IsActive = true };
        NodeProjects = [.. projects];
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Updates a registration's open and active flags.
    /// <para>更新某个登记项的打开与活动标记。</para>
    /// </summary>
    /// <param name="name">Node project name. <para>节点项目名称。</para></param>
    /// <param name="isOpen">New open state. <para>新的打开状态。</para></param>
    /// <param name="isActive">New active state. <para>新的活动状态。</para></param>
    /// <returns><c>true</c> when the registration was found. <para>找到登记项时返回 <c>true</c>。</para></returns>
    internal bool UpdateNodeProject(string name, bool isOpen, bool isActive)
    {
        var projects = NodeProjects.ToList();
        var index = projects.FindIndex(node =>
            string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        projects[index] = projects[index] with { IsOpen = isOpen, IsActive = isActive && isOpen };
        if (isActive && isOpen)
            for (var position = 0; position < projects.Count; position++)
                if (position != index && projects[position].IsActive)
                    projects[position] = projects[position] with { IsActive = false };
        NodeProjects = [.. projects];
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Resolves a node project registration to an absolute path inside the solution data root.
    /// <para>把节点项目登记项解析为解决方案数据根内的绝对路径。</para>
    /// </summary>
    /// <param name="nodeProject">Registration to resolve. <para>待解析的登记项。</para></param>
    /// <returns>The absolute file path, or <c>null</c> when no solution is open.
    /// <para>绝对文件路径；未打开解决方案时为 <c>null</c>。</para></returns>
    public string? ResolveNodeProjectPath(NodeProjectContext nodeProject)
        => RootDirectory is null ? null : Path.Combine(RootDirectory,
            nodeProject.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Opens the specified solution: sets the solution path/name and directory layout, scans for sub-projects (<c>*.uotanfw</c> files). State belongs to each sub-project.
    /// <para>打开指定解决方案：设置解决方案路径/名称与目录布局，扫描子项目（<c>*.uotanfw</c> 文件）， 状态目录由各子项目独立持有。</para></summary>
    /// <param name="solutionPath">Full path of the solution file (<c>.uotan</c>).
    /// <para>解决方案文件（<c>.uotan</c>）全路径。</para></param>
    /// <param name="snapshot">The loaded solution snapshot (used to read sub-project references).
    /// <para>已加载的解决方案快照（读取子项目引用）。</para></param>
    /// <exception cref="ArgumentException"><paramref name="solutionPath"/> is empty or only whitespace.
    /// <para><paramref name="solutionPath"/> 为空或仅包含空白字符。</para></exception>
    /// <exception cref="InvalidDataException"><paramref name="snapshot"/> carries an unsupported format version, too many sub-projects, a null reference, or a duplicate name.
    /// <para><paramref name="snapshot"/> 的格式版本不受支持、子项目过多、引用为空或名称重复。</para></exception>
    public void OpenSolution(string solutionPath, SolutionSnapshot? snapshot = null)
    {
        var candidate = new ProjectContext();
        candidate.InitializeSolution(solutionPath, snapshot);
        CurrentSolutionPath = candidate.CurrentSolutionPath;
        SolutionName = candidate.SolutionName;
        RootDirectory = candidate.RootDirectory;
        NodeProjects = candidate.NodeProjects;
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

        var subProjects = new List<SubProjectContext>();

        if (snapshot?.SubProjects is { Count: > 0 } spList)
        {
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
        NodeProjects = [.. (snapshot?.NodeProjects ?? []).Select(node => new NodeProjectContext(node.Name, node.RelativePath, node.IsOpen, node.IsActive))];
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
        => ProjectValidation.IsReservedDirectoryName(Path.GetFileName(path).AsSpan());

    /// <summary>
    /// Closes the current solution, clearing all state.
    /// <para>关闭当前解决方案，清空全部状态。</para>
    /// </summary>
    public void CloseProject()
    {
        CurrentSolutionPath = null;
        SolutionName = null;
        RootDirectory = null;
        NodeProjects = [];
        SubProjects = [];
        ActiveSubProject = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Ensures the active sub-project's <c>images/</c> directory exists and returns its path.
    /// <para>确保当前活动子项目的 <c>images/</c> 目录存在并返回其路径。</para>
    /// </summary>
    /// <returns>Absolute path of the created or already existing <c>images/</c> directory.
    /// <para>已创建或早已存在的 <c>images/</c> 目录绝对路径。</para></returns>
    /// <exception cref="InvalidOperationException">No solution is open or no sub-project is active.
    /// <para>尚未打开解决方案或没有活动子项目。</para></exception>
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
    /// <returns>Absolute path of the created or already existing <c>output/</c> directory.
    /// <para>已创建或早已存在的 <c>output/</c> 目录绝对路径。</para></returns>
    /// <exception cref="InvalidOperationException">No solution is open or no sub-project is active.
    /// <para>尚未打开解决方案或没有活动子项目。</para></exception>
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
    /// <returns>The active sub-project's <c>.uotan/metadata</c> directory, created when missing;
    /// <see langword="null"/> when no sub-project is active.
    /// <para>活动子项目的 <c>.uotan/metadata</c> 目录，缺失时会创建；没有活动子项目时为 <see langword="null"/>。</para></returns>
    public string? GetMetadataRoot()
    {
        return EnsureMetadataRoot(ActiveSubProject);
    }

    /// <summary>
    /// Finds the owner of a path without consulting the active selection.
    /// <para>查找路径的归属子项目，不受当前活动选择影响。</para>
    /// </summary>
    /// <param name="path">Absolute or relative path to test; relative paths resolve against the current working
    /// directory, not the solution root. <para>待测试的绝对或相对路径；相对路径按当前工作目录解析，而非解决方案根目录。</para></param>
    /// <returns>The deepest sub-project whose root contains <paramref name="path"/>, or <see langword="null"/>
    /// when the path belongs to none. <para>根目录包含 <paramref name="path"/> 的最深层子项目；路径不属于任何子项目时为
    /// <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or only whitespace.
    /// <para><paramref name="path"/> 为空或仅包含空白字符。</para></exception>
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

    /// <summary>
    /// Returns the metadata root of the path's owning sub-project; unrelated paths have no project root.
    /// <para>返回该路径归属子项目的元数据根目录；与任何子项目无关的路径没有项目根目录。</para>
    /// </summary>
    /// <param name="path">Absolute or relative path to resolve; relative paths resolve against the current
    /// working directory. <para>待解析的绝对或相对路径；相对路径按当前工作目录解析。</para></param>
    /// <returns>The owning sub-project's <c>.uotan/metadata</c> directory, created when missing;
    /// <see langword="null"/> when no sub-project owns <paramref name="path"/>.
    /// <para>归属子项目的 <c>.uotan/metadata</c> 目录，缺失时会创建；没有子项目拥有 <paramref name="path"/> 时为
    /// <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or only whitespace.
    /// <para><paramref name="path"/> 为空或仅包含空白字符。</para></exception>
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
    /// <param name="relativePath">Solution-root-relative path using <c>/</c> separators; absolute paths are
    /// accepted and returned normalized. <para>使用 <c>/</c> 分隔符的相对解决方案根目录路径；也接受绝对路径并原样规范化返回。</para></param>
    /// <returns>The normalized full path, or <see langword="null"/> when no solution is open or
    /// <paramref name="relativePath"/> is empty or only whitespace.
    /// <para>规范化后的全路径；未打开解决方案，或 <paramref name="relativePath"/> 为空、仅含空白时返回
    /// <see langword="null"/>。</para></returns>
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
    /// <param name="absolutePath">Path to shorten; it is normalized with <c>Path.GetFullPath</c> before comparison
    /// against the root, ignoring case on every platform.
    /// <para>待缩短的路径；会先用 <c>Path.GetFullPath</c> 规范化，再在各平台上忽略大小写地与根目录比较。</para></param>
    /// <returns>The root-relative path with <c>/</c> separators, or <see langword="null"/> when no solution is
    /// open, the input is empty or only whitespace, or the path is outside the root.
    /// <para>使用 <c>/</c> 分隔符的相对根目录路径；未打开解决方案、输入为空或仅含空白，或路径位于根目录之外时返回
    /// <see langword="null"/>。</para></returns>
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