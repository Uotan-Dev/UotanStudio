using System.Text.RegularExpressions;
using UotanStudio.Tests.Firmware.Architecture;

namespace UotanStudio.Tests.Architecture;

internal static partial class RepositoryLayout
{
    internal static string LocateRoot()
    {
        if (!FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var root) || root is null)
        {
            throw new InvalidOperationException("The test host is not running from a UotanStudio.slnx checkout.");
        }

        return root;
    }

    internal static IReadOnlyList<(string RelativePath, string Content)> SourceFiles(string relativeProjectDirectory)
    {
        var root = LocateRoot();
        var directory = Path.Combine(root, relativeProjectDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return FirmwareContextDependencyScanner.ReadSourceFiles(directory)
            .Select(entry => (Path.GetRelativePath(root, entry.Path).Replace('\\', '/'), NormalizeLineBreaks(entry.Content)))
            .OrderBy(entry => entry.Item1, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Collapses every C# new-line character onto <c>\n</c> so a directive written after a lone <c>\r</c>,
    /// <c>U+0085</c>, <c>U+2028</c> or <c>U+2029</c> still starts a line for the anchored patterns.
    /// <para>把 C# 的所有换行字符统一成 <c>\n</c>，使写在孤立 <c>\r</c>、<c>U+0085</c>、<c>U+2028</c> 或
    /// <c>U+2029</c> 之后的指令在行首锚定模式下仍然是一行的开始。</para>
    /// </summary>
    /// <param name="content">Raw file content.<para>原始文件内容。</para></param>
    /// <returns>Content with <c>\n</c> line breaks only.<para>仅以 <c>\n</c> 换行的内容。</para></returns>
    internal static string NormalizeLineBreaks(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u0085', '\n')
            .Replace('\u2028', '\n')
            .Replace('\u2029', '\n');

    /// <summary>
    /// Returns every namespace name the file declares, in source order, including block-scoped ones that
    /// follow the file-scoped header.
    /// <para>按源码顺序返回文件声明的全部命名空间名，包含跟在文件头之后的块级声明。</para>
    /// </summary>
    /// <param name="content">Normalized file content.<para>已规范换行的文件内容。</para></param>
    /// <returns>Declared namespace names; empty when the file declares none.<para>声明的命名空间名；无声明时为空。</para></returns>
    internal static IReadOnlyList<string> Namespaces(string content) =>
        NamespacePattern().Matches(content).Select(match => match.Groups["name"].Value).ToArray();

    /// <summary>
    /// Returns the namespace each <c>using</c> directive points at, whatever separates the directives on the
    /// line and whether the directive is <c>static</c>, aliased or <c>global</c>.
    /// <para>返回每个 <c>using</c> 指令指向的命名空间，无论这些指令在同一行如何排布，也无论它是
    /// <c>static</c>、别名形式还是 <c>global</c> 形式。</para>
    /// </summary>
    /// <param name="content">File content, in any line-break convention.<para>任意换行风格的文件内容。</para></param>
    /// <returns>Imported namespace or type names in source order.<para>按源码顺序返回导入的命名空间或类型名。</para></returns>
    internal static IReadOnlyList<string> ImportedNamespaces(string content) =>
        UsingDirectivePattern().Matches(NormalizeLineBreaks(content)).Select(match => match.Groups["ns"].Value).ToArray();

    /// <summary>
    /// Reports the imported names that are the given root or live beneath it.
    /// <para>上报等于给定根命名空间或位于其下的导入名。</para>
    /// </summary>
    /// <param name="content">Normalized file content.<para>已规范换行的文件内容。</para></param>
    /// <param name="forbiddenRoots">Root namespaces that must not be imported.
    /// <para>不得导入的根命名空间。</para></param>
    /// <returns>Distinct forbidden roots an import falls under.<para>被导入命中的根命名空间（去重）。</para></returns>
    internal static IReadOnlyList<string> ForbiddenImports(string content, IReadOnlyList<string> forbiddenRoots)
    {
        var imported = ImportedNamespaces(content);
        var offenders = new List<string>();
        foreach (var root in forbiddenRoots)
        {
            if (imported.Any(name => string.Equals(name, root, StringComparison.Ordinal)
                || name.StartsWith(root + ".", StringComparison.Ordinal)))
            {
                offenders.Add(root);
            }
        }

        return offenders;
    }

    internal static IReadOnlyList<string> SolutionProjectPaths()
    {
        var root = LocateRoot();
        return File.ReadLines(Path.Combine(root, "UotanStudio.slnx"))
            .Select(line => SolutionProjectPattern().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["path"].Value.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> SolutionProjectDirectories()
    {
        return SolutionProjectPaths()
            .Select(path => Path.GetDirectoryName(path)!.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<(string ProjectDirectory, string ProjectFile, string Content)> ProjectFiles()
    {
        var root = LocateRoot();
        var results = new List<(string, string, string)>();
        foreach (var project in SolutionProjectPaths())
        {
            var fullPath = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(project)!.Replace('\\', '/');
            results.Add((directory, project, File.ReadAllText(fullPath)));
        }

        return results;
    }

    internal static string FirstNamespace(string content)
    {
        var match = NamespacePattern().Match(content);
        return match.Success ? match.Groups["name"].Value : string.Empty;
    }

    internal static IReadOnlyList<string> Matches(string relativePath, string content, Regex pattern)
    {
        var matches = new List<string>();
        var lineNumber = 0;
        foreach (var line in content.Split('\n'))
        {
            lineNumber++;
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in pattern.Matches(line))
            {
                matches.Add($"{relativePath}:{lineNumber}: {match.Value}");
            }
        }

        return matches;
    }

    [GeneratedRegex(@"<Project\s+Path=""(?<path>[^""]+\.csproj)""", RegexOptions.CultureInvariant)]
    internal static partial Regex SolutionProjectPattern();

    [GeneratedRegex(@"^\s*namespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    internal static partial Regex NamespacePattern();

    [GeneratedRegex(@"(?:^|;)[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?(?:[A-Za-z_]\w*[ \t]*=[ \t]*)?(?<ns>[A-Za-z_][\w.]*)",
        RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    internal static partial Regex UsingDirectivePattern();
}