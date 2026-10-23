using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class OhscrcpyServerManagerTests
{
    [Theory]
    [InlineData("5.1.1.2", "aarch64", "uitest_agent_1.1.3.so")]
    [InlineData("5.1.1.3", "aarch64", "uitest_agent_1.1.5.so")]
    [InlineData("6.0.2.1", "aarch64", "uitest_agent_1.1.12.so")]
    [InlineData("6.0.2.2", "aarch64", "uitest_agent_1.2.3.so")]
    [InlineData("7.0.0.1", "x86_64", "uitest_agent_x86_1.1.12.so")]
    public void SelectControlAgent_VersionAndArchitecture_ReturnsExpectedAgent(
        string version, string architecture, string expected)
    {
        var actual = OhscrcpyServerManager.SelectControlAgent(Version.Parse(version), architecture);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SelectVideoLibraries_UnixDevice_ReturnsNewestUnixLibraryAndSkipsAgents()
    {
        var directory = CreateRuntimeDirectory(
            "libscrcpy_server_unix_6.3.1-20260113.z.so",
            "libscrcpy_server_unix_6.9-20260801.z.so",
            "libscrcpy_server_unix_6.5-20260313.z.so",
            "libscrcpy_server_5.10-20260114.z.so",
            "libscrcpy_server0.z.so",
            "uitest_agent_1.2.3.so");
        try
        {
            var device = new UiTestDeviceInfo(new Version(7, 0, 0, 1), "aarch64", "SGT-AL10");

            var libraries = OhscrcpyServerManager.SelectVideoLibraries(directory, device);

            Assert.Equal(3, libraries.Count);
            Assert.EndsWith("libscrcpy_server_unix_6.9-20260801.z.so", libraries[0], StringComparison.Ordinal);
            Assert.EndsWith("libscrcpy_server_unix_6.5-20260313.z.so", libraries[1], StringComparison.Ordinal);
            Assert.EndsWith("libscrcpy_server_unix_6.3.1-20260113.z.so", libraries[2], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectVideoLibraries_LegacyDevice_PrefersVersionedLegacyLibrary()
    {
        var directory = CreateRuntimeDirectory(
            "libscrcpy_server0.z.so",
            "libscrcpy_server1.z.so",
            "libscrcpy_server_5.10-20260114.z.so",
            "libscrcpy_server_unix_6.9-20260801.z.so");
        try
        {
            var device = new UiTestDeviceInfo(new Version(5, 0, 0, 1), "aarch64", "older");

            var libraries = OhscrcpyServerManager.SelectVideoLibraries(directory, device);

            Assert.Equal(3, libraries.Count);
            Assert.EndsWith("libscrcpy_server_5.10-20260114.z.so", libraries[0], StringComparison.Ordinal);
            Assert.EndsWith("libscrcpy_server1.z.so", libraries[1], StringComparison.Ordinal);
            Assert.EndsWith("libscrcpy_server0.z.so", libraries[2], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectVideoLibraries_MissingDirectory_ReturnsEmpty()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-ohscrcpy-missing-" + Guid.NewGuid().ToString("N"));

        var libraries = OhscrcpyServerManager.SelectVideoLibraries(
            directory, new UiTestDeviceInfo(new Version(7, 0, 0, 1), "aarch64", "model"));

        Assert.Empty(libraries);
    }

    [Fact]
    public void BuildVideoStartCommand_CaptureOptions_EmbedsEveryLaunchArgument()
    {
        var command = OhscrcpyServerManager.BuildVideoStartCommand(
            "libuotan_hdc_mirror_abc.z.so",
            new OhscrcpyLaunchOptions(FrameRate: 45, BitRate: 12_000_000, KeyFrameIntervalMs: 1500),
            "/data/local/tmp/libuotan_hdc_mirror_abc.z.so.log");

        Assert.Contains("--extension-name libuotan_hdc_mirror_abc.z.so", command, StringComparison.Ordinal);
        Assert.Contains("-scale 1", command, StringComparison.Ordinal);
        Assert.Contains("-frameRate 45", command, StringComparison.Ordinal);
        Assert.Contains("-bitRate 12000000", command, StringComparison.Ordinal);
        Assert.Contains("-iFrameInterval 1500", command, StringComparison.Ordinal);
        Assert.Contains("-repeatInterval 22", command, StringComparison.Ordinal);
        Assert.EndsWith("& echo started", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVideoStartCommand_OutOfRangeOptions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OhscrcpyServerManager.BuildVideoStartCommand(
            "ext.z.so", new OhscrcpyLaunchOptions(FrameRate: 121), "/data/local/tmp/ext.log"));
        Assert.Throws<ArgumentOutOfRangeException>(() => OhscrcpyServerManager.BuildVideoStartCommand(
            "ext.z.so", new OhscrcpyLaunchOptions(BitRate: 0), "/data/local/tmp/ext.log"));
    }

    [Fact]
    public void ParseProcessIds_MatchingLines_ReturnsDistinctPositiveIds()
    {
        const string output = """
            shell  101  1  0 10:00 ? 00:00:00 uitest start-daemon singleness
            shell  202  1  0 10:00 ? 00:00:00 sh -c ps -ef
            shell  202  1  0 10:00 ? 00:00:00 uitest start-daemon singleness --extension-name x
            not-a-pid 303 1 0 10:00 ? 00:00:00 marker
            """;

        var ids = OhscrcpyServerManager.ParseProcessIds(output, "uitest start-daemon singleness");

        Assert.Equal([101, 202], ids);
    }

    [Fact]
    public void ParseUiTestVersion_FourComponentOutput_ReturnsVersion()
    {
        Assert.Equal(new Version(7, 0, 0, 1), OhscrcpyServerManager.ParseUiTestVersion("7.0.0.1"));
        Assert.Equal(new Version(6, 3, 1, 2), OhscrcpyServerManager.ParseUiTestVersion("uitest 6.3.1.2\n"));
        Assert.Null(OhscrcpyServerManager.ParseUiTestVersion("no version here"));
        Assert.Null(OhscrcpyServerManager.ParseUiTestVersion(""));
    }

    private static string CreateRuntimeDirectory(params string[] fileNames)
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-ohscrcpy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var name in fileNames) File.WriteAllBytes(Path.Combine(directory, name), []);
        return directory;
    }
}