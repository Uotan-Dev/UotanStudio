using System.Text.RegularExpressions;
using Xunit;

namespace UotanStudio.Tests.Architecture;

public partial class ProjectReferenceDirectionGuardTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowed = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["UotanStudio.Net"] = new HashSet<string>(StringComparer.Ordinal),
        ["UotanStudio.Hash"] = new HashSet<string>(StringComparer.Ordinal),
        ["UotanStudio.Compress"] = Set("UotanStudio.Hash"),
        ["UotanStudio.Assets"] = Set("UotanStudio.Compress", "UotanStudio.Hash"),
        ["UotanStudio.Firmware"] = Set("UotanStudio.Assets", "UotanStudio.Compress", "UotanStudio.Hash"),
        ["UotanStudio.Comm"] = Set("UotanStudio.Assets", "UotanStudio.Firmware", "UotanStudio.Hash"),
        ["UotanStudio.Mirroring"] = Set("UotanStudio.Assets", "UotanStudio.Comm"),
        ["UotanStudio.Media"] = new HashSet<string>(StringComparer.Ordinal),
        ["UotanStudio.Projects"] = Set("UotanStudio.Hash", "UotanStudio.Compress"),
        ["UotanStudio.Delivery"] = Set("UotanStudio.Assets", "UotanStudio.Compress", "UotanStudio.Firmware", "UotanStudio.Hash", "UotanStudio.Net"),
        ["UotanStudio.Mcp"] = Set("UotanStudio.Firmware", "UotanStudio.Comm", "UotanStudio.Delivery", "UotanStudio.Projects"),
        ["UotanStudio"] = Set("UotanStudio.Assets", "UotanStudio.Projects", "UotanStudio.Comm", "UotanStudio.Mirroring", "UotanStudio.Firmware", "UotanStudio.Delivery", "UotanStudio.Mcp", "UotanStudio.Media", "UotanStudio.Hash", "UotanStudio.Net"),
        ["UotanStudio.Desktop"] = Set("UotanStudio", "UotanStudio.Mcp"),
        ["UotanStudio.RegexInfer"] = new HashSet<string>(StringComparer.Ordinal),
        ["UotanStudio.Firmware.MagicGen"] = Set("UotanStudio.Firmware"),
        ["UotanStudio.FilesystemFixture"] = Set("UotanStudio.Firmware", "UotanStudio.Hash"),
        ["UotanStudio.Benchmarks"] = Set("UotanStudio.Firmware", "UotanStudio.Hash", "UotanStudio.Compress", "UotanStudio.Comm", "UotanStudio"),
    };

    [Fact]
    public void ProjectReferences_FollowTheDeclaredDirection()
    {
        var offenders = new List<string>();
        foreach (var (projectDirectory, projectFile, content) in RepositoryLayout.ProjectFiles())
        {
            if (string.Equals(projectDirectory, "UotanStudio.Tests", StringComparison.Ordinal))
            {
                continue;
            }

            if (!Allowed.TryGetValue(projectDirectory, out var allowed))
            {
                offenders.Add($"{projectFile}: project is not registered in the dependency guard");
                continue;
            }

            foreach (Match match in ProjectReferencePattern().Matches(content))
            {
                var target = Path.GetFileNameWithoutExtension(match.Groups["path"].Value.Replace('\\', '/'));
                if (!allowed.Contains(target))
                {
                    offenders.Add($"{projectFile}: references {target}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Project references violate the allowed graph:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NetProject_IsALeaf()
    {
        var net = RepositoryLayout.ProjectFiles().Single(entry => entry.ProjectDirectory == "UotanStudio.Net");
        Assert.Empty(ProjectReferencePattern().Matches(net.Content));
    }

    [Fact]
    public void ProjectReferencePattern_Synthetic_ReturnsTargets()
    {
        var matches = ProjectReferencePattern().Matches("<ProjectReference Include=\"..\\UotanStudio.Hash\\UotanStudio.Hash.csproj\" />");
        Assert.Single(matches);
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    [GeneratedRegex(@"<ProjectReference\s+Include=""(?<path>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectReferencePattern();
}