using System;
using System.IO;

namespace UotanStudio.Services.Projects;

/// <summary>
/// Current solution context service (singleton), holding the state and directory layout of the opened
/// <c>.uotan</c> solution: data root, firmware/node project files, session state directory <c>.uotan/</c>,
/// image directory <c>images/</c>, intermediate directory <c>intermediate/</c>, and relative/absolute path
/// conversion.
/// <para>当前解决方案上下文服务（单例），承载已打开 <c>.uotan</c> 解决方案的状态与目录布局：
/// 数据根目录、固件/节点项目文件、会话状态目录 <c>.uotan/</c>、镜像目录 <c>images/</c>、
/// 中间产物目录 <c>intermediate/</c> 及相对/绝对路径转换。</para>
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
    /// Firmware project file path (<c>RootDirectory/&lt;SolutionName&gt;.uotanfw</c>); <c>null</c> when no solution is open.
    /// <para>固件项目文件路径（<c>RootDirectory/&lt;SolutionName&gt;.uotanfw</c>）；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    public string? FirmwareProjectPath { get; private set; }

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
    /// Image directory (<c>RootDirectory/images</c>); <c>null</c> when no solution is open.
    /// <para>镜像目录（<c>RootDirectory/images</c>）；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    public string? ImagesDirectory { get; private set; }

    /// <summary>
    /// Intermediate output directory (<c>RootDirectory/intermediate</c>); <c>null</c> when no solution is open.
    /// <para>中间产物目录（<c>RootDirectory/intermediate</c>）；未打开解决方案时为 <c>null</c>。</para>
    /// </summary>
    public string? IntermediateDirectory { get; private set; }

    /// <summary>
    /// Whether a solution is currently open.
    /// <para>是否已打开解决方案。</para>
    /// </summary>
    public bool IsProjectOpen => CurrentSolutionPath is not null;

    /// <summary>
    /// Opens the specified solution: sets the solution path/name and directory layout, ensuring the
    /// <c>images/</c>, <c>intermediate/</c> and <c>.uotan/</c> directories exist.
    /// <para>打开指定解决方案：设置解决方案路径/名称与目录布局，并确保 <c>images/</c>、<c>intermediate/</c>
    /// 与 <c>.uotan/</c> 目录存在。</para>
    /// </summary>
    /// <param name="solutionPath">
    /// Full path of the solution file (<c>.uotan</c>). <para>解决方案文件（<c>.uotan</c>）全路径。</para>
    /// </param>
    public void OpenSolution(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException("Solution path must not be empty.  解决方案路径不能为空。", nameof(solutionPath));
        }

        CurrentSolutionPath = Path.GetFullPath(solutionPath);
        RootDirectory = Path.ChangeExtension(CurrentSolutionPath, null);
        SolutionName = Path.GetFileName(RootDirectory);
        FirmwareProjectPath = Path.Combine(RootDirectory, SolutionName + ".uotanfw");
        NodeProjectPath = Path.Combine(RootDirectory, SolutionName + ".uotannode");
        StateDirectory = Path.Combine(RootDirectory, ".uotan");
        ImagesDirectory = Path.Combine(RootDirectory, "images");
        IntermediateDirectory = Path.Combine(RootDirectory, "intermediate");
        Directory.CreateDirectory(ImagesDirectory);
        Directory.CreateDirectory(IntermediateDirectory);
        Directory.CreateDirectory(StateDirectory);
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
        FirmwareProjectPath = null;
        NodeProjectPath = null;
        StateDirectory = null;
        ImagesDirectory = null;
        IntermediateDirectory = null;
    }

    /// <summary>
    /// Ensures the image directory exists and returns its path.
    /// <para>确保镜像目录存在并返回其路径。</para>
    /// </summary>
    /// <returns>
    /// The absolute image directory path. <para>镜像目录绝对路径。</para>
    /// </returns>
    public string EnsureImagesDirectory()
    {
        if (ImagesDirectory is null)
        {
            throw new InvalidOperationException("No solution is open.  尚未打开解决方案。");
        }

        Directory.CreateDirectory(ImagesDirectory);
        return ImagesDirectory;
    }

    /// <summary>
    /// Ensures the intermediate sub-directory for the given operation exists and returns its path
    /// (<c>intermediate/&lt;operation&gt;</c>).
    /// <para>确保指定操作的中间产物子目录存在并返回其路径（<c>intermediate/&lt;operation&gt;</c>）。</para>
    /// </summary>
    /// <param name="operation">
    /// Operation name (used as the sub-directory name). <para>操作名（用作子目录名）。</para>
    /// </param>
    /// <returns>
    /// The absolute intermediate sub-directory path. <para>中间产物子目录绝对路径。</para>
    /// </returns>
    public string EnsureIntermediateDirectory(string operation)
    {
        if (IntermediateDirectory is null)
        {
            throw new InvalidOperationException("No solution is open.  尚未打开解决方案。");
        }

        var dir = Path.Combine(IntermediateDirectory, operation);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Resolves a solution-root-relative path to an absolute path; returns <c>null</c> when no solution is open.
    /// <para>将相对解决方案根目录的路径解析为绝对路径；未打开解决方案时返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="relativePath">
    /// Relative path (forward or back slashes both accepted). <para>相对路径（使用正斜杠或反斜杠均可）。</para>
    /// </param>
    /// <returns>
    /// The absolute path; <c>null</c> when no solution is open. <para>绝对路径；未打开解决方案时为 <c>null</c>。</para>
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
    /// Converts an absolute path to a solution-root-relative path; returns <c>null</c> when the path is
    /// outside the solution root or no solution is open.
    /// <para>将绝对路径转换为相对解决方案根目录的路径；路径不在解决方案根目录下或未打开解决方案时返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="absolutePath">
    /// Absolute path. <para>绝对路径。</para>
    /// </param>
    /// <returns>
    /// The relative path (forward slashes); <c>null</c> when it cannot be converted. <para>相对路径（以正斜杠分隔）；无法转换时为 <c>null</c>。</para>
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