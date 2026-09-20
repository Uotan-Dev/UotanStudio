namespace UotanStudio.Services.Firmware;

/// <summary>
/// <para>新建项目流程目录辅助：空目录判定与默认名称探测。容器结构识别与条目枚举已迁移到
/// <c>UotanStudio.Firmware.Application.Containers.ContainerStructureAnalyzer</c>。</para>
/// New-project directory helpers: empty-directory check and default-name probing. Container structure
/// detection and entry enumeration moved to
/// <c>UotanStudio.Firmware.Application.Containers.ContainerStructureAnalyzer</c>.
/// </summary>
public static class NewProjectDirectoryHelper
{
    /// <summary>
    /// <para>判断目录是否「不存在或为空」——新建解决方案/子项目要求目标目录全新。</para>
    /// Whether the directory is missing or empty — new solutions/sub-projects require a fresh directory.
    /// </summary>
    /// <param name="path"><para>目录绝对路径；空白视为不合法（false）。</para>Absolute directory path; blank is invalid (false).</param>
    /// <returns><para>目录不存在或不含任何条目时 true。</para>True when the directory does not exist or has no entries.</returns>
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
            // 目录不可读视为不满足「空目录」要求。
            // An unreadable directory does not satisfy the empty-directory requirement.
            return false;
        }
    }

    /// <summary>
    /// <para>在基础目录下探测第一个空闲的默认名称（NewProject1、NewProject2…）；
    /// <paramref name="reservedNames"/> 中的名称（如已存在的子项目）同样视为占用。</para>
    /// Probes the first free default name (NewProject1, NewProject2, …) under the base directory;
    /// names in <paramref name="reservedNames"/> (e.g. existing sub-projects) are treated as taken.
    /// </summary>
    /// <param name="baseDirectory"><para>探测的基础目录（可以不存在）。</para>Base directory to probe (may not exist).</param>
    /// <param name="reservedNames">
    /// <para>额外占用的名称集合（不区分大小写）；可为空。</para>
    /// Additional taken names (case-insensitive); may be null.
    /// </param>
    /// <returns><para>第一个空闲名称。</para>The first free name.</returns>
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

        // 兜底：加时间戳后缀，保证唯一。
        // Fallback: timestamp suffix guarantees uniqueness.
        return $"NewProject_{DateTime.Now:yyyyMMddHHmmss}";
    }
}