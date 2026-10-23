using Xunit;

namespace UotanStudio.Tests.Architecture;

public class NamespaceOwnershipGuardTests
{
    private static readonly IReadOnlyDictionary<string, string> AssemblyRoots = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UotanStudio.Hash"] = "UotanStudio.Hash",
        ["UotanStudio.Net"] = "UotanStudio.Net",
        ["UotanStudio.Compress"] = "UotanStudio.Compress",
        ["UotanStudio.Firmware"] = "UotanStudio.Firmware",
        ["UotanStudio.Firmware.MagicGen"] = "UotanStudio.Firmware.MagicGen",
        ["UotanStudio.Comm"] = "UotanStudio.Comm",
        ["UotanStudio.Mirroring"] = "UotanStudio.Mirroring",
        ["UotanStudio.Media"] = "UotanStudio.Media",
        ["UotanStudio.Projects"] = "UotanStudio.Projects",
        ["UotanStudio.Delivery"] = "UotanStudio.Delivery",
        ["UotanStudio.Mcp"] = "UotanStudio.Mcp",
        ["UotanStudio"] = "UotanStudio",
        ["UotanStudio.Desktop"] = "UotanStudio.Desktop",
        ["UotanStudio.Benchmarks"] = "UotanStudio.Benchmarks",
        ["UotanStudio.FilesystemFixture"] = "UotanStudio.FilesystemFixture",
        ["UotanStudio.RegexInfer"] = "UotanStudio.RegexInfer",
    };

    private static readonly string[] HostForbiddenRoots =
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
        "UotanStudio.Desktop",
        "UotanStudio.Benchmarks",
        "UotanStudio.FilesystemFixture",
        "UotanStudio.RegexInfer",
    ];

    [Fact]
    public void ProductionSources_DeclareTheirAssemblyRoot()
    {
        var offenders = new List<string>();
        foreach (var (projectDirectory, root) in AssemblyRoots)
        {
            foreach (var (path, content) in RepositoryLayout.SourceFiles(projectDirectory))
            {
                var name = RepositoryLayout.FirstNamespace(content);
                if (name.Length == 0)
                {
                    continue;
                }

                if (string.Equals(projectDirectory, "UotanStudio", StringComparison.Ordinal))
                {
                    if (!name.StartsWith("UotanStudio", StringComparison.Ordinal)
                        || HostForbiddenRoots.Any(forbidden => name.Equals(forbidden, StringComparison.Ordinal)
                            || name.StartsWith(forbidden + ".", StringComparison.Ordinal)))
                    {
                        offenders.Add($"{path}: declares {name}");
                    }

                    continue;
                }

                if (!name.Equals(root, StringComparison.Ordinal) && !name.StartsWith(root + ".", StringComparison.Ordinal))
                {
                    offenders.Add($"{path}: declares {name}, assembly root is {root}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Source namespaces must live under their assembly root:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NamespacePattern_SyntheticSource_ReturnsFirstNamespace()
    {
        Assert.Equal("UotanStudio.Projects.Schemes", RepositoryLayout.FirstNamespace("using X;\nnamespace UotanStudio.Projects.Schemes;\n"));
        Assert.Equal(string.Empty, RepositoryLayout.FirstNamespace("using X;\n"));
    }

    [Fact]
    public void Solution_DeclaresTheExpectedProductionProjects()
    {
        var directories = RepositoryLayout.SolutionProjectDirectories();
        Assert.Contains("UotanStudio.Net", directories);
        Assert.Contains("UotanStudio.Benchmarks", directories);
        Assert.Contains("UotanStudio.FilesystemFixture", directories);
        Assert.DoesNotContain("UotanStudio.IconVerification", directories);
    }
}