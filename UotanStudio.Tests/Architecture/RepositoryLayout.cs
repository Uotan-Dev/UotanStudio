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
            .Select(entry => (Path.GetRelativePath(root, entry.Path).Replace('\\', '/'), entry.Content))
            .OrderBy(entry => entry.Item1, StringComparer.Ordinal)
            .ToArray();
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
}