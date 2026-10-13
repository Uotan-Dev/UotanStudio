namespace UotanStudio.Services.Firmware;

/// <summary>
/// Directory helpers for new projects: fresh-target check and default-name probing.
/// <para>新建项目的目录辅助：全新目录判定与默认名称探测。</para>
/// </summary>
public static class NewProjectDirectoryHelper
{
    /// <summary>
    /// Whether the directory is missing or empty — new solutions/sub-projects require a fresh directory.
    /// <para>判断目录是否「不存在或为空」——新建解决方案/子项目要求目标目录全新。</para>
    /// </summary>
    /// <param name="path">Absolute directory path; blank is invalid (false). <para>目录绝对路径；空白视为不合法（false）。</para></param>
    /// <returns>True when the directory does not exist or has no entries. <para>目录不存在或不含任何条目时 true。</para></returns>
    public static bool IsDirectoryEmptyOrMissing(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (File.Exists(path)) return false;
        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Probes the first free default name under the base directory, treating reserved names as taken.
    /// <para>在基础目录下探测第一个空闲默认名称，保留名称视为已占用。</para>
    /// </summary>
    /// <param name="baseDirectory">Base directory to probe (may not exist). <para>探测的基础目录（可以不存在）。</para></param>
    /// <param name="reservedNames">
    /// Additional taken names (case-insensitive); may be null.
    /// <para>额外占用的名称集合（不区分大小写）；可为空。</para>
    /// </param>
    /// <returns>The first free name. <para>第一个空闲名称。</para></returns>
    public static string GetNextDefaultName(string baseDirectory, IEnumerable<string>? reservedNames = null)
    {
        var reserved = new HashSet<string>(
            reservedNames ?? [],
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var i = 1; i <= 9999; i++)
        {
            var candidate = $"NewProject{i}";
            if (reserved.Contains(candidate))
            {
                continue;
            }

            var fullPath = string.IsNullOrWhiteSpace(baseDirectory) ? candidate : Path.Combine(baseDirectory, candidate);
            if (!Directory.Exists(fullPath))
            {
                return candidate;
            }
        }

        return $"NewProject_{DateTime.Now:yyyyMMddHHmmss}";
    }
}