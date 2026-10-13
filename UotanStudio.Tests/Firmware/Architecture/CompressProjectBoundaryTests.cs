using System.Text.RegularExpressions;
using Xunit;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Guards the invariants that keep <c>UotanStudio.Compress</c> a leaf library: every source file declares the
/// namespace spelled by its folder path, the project references nothing above the hash library, and the
/// compression implementation never reaches back into the firmware assembly.
/// <para>守护使 <c>UotanStudio.Compress</c> 保持叶子库地位的不变量：每个源文件声明的 namespace 与其文件夹路径一致，
/// 工程只引用哈希库，且压缩实现绝不反向引用固件程序集。</para>
/// </summary>
public class CompressProjectBoundaryTests
{
    private static readonly Regex FileScopedNamespacePattern = new(@"^\s*namespace\s+([\w.]+)\s*;", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex FirmwareReferencePattern = new(
        @"^(?!\s*\[assembly:\s*InternalsVisibleTo).*?UotanStudio\.Firmware",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex ProjectReferencePattern = new(
        @"<ProjectReference\s+Include=""(?<path>[^""]+)""",
        RegexOptions.CultureInvariant);

    [Fact]
    public void ScanCurrentCompressSources_NamespaceDeclarations_MatchFolderPaths()
    {
        var compressDirectory = CompressSourceTree.LocateCompressDirectory();
        var mismatches = new List<string>();
        var missing = new List<string>();

        foreach (var (path, content) in CompressSourceTree.ReadSourceFiles())
        {
            var expected = ExpectedNamespace(compressDirectory, path);

            var match = FileScopedNamespacePattern.Match(content);
            if (!match.Success)
            {
                if (!IsNamespaceFreeFileAllowed(path, compressDirectory))
                {
                    missing.Add(path);
                }

                continue;
            }

            if (!string.Equals(match.Groups[1].Value, expected, StringComparison.Ordinal))
            {
                mismatches.Add($"{Path.GetRelativePath(compressDirectory, path)}: declares {match.Groups[1].Value}, folder requires {expected}");
            }
        }

        Assert.True(missing.Count == 0, $"Compression source files without a file-scoped namespace outside Properties/: {Describe(missing)}.");
        Assert.True(mismatches.Count == 0, $"Namespace declarations drifted from folder paths: {Describe(mismatches)}.");
    }

    [Fact]
    public void ScanCurrentCompressSources_FirmwareAssembly_IsNeverReferenced()
    {
        var offenders = new List<string>();
        foreach (var (path, content) in CompressSourceTree.ReadSourceFiles())
        {
            offenders.AddRange(FindFirmwareReferences(path, content));
        }

        Assert.True(offenders.Count == 0, $"UotanStudio.Compress references UotanStudio.Firmware and would form an assembly cycle: {Describe(offenders)}. The shared kernel sits above this layer; keep the needed helper local to the compression project instead.");
    }

    [Fact]
    public void ExpectedNamespace_SyntheticCompressPaths_SpellTheNamespaceFromTheFolder()
    {
        const string root = "C:/repo/UotanStudio.Compress";

        Assert.Equal("UotanStudio.Compress", ExpectedNamespace(root, Path.Combine(root, "CompressionService.cs")));
        Assert.Equal("UotanStudio.Compress.Compressors", ExpectedNamespace(root, Path.Combine(root, "Compressors", "ZstdCompressor.cs")));
        Assert.Equal("UotanStudio.Compress.Codecs.Lz4", ExpectedNamespace(root, Path.Combine(root, "Codecs", "Lz4", "Lz4DestinationCodec.cs")));
        Assert.Equal("UotanStudio.Compress.Codecs.Lzo", ExpectedNamespace(root, Path.Combine(root, "Codecs", "Lzo", "Lzo1x.cs")));
    }

    [Fact]
    public void FindFirmwareReferences_RealReferenceAndFriendAssemblyDeclaration_ReportsOnlyTheReference()
    {
        const string path = "C:/repo/UotanStudio.Compress/CompressionService.cs";
        const string content = """
            using UotanStudio.Firmware.Core;

            namespace UotanStudio.Compress;

            public static class CompressionService
            {
                public static void Run() => UotanStudio.Firmware.Core.Guard.ThrowIfNull(null);
            }
            """;

        var offenders = FindFirmwareReferences(path, content);

        Assert.Equal(2, offenders.Count);
        Assert.All(offenders, offender => Assert.StartsWith("CompressionService.cs:", offender, StringComparison.Ordinal));
    }

    [Fact]
    public void FindFirmwareReferences_FriendAssemblyDeclarationOnly_ReportsNothing()
    {
        const string path = "C:/repo/UotanStudio.Compress/Properties/AssemblyInfo.cs";
        const string content = """
            using System.Runtime.CompilerServices;

            [assembly: InternalsVisibleTo("UotanStudio.Firmware")]
            [assembly: InternalsVisibleTo("UotanStudio.Tests")]
            """;

        Assert.Empty(FindFirmwareReferences(path, content));
    }

    [Fact]
    public void ScanCurrentCompressProject_ReferencesOnlyTheHashLibrary()
    {
        var compressDirectory = CompressSourceTree.LocateCompressDirectory();
        var projectPath = Path.Combine(compressDirectory, "UotanStudio.Compress.csproj");
        Assert.True(File.Exists(projectPath), $"Compression project file '{projectPath}' is missing.");

        var references = ProjectReferencePattern.Matches(File.ReadAllText(projectPath))
            .Select(match => Path.GetFileNameWithoutExtension(match.Groups["path"].Value))
            .Where(name => name.Length > 0)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["UotanStudio.Hash"], references);
    }

    private static IReadOnlyList<string> FindFirmwareReferences(string path, string content)
    {
        var offenders = new List<string>();
        foreach (Match match in FirmwareReferencePattern.Matches(content))
        {
            var line = content.Take(match.Index).Count(character => character == '\n') + 1;
            offenders.Add($"{Path.GetFileName(path)}:{line}");
        }

        return offenders;
    }

    private static string ExpectedNamespace(string compressDirectory, string path)
    {
        var folder = Path.GetRelativePath(compressDirectory, Path.GetDirectoryName(path)!)
            .Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');

        return folder == "." ? "UotanStudio.Compress" : "UotanStudio.Compress." + folder;
    }

    private static bool IsNamespaceFreeFileAllowed(string path, string compressDirectory)
    {
        var relative = Path.GetRelativePath(compressDirectory, path);
        return relative.StartsWith("Properties" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("Properties/", StringComparison.Ordinal);
    }

    private static string Describe(IEnumerable<string> items)
    {
        return string.Join(", ", items);
    }
}