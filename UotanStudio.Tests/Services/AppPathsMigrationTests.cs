using UotanStudio.Services;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Verifies the legacy-location migration performed at startup: children move into the unified per-user
/// data root, existing targets are never overwritten, and repeated runs are no-ops.
/// <para>验证启动时执行的遗留位置迁移：子项搬入统一的按用户数据根目录、既有目标不被覆盖、重复运行为空操作。</para>
/// </summary>
public sealed class AppPathsMigrationTests
{
    [Fact]
    public void MigrateDirectoryContents_MissingSource_DoesNothing()
    {
        using var target = new TempDirectory();
        var missing = Path.Combine(target.Path, "not-there");

        AppPaths.MigrateDirectoryContents(missing, Path.Combine(target.Path, "root"));

        Assert.False(Directory.Exists(Path.Combine(target.Path, "root")));
    }

    [Fact]
    public void MigrateDirectoryContents_MovesEveryChild_IntoTargetAndRemovesSource()
    {
        using var legacy = new TempDirectory();
        using var target = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(legacy.Path, "cache", "nested"));
        File.WriteAllText(Path.Combine(legacy.Path, "cache", "nested", "blob.bin"), "binary");
        File.WriteAllText(Path.Combine(legacy.Path, "settings.json"), "{}");

        AppPaths.MigrateDirectoryContents(legacy.Path, target.Path);

        Assert.Equal("binary", File.ReadAllText(Path.Combine(target.Path, "cache", "nested", "blob.bin")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(target.Path, "settings.json")));
        Assert.False(Directory.Exists(legacy.Path));
    }

    [Fact]
    public void MigrateDirectoryContents_KeepsExistingTargets_AndTheirSources()
    {
        using var legacy = new TempDirectory();
        using var target = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(legacy.Path, "cache"));
        File.WriteAllText(Path.Combine(legacy.Path, "cache", "old.bin"), "legacy");
        Directory.CreateDirectory(Path.Combine(target.Path, "cache"));
        File.WriteAllText(Path.Combine(target.Path, "cache", "new.bin"), "current");

        AppPaths.MigrateDirectoryContents(legacy.Path, target.Path);

        Assert.Equal("current", File.ReadAllText(Path.Combine(target.Path, "cache", "new.bin")));
        Assert.False(File.Exists(Path.Combine(target.Path, "cache", "old.bin")));
        Assert.True(Directory.Exists(legacy.Path));
    }

    [Fact]
    public void MigrateDirectoryContents_RepeatedRuns_AreIdempotent()
    {
        using var legacy = new TempDirectory();
        using var target = new TempDirectory();
        File.WriteAllText(Path.Combine(legacy.Path, "cache.bin"), "data");

        AppPaths.MigrateDirectoryContents(legacy.Path, target.Path);
        AppPaths.MigrateDirectoryContents(legacy.Path, target.Path);

        Assert.Equal("data", File.ReadAllText(Path.Combine(target.Path, "cache.bin")));
        Assert.False(Directory.Exists(legacy.Path));
    }

    [Fact]
    public void MigrateDirectoryContents_SameSourceAndTarget_IsSkipped()
    {
        using var root = new TempDirectory();
        File.WriteAllText(Path.Combine(root.Path, "keep.bin"), "data");

        AppPaths.MigrateDirectoryContents(root.Path, root.Path);

        Assert.Equal("data", File.ReadAllText(Path.Combine(root.Path, "keep.bin")));
    }

    [Fact]
    public void WellKnownDirectories_ResolveUnderRoot()
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.StartsWith(AppPaths.Root, AppPaths.Logs, comparison);
        Assert.StartsWith(AppPaths.Root, AppPaths.Keys, comparison);
        Assert.StartsWith(AppPaths.Root, AppPaths.VisualPlugins, comparison);
        Assert.StartsWith(AppPaths.Root, AppPaths.PublisherTrustFile, comparison);
        Assert.StartsWith(AppPaths.Root, AppPaths.ScrcpyServer, comparison);
        Assert.StartsWith(AppPaths.Root, AppPaths.Native, comparison);
        Assert.StartsWith(AppPaths.Root, AppPaths.AppleLockdown, comparison);
        Assert.StartsWith(AppPaths.Root, AppPaths.DeliveryTools, comparison);
    }
}