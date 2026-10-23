using Xunit;

namespace UotanStudio.Tests.Architecture;

public class SolutionMembershipGuardTests
{
    [Fact]
    public void EveryRepositoryProject_IsDeclaredInTheSolution()
    {
        var root = RepositoryLayout.LocateRoot();
        var declared = RepositoryLayout.SolutionProjectPaths().ToHashSet(StringComparer.Ordinal);
        var discovered = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !IsBuildOrReferencePath(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var missing = discovered.Where(path => !declared.Contains(path)).ToArray();
        var missingOnDisk = declared.Where(path => !File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
        Assert.True(missing.Length == 0, "Projects missing from UotanStudio.slnx:\n" + string.Join("\n", missing));
        Assert.True(missingOnDisk.Length == 0, "Solution entries without a project file:\n" + string.Join("\n", missingOnDisk));
    }

    [Fact]
    public void IconVerification_IsRemoved()
    {
        var root = RepositoryLayout.LocateRoot();
        Assert.False(Directory.Exists(Path.Combine(root, "UotanStudio.IconVerification")));
        Assert.DoesNotContain(RepositoryLayout.SolutionProjectPaths(), path => path.Contains("IconVerification", StringComparison.Ordinal));
    }

    private static bool IsBuildOrReferencePath(string path)
    {
        return path.StartsWith("reference/", StringComparison.Ordinal)
            || path.Contains("/bin/", StringComparison.Ordinal)
            || path.Contains("/obj/", StringComparison.Ordinal)
            || path.Contains("/artifacts/", StringComparison.Ordinal);
    }
}