using System.Text.RegularExpressions;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Builds the bounded-context dependency graph of <c>UotanStudio.Firmware</c> from source text, so
/// architecture rules can be checked without loading the compiled assembly.
/// <para>从源码文本构建 <c>UotanStudio.Firmware</c> 的限界上下文依赖图，无需加载已编译程序集即可校验架构规则。</para>
/// </summary>
public static class FirmwareContextDependencyScanner
{
    private const string BoundedContextsMarker = "BoundedContexts/";
    private const string ProjectMarker = "UotanStudio.Firmware/";

    private static readonly Regex ContextReferencePattern = new(@"BoundedContexts\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
    private static readonly Regex RegionReferencePattern = new(@"UotanStudio\.Firmware\.(Application|Core|Infrastructure)\b", RegexOptions.CultureInvariant);
    private static readonly Regex FlatReferencePattern = new(@"UotanStudio\.Firmware\.(?!BoundedContexts\b)([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant);
    private static readonly Regex NamespaceDeclarationPattern = new(@"^(?:internal\s+)?(?:public\s+)?(?:file-scoped\s+)?(?:static\s+)?(?:partial\s+)?namespace\s", RegexOptions.CultureInvariant);

    /// <summary>
    /// Gets the non-context region names that sit outside the bounded contexts.
    /// <para>获取位于限界上下文之外的非上下文区域名。</para>
    /// </summary>
    public static IReadOnlySet<string> RegionLayerNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Application",
        "Core",
        "Infrastructure",
    };

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> until a directory containing <c>UotanStudio.slnx</c> is found.
    /// <para>从 <paramref name="startDirectory"/> 逐级向上查找包含 <c>UotanStudio.slnx</c> 的目录。</para>
    /// </summary>
    /// <param name="startDirectory">Directory to start the search from, usually <c>AppContext.BaseDirectory</c>.</param>
    /// <param name="repositoryRoot">The discovered repository root, or <see langword="null"/> when the solution file is missing.</param>
    /// <returns><see langword="true"/> when the solution file was located.</returns>
    public static bool TryFindRepositoryRoot(string startDirectory, out string? repositoryRoot)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "UotanStudio.slnx")))
            {
                repositoryRoot = current.FullName;
                return true;
            }

            current = current.Parent;
        }

        repositoryRoot = null;
        return false;
    }

    /// <summary>
    /// Discovers the bounded-context names of a firmware tree: the children of <c>BoundedContexts/</c> when that layer
    /// folder exists, otherwise the top-level folders outside the region layers.
    /// <para>发现固件树的限界上下文名：存在 <c>BoundedContexts/</c> 层时取其子目录，否则取区域层之外的顶层目录。</para>
    /// </summary>
    /// <param name="firmwareProjectDirectory">Absolute path of the <c>UotanStudio.Firmware</c> directory or a synthetic tree root.</param>
    /// <returns>Context names in ordinal order; empty when the tree carries no context folder.</returns>
    public static IReadOnlySet<string> DiscoverContextNames(string firmwareProjectDirectory)
    {
        var boundedDirectory = Path.Combine(firmwareProjectDirectory, "BoundedContexts");
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(boundedDirectory))
        {
            foreach (var child in Directory.EnumerateDirectories(boundedDirectory))
            {
                names.Add(Path.GetFileName(child));
            }

            return names;
        }

        foreach (var child in Directory.EnumerateDirectories(firmwareProjectDirectory))
        {
            var name = Path.GetFileName(child);
            if (!IsRegionLayerName(name) && !IsStructuralFolderName(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static bool IsStructuralFolderName(string name)
    {
        return name is "Properties" or "artifacts" or "obj" or "bin";
    }

    /// <summary>
    /// Reports the context or region a source file belongs to based on its path. Supports both the layered layout
    /// (<c>BoundedContexts/&lt;Context&gt;/…</c>) and the flat layout (<c>&lt;Context&gt;/…</c> directly under the project).
    /// <para>根据路径报告源文件所属的上下文或区域。同时支持分层布局（<c>BoundedContexts/&lt;Context&gt;/…</c>）与
    /// 扁平布局（上下文目录直接位于工程之下）。</para>
    /// </summary>
    /// <param name="path">Path of the source file, absolute or synthetic.</param>
    /// <returns>The owning context or region name, or <see langword="null"/> when the path carries no region.</returns>
    public static string? OwnerOf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var markerIndex = normalized.LastIndexOf(BoundedContextsMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var remainder = normalized[(markerIndex + BoundedContextsMarker.Length)..];
            var separatorIndex = remainder.IndexOf('/');
            if (separatorIndex <= 0)
            {
                return null;
            }

            var candidate = remainder[..separatorIndex];
            return RegionLayerNames.Contains(candidate) ? null : candidate;
        }

        var projectIndex = normalized.LastIndexOf(ProjectMarker, StringComparison.Ordinal);
        if (projectIndex >= 0)
        {
            var remainder = normalized[(projectIndex + ProjectMarker.Length)..];
            var separatorIndex = remainder.IndexOf('/');
            if (separatorIndex <= 0)
            {
                return null;
            }

            var candidate = remainder[..separatorIndex];
            return IsStructuralFolderName(candidate) ? null : candidate;
        }

        foreach (var layer in RegionLayerNames)
        {
            if (normalized.Contains('/' + layer + '/', StringComparison.Ordinal) || normalized.StartsWith(layer + '/', StringComparison.Ordinal))
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts every dependency edge written in a single source file, ignoring documentation comments and the file's own
    /// namespace declaration. When <paramref name="knownContextNames"/> is provided, flat references such as
    /// <c>UotanStudio.Firmware.FileSystem</c> count as context edges only for those names.
    /// <para>提取单个源文件中写出的所有依赖边，忽略文档注释与文件自身的 namespace 声明。提供
    /// <paramref name="knownContextNames"/> 时，<c>UotanStudio.Firmware.FileSystem</c> 之类的扁平引用只有命中已知上下文名才计为上下文边。</para>
    /// </summary>
    /// <param name="path">Path of the source file, used to attribute the edge source.</param>
    /// <param name="content">Full text of the source file.</param>
    /// <param name="knownContextNames">Context names discovered for the scanned tree, or <see langword="null"/> to skip flat-reference matching.</param>
    /// <returns>Edges in line order; empty when the file owns no region or references nothing else.</returns>
    public static IReadOnlyList<FirmwareDependencyEdge> ScanFile(string path, string content, IReadOnlySet<string>? knownContextNames = null)
    {
        var owner = OwnerOf(path);
        if (owner is null)
        {
            return [];
        }

        var edges = new List<FirmwareDependencyEdge>();
        var lines = content.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("///", StringComparison.Ordinal))
            {
                continue;
            }

            if (NamespaceDeclarationPattern.IsMatch(line))
            {
                continue;
            }

            AppendMatches(ContextReferencePattern, line, owner, index + 1, edges, target => true);
            if (knownContextNames is null)
            {
                AppendMatches(RegionReferencePattern, line, owner, index + 1, edges, target => true);
            }
            else
            {
                AppendMatches(FlatReferencePattern, line, owner, index + 1, edges, target =>
                    RegionLayerNames.Contains(target) || knownContextNames.Contains(target));
            }
        }

        return edges;
    }

    /// <summary>
    /// Reads every C# source file below the firmware project directory, skipping build outputs.
    /// <para>读取固件工程目录下的每个 C# 源文件，跳过构建产物。</para>
    /// </summary>
    /// <param name="firmwareProjectDirectory">Absolute path of the <c>UotanStudio.Firmware</c> directory.</param>
    /// <returns>Path and content pairs for the scanner to analyse.</returns>
    public static IEnumerable<(string Path, string Content)> ReadSourceFiles(string firmwareProjectDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(firmwareProjectDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!IsIgnoredDirectory(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            {
                yield return (file, File.ReadAllText(file));
            }
        }
    }

    /// <summary>
    /// Scans a whole firmware project directory and returns every edge found in it, with context names discovered from
    /// the tree so flat references are attributed as well.
    /// <para>扫描整个固件工程目录并返回其中发现的每条边；上下文名从目录树发现，扁平引用同样会被归属。</para>
    /// </summary>
    /// <param name="firmwareProjectDirectory">Absolute path of the <c>UotanStudio.Firmware</c> directory.</param>
    /// <returns>Edges keyed by the file and line that produced them.</returns>
    public static IReadOnlyList<FirmwareDependencyEdge> ScanDirectory(string firmwareProjectDirectory)
    {
        var contextNames = DiscoverContextNames(firmwareProjectDirectory);
        var edges = new List<FirmwareDependencyEdge>();
        foreach (var (path, content) in ReadSourceFiles(firmwareProjectDirectory))
        {
            edges.AddRange(ScanFile(path, content, contextNames));
        }

        return edges;
    }

    /// <summary>
    /// Collapses per-line edges into context-to-context edges with their line counts, keeping the context declaration order.
    /// <para>将逐行边折叠为带行数的上下文间边，并保留上下文声明顺序。</para>
    /// </summary>
    /// <param name="edges">Raw edges produced by <see cref="ScanFile"/> or <see cref="ScanDirectory"/>.</param>
    /// <returns>Distinct context-to-context edges ordered by source and target name.</returns>
    public static IReadOnlyList<FirmwareContextEdge> AggregateContextEdges(IEnumerable<FirmwareDependencyEdge> edges)
    {
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (IsRegionLayerName(edge.Source) || IsRegionLayerName(edge.Target))
            {
                continue;
            }

            if (!counts.TryGetValue(edge.Source, out var targets))
            {
                targets = new Dictionary<string, int>(StringComparer.Ordinal);
                counts[edge.Source] = targets;
            }

            targets.TryGetValue(edge.Target, out var current);
            targets[edge.Target] = current + 1;
        }

        var result = new List<FirmwareContextEdge>();
        foreach (var source in counts.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var target in counts[source].Keys.Order(StringComparer.Ordinal))
            {
                result.Add(new FirmwareContextEdge(source, target, counts[source][target]));
            }
        }

        return result;
    }

    /// <summary>
    /// Returns <see langword="true"/> when a name belongs to a non-context region instead of a bounded context.
    /// <para>当名称属于非上下文区域而非限界上下文时返回 <see langword="true"/>。</para>
    /// </summary>
    /// <param name="name">Region or context name produced by the scanner.</param>
    /// <returns>Whether the name is one of <see cref="RegionLayerNames"/>.</returns>
    public static bool IsRegionLayerName(string name)
    {
        return RegionLayerNames.Contains(name);
    }

    /// <summary>
    /// Depth-first searches the context dependency graph for a cycle and renders it when one exists.
    /// <para>对上下文依赖图做深度优先搜索，并在存在环时渲染该环。</para>
    /// </summary>
    /// <param name="edges">Context-to-context edges, usually from <see cref="AggregateContextEdges"/>.</param>
    /// <returns>A readable cycle such as <c>FileSystem -> BlockImage -> FileSystem</c>, or <see langword="null"/> when the graph is acyclic.</returns>
    public static string? FindContextCycle(IEnumerable<FirmwareContextEdge> edges)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var nodes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (IsRegionLayerName(edge.Source) || IsRegionLayerName(edge.Target) || string.Equals(edge.Source, edge.Target, StringComparison.Ordinal))
            {
                continue;
            }

            nodes.Add(edge.Source);
            nodes.Add(edge.Target);
            if (!graph.TryGetValue(edge.Source, out var targets))
            {
                targets = [];
                graph[edge.Source] = targets;
            }

            if (!targets.Contains(edge.Target))
            {
                targets.Add(edge.Target);
            }
        }

        foreach (var targets in graph.Values)
        {
            targets.Sort(StringComparer.Ordinal);
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();

        return nodes.Any(node => TryVisit(node)) ? string.Join(" -> ", path) : null;

        bool TryVisit(string node)
        {
            state[node] = 1;
            path.Add(node);
            if (graph.TryGetValue(node, out var targets))
            {
                foreach (var next in targets)
                {
                    if (!state.TryGetValue(next, out var nextState) || nextState == 0)
                    {
                        if (TryVisit(next))
                        {
                            return true;
                        }
                    }
                    else if (nextState == 1)
                    {
                        path.Add(next);
                        return true;
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            state[node] = 2;
            return false;
        }
    }

    private static void AppendMatches(Regex pattern, string line, string owner, int lineNumber, List<FirmwareDependencyEdge> edges, Func<string, bool> countsAsEdge)
    {
        foreach (Match match in pattern.Matches(line))
        {
            var target = match.Groups[1].Value;
            if (string.Equals(target, owner, StringComparison.Ordinal) || !countsAsEdge(target))
            {
                continue;
            }

            edges.Add(new FirmwareDependencyEdge(owner, target, lineNumber));
        }
    }

    private static bool IsIgnoredDirectory(string name)
    {
        return name is "bin" or "obj" or "artifacts";
    }
}

/// <summary>
/// A dependency written on one source line of a firmware file.
/// <para>固件文件的某一源码行中写出的依赖。</para>
/// </summary>
/// <param name="Source">Context or region that owns the referencing file.</param>
/// <param name="Target">Context or region named by the reference.</param>
/// <param name="Line">One-based line number that produced the reference.</param>
public sealed record FirmwareDependencyEdge(string Source, string Target, int Line);

/// <summary>
/// A distinct context-to-context dependency together with how many source lines declare it.
/// <para>一条去重后的上下文间依赖，以及声明它的源码行数。</para>
/// </summary>
/// <param name="Source">Referencing context.</param>
/// <param name="Target">Referenced context.</param>
/// <param name="LineCount">Number of source lines mentioning the target context.</param>
public sealed record FirmwareContextEdge(string Source, string Target, int LineCount)
{
    /// <summary>
    /// Gets the edge rendered as <c>Source -&gt; Target</c>.
    /// <para>获取以 <c>Source -&gt; Target</c> 形式渲染的边。</para>
    /// </summary>
    public string Key => $"{Source} -> {Target}";

    /// <summary>
    /// Renders the edge with its line count for assertion messages.
    /// <para>渲染带行数的边，用于断言消息。</para>
    /// </summary>
    /// <returns>The key followed by the declaring line count.</returns>
    public override string ToString()
    {
        return $"{Key} ({LineCount} lines)";
    }
}