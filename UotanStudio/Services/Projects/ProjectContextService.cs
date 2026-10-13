namespace UotanStudio.Services.Projects;

/// <summary>Desktop compatibility context wrapping the shared project context.
/// <para>包装共享项目上下文的桌面兼容上下文。</para></summary>
public sealed class ProjectContextService
{
    public static ProjectContextService Instance { get; } = new();
    internal ProjectContext Core { get; } = new();
    private ProjectContextService() => Core.Changed += () => UotanStudio.Firmware.Core.Repacking.RepackMetadata.DefaultMetadataRoot = Core.GetMetadataRoot();
    public string? CurrentSolutionPath => Core.CurrentSolutionPath;
    public string? SolutionName => Core.SolutionName;
    public string? RootDirectory => Core.RootDirectory;
    public string? FirmwareProjectPath => Core.FirmwareProjectPath;
    public IReadOnlyList<NodeProjectContext> NodeProjects => Core.NodeProjects;
    public NodeProjectContext? ActiveNodeProject => Core.ActiveNodeProject;
    public string? NodeProjectsDirectory => Core.NodeProjectsDirectory;
    public string? ResolveNodeProjectPath(NodeProjectContext nodeProject) => Core.ResolveNodeProjectPath(nodeProject);
    public bool SetActiveNodeProject(string name) => Core.SetActiveNodeProject(name);
    public string? StateDirectory => Core.StateDirectory;
    public IReadOnlyList<SubProjectContext> SubProjects => Core.SubProjects;
    public SubProjectContext? ActiveSubProject => Core.ActiveSubProject;
    public bool IsProjectOpen => Core.IsProjectOpen;
    public bool SetActiveSubProject(string name) => Core.SetActiveSubProject(name);
    public void OpenSolution(string path, SolutionSnapshot? snapshot = null) => Core.OpenSolution(path, snapshot);
    public void CloseProject() => Core.CloseProject();
    public string EnsureImagesDirectory() => Core.EnsureImagesDirectory();
    public string EnsureOutputDirectory() => Core.EnsureOutputDirectory();
    public string? GetMetadataRoot() => Core.GetMetadataRoot();
    /// <summary>Finds metadata by the owning path, independent of which sub-project is selected.
    /// <para>按所属路径查找元数据根目录，与所选子项目无关。</para></summary>
    /// <param name="path">The path whose owning project supplies the root.<para>由其所属项目提供根目录的路径。</para></param>
    /// <returns>The owning metadata root, or <see langword="null"/> when no project owns the path.
    /// <para>所属元数据根目录；没有项目拥有该路径时为 <see langword="null"/>。</para></returns>
    public string? GetMetadataRoot(string path) => Core.GetMetadataRoot(path);
    /// <summary>Captures a fixed metadata context for one source or extraction directory.
    /// <para>为单个源目录或解压目录捕获固定的元数据上下文。</para></summary>
    /// <param name="path">The source or extraction directory to resolve.<para>要解析的源目录或解压目录。</para></param>
    /// <returns>A project-root context when an owning root exists, otherwise a local-directory context.
    /// <para>存在所属根目录时返回项目根上下文，否则返回本地目录上下文。</para></returns>
    public UotanStudio.Firmware.Core.Repacking.RepackMetadataContext CaptureMetadataContext(string path)
        => GetMetadataRoot(path) is { } root
            ? UotanStudio.Firmware.Core.Repacking.RepackMetadataContext.FromProjectRoot(root)
            : UotanStudio.Firmware.Core.Repacking.RepackMetadataContext.LocalDirectory;
    public string? ResolvePath(string path) => Core.ResolvePath(path);
    public string? RelativePath(string path) => Core.RelativePath(path);
}