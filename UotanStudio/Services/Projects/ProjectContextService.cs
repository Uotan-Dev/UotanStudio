namespace UotanStudio.Services.Projects;

/// <summary>Desktop compatibility context; independent hosts use ProjectContext.</summary>
public sealed class ProjectContextService
{
    public static ProjectContextService Instance { get; } = new();
    internal ProjectContext Core { get; } = new();
    private ProjectContextService() => Core.Changed += () => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.DefaultMetadataRoot = Core.GetMetadataRoot();
    public string? CurrentSolutionPath => Core.CurrentSolutionPath;
    public string? SolutionName => Core.SolutionName;
    public string? RootDirectory => Core.RootDirectory;
    public string? FirmwareProjectPath => Core.FirmwareProjectPath;
    public string? NodeProjectPath => Core.NodeProjectPath;
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
    /// <summary>Finds metadata by the owning path, independent of which sub-project is selected.</summary>
    public string? GetMetadataRoot(string path) => Core.GetMetadataRoot(path);
    /// <summary>Captures a fixed metadata context for one source or extraction directory.</summary>
    public UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadataContext CaptureMetadataContext(string path)
        => GetMetadataRoot(path) is { } root
            ? UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadataContext.FromProjectRoot(root)
            : UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadataContext.LocalDirectory;
    public string? ResolvePath(string path) => Core.ResolvePath(path);
    public string? RelativePath(string path) => Core.RelativePath(path);
}