using Xunit;

namespace UotanStudio.Tests.TestInfrastructure;

public sealed class MagiskBootOracleTests
{
    public static bool OracleRunnable => NativeOracleGate.MagiskBootOracleAvailable;
    public static bool LargeDataEnabled => NativeOracleGate.LargeDataEnabled;

    [Fact]
    public void IsAvailable_VariableUnset_ReportsUnavailableSoOracleTestsSkip()
    {
        Assert.SkipWhen(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MagiskBootOracle.ExecutableVariable)),
            "UOTAN_MAGISKBOOT_ORACLE is set for this run; this case only describes the unset gate.");

        Assert.Null(MagiskBootOracle.TryGetExecutablePath());
        Assert.False(MagiskBootOracle.IsAvailable);
        Assert.False(NativeOracleGate.MagiskBootOracleAvailable);
    }

    [Fact]
    public void ResolveConfiguredPath_DirectoryValue_ResolvesContainedBinaryStub()
    {
        using var workspace = MagiskBootOracle.CreateWorkspace();
        var binaryName = OperatingSystem.IsWindows() ? "magiskboot.exe" : "magiskboot";
        File.WriteAllText(workspace.PathFor(binaryName), "stub");

        Assert.Equal(workspace.PathFor(binaryName), MagiskBootOracle.ResolveConfiguredPath(workspace.DirectoryPath));
        Assert.Null(MagiskBootOracle.ResolveConfiguredPath(Path.Combine(workspace.DirectoryPath, "absent")));
        Assert.Null(MagiskBootOracle.ResolveConfiguredPath(null));
        Assert.Null(MagiskBootOracle.ResolveConfiguredPath("   "));
    }

    [Fact]
    public void SkipUnlessLargeData_StaticGateAndRuntimeGate_AgreeOnSwitchValue()
    {
        NativeOracleGate.SkipUnlessLargeData();

        Assert.True(LargeDataEnabled);
        Assert.Contains(NativeOracleGate.LargeDataVariable, NativeOracleGate.DescribeMissing(NativeOracleGate.LargeDataVariable));
        Assert.Contains("UOTAN_LARGE_DATA=1", NativeOracleGate.DescribeMissing(NativeOracleGate.LargeDataVariable));
    }

    [Fact(Skip = "Set UOTAN_MAGISKBOOT_ORACLE to a magiskboot binary, or to a directory containing one, to compare boot-image behaviour with the reference implementation.",
        SkipUnless = nameof(OracleRunnable))]
    public async Task RunAsync_UsageStyleInvocation_CapturesReferenceBanner()
    {
        using var workspace = MagiskBootOracle.CreateWorkspace();
        var result = await MagiskBootOracle.RunAsync([], workspace.DirectoryPath,
            TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.SkipUnless(result.Launched,
            "UOTAN_MAGISKBOOT_ORACLE points at a binary this operating system cannot start: " + result.StandardError);
        Assert.False(result.TimedOut, result.CombinedOutput);
        Assert.True(result.CombinedOutput.Length > 0, $"magiskboot produced no output (exit {result.ExitCode}).");
    }
}