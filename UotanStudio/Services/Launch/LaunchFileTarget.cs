namespace UotanStudio.Services.Launch;

/// <summary>How a desktop launch opens one path.<para>桌面启动为单个路径选择的打开方式。</para></summary>
public enum LaunchFileKind
{
    /// <summary>The path is a solution that opens as a project.<para>路径为按工程打开的解决方案。</para></summary>
    Solution,

    /// <summary>The path is a firmware project file inside a solution.<para>路径为解决方案中的固件子工程文件。</para></summary>
    FirmwareProject,

    /// <summary>The path is a node project file inside a solution.<para>路径为解决方案中的节点工程文件。</para></summary>
    NodeProject,

    /// <summary>The path opens as a workspace document.<para>路径按工作区文档打开。</para></summary>
    Document,
}

/// <summary>
/// One path from a desktop launch classified into the action that opens it, including the owning
/// solution of a standalone project file.
/// <para>把桌面启动中的一个路径归类为打开动作，并给出独立工程文件所属的解决方案。</para>
/// </summary>
/// <param name="Kind">The action kind.<para>打开动作类型。</para></param>
/// <param name="Path">The absolute path.<para>绝对路径。</para></param>
/// <param name="OwningSolution">The owning solution for project files, otherwise null.<para>工程文件所属的解决方案；其它文件为 null。</para></param>
/// <param name="ProjectName">The registered project name for project files, otherwise null.<para>工程文件登记的工程名；其它文件为 null。</para></param>
public sealed record LaunchFileTarget(LaunchFileKind Kind, string Path, string? OwningSolution, string? ProjectName)
{
    private const int MaxAncestorDepth = 32;

    /// <summary>
    /// Classifies one launch path by extension and, for standalone project files, by the solution that owns it.
    /// <para>按扩展名归类一个启动路径；独立工程文件还按其所属的解决方案归类。</para>
    /// </summary>
    /// <param name="path">The launch path.<para>启动路径。</para></param>
    /// <returns>The classified target.<para>归类结果。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank or invalid.<para>路径为空或非法。</para></exception>
    public static LaunchFileTarget Classify(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var extension = System.IO.Path.GetExtension(fullPath);
        if (string.Equals(extension, ".uotan", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchFileTarget(LaunchFileKind.Solution, fullPath, fullPath, null);
        }

        if (string.Equals(extension, ".uotanfw", StringComparison.OrdinalIgnoreCase))
        {
            return ClassifyProjectFile(LaunchFileKind.FirmwareProject, fullPath);
        }

        if (string.Equals(extension, ".uotannode", StringComparison.OrdinalIgnoreCase))
        {
            return ClassifyProjectFile(LaunchFileKind.NodeProject, fullPath);
        }

        return new LaunchFileTarget(LaunchFileKind.Document, fullPath, null, null);
    }

    /// <summary>
    /// Finds the single solution that owns a project file by walking up from its directory.
    /// <para>从工程文件所在目录逐级向上查找拥有它的唯一解决方案。</para>
    /// </summary>
    /// <param name="path">The project file path.<para>工程文件路径。</para></param>
    /// <returns>The owning solution path, or null when it is absent or ambiguous.<para>所属解决方案路径；不存在或不唯一时为 null。</para></returns>
    public static string? FindOwningSolution(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        for (var depth = 0; directory is not null && depth < MaxAncestorDepth; depth++)
        {
            var solutions = EnumerateSolutions(directory);
            if (solutions.Count == 1)
            {
                return solutions[0];
            }

            if (solutions.Count > 1)
            {
                return null;
            }

            directory = System.IO.Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static LaunchFileTarget ClassifyProjectFile(LaunchFileKind kind, string fullPath)
    {
        var solution = FindOwningSolution(fullPath);
        return solution is null
            ? new LaunchFileTarget(LaunchFileKind.Document, fullPath, null, null)
            : new LaunchFileTarget(kind, fullPath, solution, System.IO.Path.GetFileNameWithoutExtension(fullPath));
    }

    private static List<string> EnumerateSolutions(string directory)
    {
        try
        {
            return [.. Directory.EnumerateFiles(directory, "*.uotan", SearchOption.TopDirectoryOnly)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }
}