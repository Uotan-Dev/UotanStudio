using System.Text.RegularExpressions;
using Xunit;

namespace UotanStudio.Tests.Architecture;

public partial class ContainerBoundaryGuardTests
{
    private static readonly string[] ScannedProjects =
    [
        "UotanStudio.Hash",
        "UotanStudio.Net",
        "UotanStudio.Compress",
        "UotanStudio.Firmware",
        "UotanStudio.Comm",
        "UotanStudio.Mirroring",
        "UotanStudio.Media",
        "UotanStudio.Projects",
        "UotanStudio.Delivery",
        "UotanStudio.Mcp",
        "UotanStudio",
        "UotanStudio.Desktop",
        "UotanStudio.RegexInfer",
        "UotanStudio.Firmware.MagicGen",
        "UotanStudio.FilesystemFixture",
        "UotanStudio.Benchmarks",
    ];

    private static readonly HashSet<string> AllowedPaths = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void ProductionSources_ConstructZipArchivesOnlyInsideCompress()
    {
        var offenders = new List<string>();
        foreach (var project in ScannedProjects)
        {
            foreach (var (path, content) in RepositoryLayout.SourceFiles(project))
            {
                if (AllowedPaths.Contains(path) || path.StartsWith("UotanStudio.Compress/", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.AddRange(RepositoryLayout.Matches(path, content, ZipConstructionPattern()));
                offenders.AddRange(RepositoryLayout.Matches(path, content, ZipFileFacadePattern()));
            }
        }

        Assert.True(offenders.Count == 0, "ZIP construction must go through UotanStudio.Compress.Zip:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ZipConstructionPattern_Synthetic_ReportsConstructionAndZipFile()
    {
        const string content = "using var archive = new ZipArchive(stream);\nusing var zip = ZipFile.OpenRead(path);";
        Assert.Equal(2, RepositoryLayout.Matches("sample.cs", content, ZipConstructionPattern()).Count + RepositoryLayout.Matches("sample.cs", content, ZipFileFacadePattern()).Count);
    }

    [GeneratedRegex(@"new\s+(?:System\.IO\.Compression\.)?ZipArchive\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ZipConstructionPattern();

    [GeneratedRegex(@"\bZipFile\.(?:Open|OpenRead|OpenWrite|CreateFromDirectory)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ZipFileFacadePattern();
}