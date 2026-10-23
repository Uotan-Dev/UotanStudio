using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsExclusionTests
{
    private static string[] ImagePaths(byte[] image)
    {
        using var parser = new ErofsParser(image);
        return [.. parser.EnumerateEntries().Select(entry => entry.Path)];
    }

    private static ErofsOptions Options(IReadOnlyList<string>? paths, IReadOnlyList<string>? regexes)
        => new()
        {
            ExcludePaths = paths,
            ExcludeRegexes = regexes,
        };

    [Fact]
    public void IsExcluded_LiteralPath_MatchesExactlyAndIgnoresNeighbouringNames()
    {
        var exclusions = new ErofsExclusions(["system/bin/tool"], null);

        Assert.True(exclusions.IsExcluded("system/bin/tool"));
        Assert.False(exclusions.IsExcluded("system/bin/tool/nested"));
        Assert.False(exclusions.IsExcluded("system/bin/tool2"));
        Assert.False(exclusions.IsExcluded("system/bin/tool.d"));
        Assert.False(exclusions.IsExcluded("system/bin"));
        Assert.False(exclusions.IsExcluded("system/bin/toolbox/grep"));
    }

    [Fact]
    public void IsExcluded_LiteralDirectoryEntry_DoesNotExpandToDescendants()
    {
        var exclusions = new ErofsExclusions(["system/etc"], null);

        Assert.True(exclusions.IsExcluded("system/etc"));
        Assert.False(exclusions.IsExcluded("system/etc/hosts"));
        Assert.False(exclusions.IsExcluded("system/etc/init/deep/file"));
        Assert.False(exclusions.IsExcluded("system/etcetera"));
        Assert.False(exclusions.IsExcluded("system"));
    }

    [Fact]
    public void IsExcluded_RegexPattern_SearchesAnyPositionCaseSensitively()
    {
        var exclusions = new ErofsExclusions(null, [@"\.so$", "^System/"]);

        Assert.True(exclusions.IsExcluded("lib/libc.so"));
        Assert.True(exclusions.IsExcluded("System/app"));
        Assert.False(exclusions.IsExcluded("lib/libc.so.bak"));
        Assert.False(exclusions.IsExcluded("system/app"));
        Assert.False(exclusions.IsExcluded("lib/libc.so/system"));
    }

    [Fact]
    public void IsExcluded_HostSeparatorAndRedundantSlashes_CompareAsImagePath()
    {
        var exclusions = new ErofsExclusions(["system/bin/tool"], null);

        Assert.True(exclusions.IsExcluded(@"system\bin\tool"));
        Assert.True(exclusions.IsExcluded("//system//bin/tool"));
        Assert.True(exclusions.IsExcluded("./system/bin/tool"));
        Assert.True(exclusions.IsExcluded("system/bin/tool/"));
    }

    [Fact]
    public void IsExcluded_RootAndEmptyPath_AreNeverExcluded()
    {
        var exclusions = new ErofsExclusions(["system/etc"], null);

        Assert.False(exclusions.IsExcluded(null));
        Assert.False(exclusions.IsExcluded(string.Empty));
        Assert.False(exclusions.IsExcluded("/"));
        Assert.False(exclusions.IsExcluded("."));
        Assert.False(exclusions.IsEmpty);
    }

    [Fact]
    public void Validate_InvalidRegex_ThrowsArgumentExceptionNamingThePattern()
    {
        var exclusions = new ErofsExclusions(null, [@"a(b"]);

        var error = Assert.Throws<ArgumentException>(exclusions.Validate);
        Assert.Contains(@"a(b", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyRegexEntry_ThrowsArgumentException(string entry)
    {
        var exclusions = new ErofsExclusions(null, [entry]);

        var error = Assert.Throws<ArgumentException>(exclusions.Validate);
        Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(".")]
    public void Validate_EmptyPathEntry_ThrowsArgumentException(string entry)
    {
        var exclusions = new ErofsExclusions([entry], null);

        var error = Assert.Throws<ArgumentException>(exclusions.Validate);
        Assert.Contains("exclusion path entry", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NoRules_DoesNotThrow()
    {
        ErofsExclusions.Empty.Validate();
        new ErofsExclusions(null, null).Validate();
        new ErofsExclusions(["system/etc"], [@"\.apk$"]).Validate();
        Assert.True(ErofsExclusions.Empty.IsEmpty);
    }

    [Fact]
    public void Build_ExcludePathsAndRegexes_OmitMatchingEntries()
    {
        using var source = new TempDirectory();
        source.WriteBytes("kept.bin", Enumerable.Repeat((byte)0x31, 4096).ToArray());
        source.WriteBytes("drop.bin", Enumerable.Repeat((byte)0x32, 4096).ToArray());
        source.WriteBytes("secret/token", Enumerable.Repeat((byte)0x33, 512).ToArray());
        source.WriteBytes("secret/nested/data", Enumerable.Repeat((byte)0x34, 512).ToArray());
        source.WriteBytes("lib/kept.txt", Enumerable.Repeat((byte)0x35, 512).ToArray());
        source.WriteBytes("lib/drop.so", Enumerable.Repeat((byte)0x36, 512).ToArray());

        var image = new ErofsBuilder().Build(source.Path, Options(["drop.bin", "secret"], [@"\.so$"]));
        var paths = ImagePaths(image);

        Assert.Contains("kept.bin", paths);
        Assert.Contains("lib/kept.txt", paths);
        Assert.DoesNotContain("drop.bin", paths);
        Assert.DoesNotContain("lib/drop.so", paths);
        Assert.DoesNotContain(paths, path => path == "secret" || path.StartsWith("secret/", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ExcludeRegex_KeepsNeighbouringNameWithTheSamePrefix()
    {
        using var source = new TempDirectory();
        source.WriteBytes("keep/one.txt", "one"u8.ToArray());
        source.WriteBytes("keep/one.txt.backup", "backup"u8.ToArray());
        source.WriteBytes("keep/two.apk", "two"u8.ToArray());

        var image = new ErofsBuilder().Build(source.Path, Options(null, [@"\.apk$"]));
        var paths = ImagePaths(image);

        Assert.Contains("keep/one.txt", paths);
        Assert.Contains("keep/one.txt.backup", paths);
        Assert.DoesNotContain(paths, path => path.EndsWith(".apk", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_InvalidRegex_FailsBeforeCollection()
    {
        using var source = new TempDirectory();
        source.WriteBytes("kept.txt", "kept"u8.ToArray());

        var error = Assert.Throws<ArgumentException>(
            () => new ErofsBuilder().Build(source.Path, Options(null, ["[unterminated"])));

        Assert.Contains("[unterminated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RulesAndEditPlanMembership_ComposeConjunctively()
    {
        using var source = new TempDirectory();
        source.WriteBytes("kept.txt", "kept"u8.ToArray());
        source.WriteBytes("projected-and-excluded.txt", "excluded"u8.ToArray());
        source.WriteBytes("not-projected.txt", "not-projected"u8.ToArray());
        var manifest = new FileSystemPreservationData
        {
            ParsedEntries =
            [
                new() { Path = ".", Mode = 0x41ED },
                new() { Path = "kept.txt", Mode = 0x81A4 },
                new() { Path = "projected-and-excluded.txt", Mode = 0x81A4 },
            ],
            ImagePaths = new()
            {
                ["kept.txt"] = "kept.txt",
                ["projected-and-excluded.txt"] = "projected-and-excluded.txt",
            },
        };
        var projection = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kept.txt"] = "file:kept",
            ["projected-and-excluded.txt"] = "file:excluded",
        };
        using var scope = new FileSystemEditPlan(source.Path, manifest, projection).Enter();

        var image = new ErofsBuilder().Build(source.Path, Options(["projected-and-excluded.txt"], null));
        var paths = ImagePaths(image);

        Assert.Contains("kept.txt", paths);
        Assert.DoesNotContain("projected-and-excluded.txt", paths);
        Assert.DoesNotContain("not-projected.txt", paths);
    }
}