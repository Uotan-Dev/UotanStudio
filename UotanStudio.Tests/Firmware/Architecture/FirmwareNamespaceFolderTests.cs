using System.Text.RegularExpressions;
using Xunit;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Guards the invariant that every firmware source file declares the namespace spelled by its folder path, so folder
/// moves and namespace renames cannot drift apart.
/// <para>守护"每个固件源文件声明的 namespace 必须与其文件夹路径一致"这一不变量，防止文件夹移动与 namespace 改名脱节。</para>
/// </summary>
public class FirmwareNamespaceFolderTests
{
    private static readonly Regex FileScopedNamespacePattern = new(@"^\s*namespace\s+([\w.]+)\s*;", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    [Fact]
    public void ScanCurrentFirmwareSources_NamespaceDeclarations_MatchFolderPaths()
    {
        var firmwareDirectory = FirmwareSourceTree.LocateFirmwareDirectory();
        var mismatches = new List<string>();
        var missing = new List<string>();

        foreach (var (path, content) in FirmwareContextDependencyScanner.ReadSourceFiles(firmwareDirectory))
        {
            var expected = "UotanStudio.Firmware." + Path.GetRelativePath(firmwareDirectory, Path.GetDirectoryName(path)!)
                .Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');

            var match = FileScopedNamespacePattern.Match(content);
            if (!match.Success)
            {
                if (!IsNamespaceFreeFileAllowed(path, firmwareDirectory))
                {
                    missing.Add(path);
                }

                continue;
            }

            if (!string.Equals(match.Groups[1].Value, expected, StringComparison.Ordinal))
            {
                mismatches.Add($"{Path.GetRelativePath(firmwareDirectory, path)}: declares {match.Groups[1].Value}, folder requires {expected}");
            }
        }

        Assert.True(missing.Count == 0, $"Firmware source files without a file-scoped namespace outside Properties/: {Describe(missing)}.");
        Assert.True(mismatches.Count == 0, $"Namespace declarations drifted from folder paths: {Describe(mismatches)}.");
    }

    [Fact]
    public void ScanCurrentFirmwareSources_Core_UsesNoThirdPartyNamespaces()
    {
        var firmwareDirectory = FirmwareSourceTree.LocateFirmwareDirectory();
        var coreDirectory = Path.Combine(firmwareDirectory, "Core");
        Assert.True(Directory.Exists(coreDirectory), $"Core directory '{coreDirectory}' is missing.");

        var offenders = new List<string>();
        foreach (var (path, content) in FirmwareContextDependencyScanner.ReadSourceFiles(coreDirectory))
        {
            foreach (Match match in FirmwareThirdPartyReferencePattern.Matches(content))
            {
                offenders.Add($"{Path.GetRelativePath(firmwareDirectory, path)}: {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0, $"Core picked up third-party dependencies and can no longer ride below every bounded context: {Describe(offenders)}.");
    }

    private static bool IsNamespaceFreeFileAllowed(string path, string firmwareDirectory)
    {
        var relative = Path.GetRelativePath(firmwareDirectory, path);
        return relative.StartsWith("Properties" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("Properties/", StringComparison.Ordinal);
    }

    private static string Describe(IEnumerable<string> items)
    {
        return string.Join(", ", items);
    }

    private static readonly Regex FirmwareThirdPartyReferencePattern = new(
        @"\b(?:BrotliSharpLib|BsDiff|Google\.Protobuf|K4os|LZMA|SharpCompress|SharpZipLib|BouncyCastle|YamlDotNet|System\.Security\.Cryptography\.Pkcs)\b",
        RegexOptions.CultureInvariant);
}