using System;
using System.IO;

namespace UotanStudio.Services;

/// <summary>
/// <para>当前工程上下文服务（单例），承载已打开 <c>.uotan</c> 工程的状态与目录布局：
/// 数据根目录、镜像目录 <c>images/</c>、中间产物目录 <c>intermediate/</c> 及相对/绝对路径转换。</para>
/// Current project context service (singleton), holding the state and directory layout of the
/// opened <c>.uotan</c> project: data root, image directory <c>images/</c>, intermediate
/// directory <c>intermediate/</c>, and relative/absolute path conversion.
/// </summary>
public sealed class ProjectContextService
{
    private static readonly Lazy<ProjectContextService> LazyInstance = new(() => new ProjectContextService());

    /// <summary>
    /// <para>获取工程上下文单例实例。</para>
    /// Gets the singleton instance of the project context service.
    /// </summary>
    public static ProjectContextService Instance => LazyInstance.Value;

    private ProjectContextService()
    {
    }

    /// <summary>
    /// <para>当前工程文件（<c>.uotan</c>）全路径；未打开工程时为 <c>null</c>。</para>
    /// Full path of the current project file (<c>.uotan</c>); <c>null</c> when no project is open.
    /// </summary>
    public string? CurrentProjectPath { get; private set; }

    /// <summary>
    /// <para>工程名（不含扩展名）；未打开工程时为 <c>null</c>。</para>
    /// Project name (without extension); <c>null</c> when no project is open.
    /// </summary>
    public string? ProjectName { get; private set; }

    /// <summary>
    /// <para>数据根目录（与工程文件同名的目录）；未打开工程时为 <c>null</c>。</para>
    /// Data root directory (a directory sharing the project file's base name); <c>null</c> when no project is open.
    /// </summary>
    public string? RootDirectory { get; private set; }

    /// <summary>
    /// <para>镜像目录（<c>RootDirectory/images</c>）；未打开工程时为 <c>null</c>。</para>
    /// Image directory (<c>RootDirectory/images</c>); <c>null</c> when no project is open.
    /// </summary>
    public string? ImagesDirectory { get; private set; }

    /// <summary>
    /// <para>中间产物目录（<c>RootDirectory/intermediate</c>）；未打开工程时为 <c>null</c>。</para>
    /// Intermediate output directory (<c>RootDirectory/intermediate</c>); <c>null</c> when no project is open.
    /// </summary>
    public string? IntermediateDirectory { get; private set; }

    /// <summary>
    /// <para>是否已打开工程。</para>
    /// Whether a project is currently open.
    /// </summary>
    public bool IsProjectOpen => CurrentProjectPath is not null;

    /// <summary>
    /// <para>打开指定工程：设置工程路径/名称与目录布局，并确保 <c>images/</c>、<c>intermediate/</c> 存在。</para>
    /// Opens the specified project: sets the project path/name and directory layout, ensuring the
    /// <c>images/</c> and <c>intermediate/</c> directories exist.
    /// </summary>
    /// <param name="projectPath">
    /// <para>工程文件（<c>.uotan</c>）全路径。</para>
    /// Full path of the project file (<c>.uotan</c>).
    /// </param>
    public void OpenProject(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path must not be empty. 工程路径不能为空。", nameof(projectPath));
        }

        CurrentProjectPath = Path.GetFullPath(projectPath);
        RootDirectory = Path.ChangeExtension(CurrentProjectPath, null);
        ProjectName = Path.GetFileName(RootDirectory);
        ImagesDirectory = Path.Combine(RootDirectory, "images");
        IntermediateDirectory = Path.Combine(RootDirectory, "intermediate");
        Directory.CreateDirectory(ImagesDirectory);
        Directory.CreateDirectory(IntermediateDirectory);
    }

    /// <summary>
    /// <para>关闭当前工程，清空全部状态。</para>
    /// Closes the current project, clearing all state.
    /// </summary>
    public void CloseProject()
    {
        CurrentProjectPath = null;
        ProjectName = null;
        RootDirectory = null;
        ImagesDirectory = null;
        IntermediateDirectory = null;
    }

    /// <summary>
    /// <para>确保镜像目录存在并返回其路径。</para>
    /// Ensures the image directory exists and returns its path.
    /// </summary>
    /// <returns>
    /// <para>镜像目录绝对路径。</para>
    /// The absolute image directory path.
    /// </returns>
    public string EnsureImagesDirectory()
    {
        if (ImagesDirectory is null)
        {
            throw new InvalidOperationException("No project is open. 尚未打开工程。");
        }

        Directory.CreateDirectory(ImagesDirectory);
        return ImagesDirectory;
    }

    /// <summary>
    /// <para>确保指定操作的中间产物子目录存在并返回其路径（<c>intermediate/&lt;operation&gt;</c>）。</para>
    /// Ensures the intermediate sub-directory for the given operation exists and returns its path
    /// (<c>intermediate/&lt;operation&gt;</c>).
    /// </summary>
    /// <param name="operation">
    /// <para>操作名（用作子目录名）。</para>
    /// Operation name (used as the sub-directory name).
    /// </param>
    /// <returns>
    /// <para>中间产物子目录绝对路径。</para>
    /// The absolute intermediate sub-directory path.
    /// </returns>
    public string EnsureIntermediateDirectory(string operation)
    {
        if (IntermediateDirectory is null)
        {
            throw new InvalidOperationException("No project is open. 尚未打开工程。");
        }

        var dir = Path.Combine(IntermediateDirectory, operation);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// <para>将相对工程根目录的路径解析为绝对路径；未打开工程时返回 <c>null</c>。</para>
    /// Resolves a project-root-relative path to an absolute path; returns <c>null</c> when no project is open.
    /// </summary>
    /// <param name="relativePath">
    /// <para>相对路径（使用正斜杠或反斜杠均可）。</para>
    /// Relative path (forward or back slashes both accepted).
    /// </param>
    /// <returns>
    /// <para>绝对路径；未打开工程时为 <c>null</c>。</para>
    /// The absolute path; <c>null</c> when no project is open.
    /// </returns>
    public string? ResolvePath(string relativePath)
    {
        if (RootDirectory is null || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(RootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// <para>将绝对路径转换为相对工程根目录的路径；路径不在工程根目录下或未打开工程时返回 <c>null</c>。</para>
    /// Converts an absolute path to a project-root-relative path; returns <c>null</c> when the path is
    /// outside the project root or no project is open.
    /// </summary>
    /// <param name="absolutePath">
    /// <para>绝对路径。</para>
    /// Absolute path.
    /// </param>
    /// <returns>
    /// <para>相对路径（以正斜杠分隔）；无法转换时为 <c>null</c>。</para>
    /// The relative path (forward slashes); <c>null</c> when it cannot be converted.
    /// </returns>
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