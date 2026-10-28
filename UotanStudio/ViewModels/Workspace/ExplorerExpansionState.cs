using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// Captures and restores the project explorer's expanded directories as project-root-relative paths, so the
/// expansion state can travel with a solution file.
/// <para>按工程根相对路径捕获与恢复项目资源管理器的已展开目录，使展开状态可以随解决方案文件保存。</para>
/// </summary>
internal static class ExplorerExpansionState
{
    /// <summary>
    /// Lists the expanded directories below <paramref name="rootDirectory"/> as <c>/</c>-separated relative
    /// paths sorted ordinally; collapsed, placeholder and out-of-root nodes are skipped.
    /// <para>按序数排序列出 <paramref name="rootDirectory"/> 下已展开目录的 <c>/</c> 分隔相对路径；
    /// 折叠节点、占位节点与根外节点不会列出。</para>
    /// </summary>
    /// <param name="roots">Explorer tree roots to walk. <para>要遍历的资源管理器树根。</para></param>
    /// <param name="rootDirectory">Directory the stored paths are relative to. <para>存储路径所相对的目录。</para></param>
    /// <returns>The sorted relative paths; empty when nothing is expanded. <para>排序后的相对路径；无展开项时为空列表。</para></returns>
    internal static List<string> Capture(IEnumerable<ExplorerNode> roots, string rootDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        List<string> paths = [];
        foreach (var node in roots)
        {
            Visit(node);
        }
        paths.Sort(StringComparer.Ordinal);
        return paths;

        void Visit(ExplorerNode node)
        {
            if (!node.IsDirectory || node.IsPlaceholder)
            {
                return;
            }

            if (node.IsExpanded && TryGetRelativePath(node.Path, root) is { Length: > 0 } relative)
            {
                paths.Add(relative);
            }

            foreach (var child in node.Children)
            {
                Visit(child);
            }
        }
    }

    /// <summary>
    /// Expands every captured directory again, loading the lazily scanned children along each route; paths outside
    /// <paramref name="rootDirectory"/> and paths that no longer exist are ignored.
    /// <para>重新展开每个已捕获目录，并沿途加载惰性扫描的子节点；根目录之外与已不存在的路径会被忽略。</para>
    /// </summary>
    /// <param name="roots">Explorer tree roots to walk. <para>要遍历的资源管理器树根。</para></param>
    /// <param name="rootDirectory">Directory the stored paths are relative to. <para>存储路径所相对的目录。</para></param>
    /// <param name="relativePaths">Stored relative paths; empty leaves the tree untouched.
    /// <para>已存储的相对路径；为空时不改变树。</para></param>
    /// <returns>A task that completes after every reachable path is expanded and loaded.
    /// <para>每个可达路径都完成展开与加载后完成的任务。</para></returns>
    internal static async Task RestoreAsync(IEnumerable<ExplorerNode> roots, string rootDirectory, IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        List<string> targets = [];
        foreach (var relative in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            string combined;
            try
            {
                combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (IsWithinRoot(combined, root) && Directory.Exists(combined))
            {
                targets.Add(combined);
            }
        }

        foreach (var target in targets.OrderBy(path => path.Length))
        {
            foreach (var node in roots.ToArray())
            {
                if (IsWithinRoot(target, Path.TrimEndingDirectorySeparator(Path.GetFullPath(node.Path))))
                {
                    await ExpandToAsync(node, target).ConfigureAwait(true);
                }
            }
        }
    }

    private static async Task ExpandToAsync(ExplorerNode node, string target)
    {
        if (!IsWithinRoot(target, node.Path))
        {
            return;
        }

        node.IsExpanded = true;
        if (string.Equals(Path.TrimEndingDirectorySeparator(node.Path), target, PathComparison))
        {
            return;
        }

        await node.EnsureChildrenLoadedAsync().ConfigureAwait(true);
        foreach (var child in node.Children.ToArray())
        {
            if (child is { IsDirectory: true, IsPlaceholder: false } && IsWithinRoot(target, child.Path))
            {
                await ExpandToAsync(child, target).ConfigureAwait(true);
            }
        }
    }

    private static string? TryGetRelativePath(string path, string root)
    {
        if (!IsWithinRoot(path, root) || string.Equals(Path.TrimEndingDirectorySeparator(path), root, PathComparison))
        {
            return null;
        }

        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var container = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(candidate, container, PathComparison)
            || candidate.StartsWith(container + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}