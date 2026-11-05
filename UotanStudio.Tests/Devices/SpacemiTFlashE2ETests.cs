using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Real-device SpacemiT K1/K3 whole-package flash loop: drive the unpacked titan package exactly as the flash page
/// does, including the BootROM FSBL staging that a dead bulk-OUT must not turn into a permanent failure. Set
/// <c>UOTAN_SPACEMIT_FLASH_E2E=1</c> and point <c>UOTAN_SPACEMIT_FLASH_PACKAGE</c> at the unpacked package directory;
/// the suite skips itself everywhere else. These cases write to the board, so they never run by default.
/// <para>进迭时空 K1/K3 真机整包烧录闭环：完全按烧录页的方式驱动已解包的 titan 包，包含 BootROM FSBL 暂存——
/// 该阶段一次批量 OUT 失联不得变成永久失败。设 <c>UOTAN_SPACEMIT_FLASH_E2E=1</c> 并用
/// <c>UOTAN_SPACEMIT_FLASH_PACKAGE</c> 指向已解包的包目录；其余环境一律自跳。这些用例会真实写盘，因此默认从不执行。</para>
/// </summary>
public sealed class SpacemiTFlashE2ETests
{
    private const string EnableVariable = "UOTAN_SPACEMIT_FLASH_E2E";

    private const string PackageVariable = "UOTAN_SPACEMIT_FLASH_PACKAGE";

    [Fact]
    public async Task ReadVariables_SpacemiTGadget_AnswersIdentityWithoutWriting()
    {
        Assert.SkipUnless(IsEnabled(), $"{EnableVariable} is not set to 1; connect a SpacemiT board and opt in to run "
            + "this read-only probe.");
        using var monitor = DeviceMonitorService.Create();
        var device = FindSpacemiT(monitor);
        Assert.SkipWhen(device is null, "No SpacemiT fastboot endpoint was discovered on this host.");

        var probes = await Task.Run(
            () => monitor.RunFastboot(device, session => new[]
            {
                Probe(session, SpacemiTConstants.VarBromVersion),
                Probe(session, "version"),
                Probe(session, "product"),
                Probe(session, FastbootConstants.VarMaxDownloadSize),
            }, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.False(probes.Length == 0, "No probe ran.");
        Assert.True(probes.Any(probe => !probe.Contains("FAIL", StringComparison.Ordinal)),
            "Every fastboot probe failed: " + string.Join(" | ", probes));

        static string Probe(FastbootSession session, string key)
        {
            var (value, success, error) = session.GetVarWithResult(key, useCache: false);
            return $"getvar:{key}={success switch { true => "OKAY", false => "FAIL" }}:'{value}'{(error is null ? string.Empty : $" ({error})")}";
        }
    }

    [Fact]
    public async Task FlashWholePackage_SpacemiTGadget_CompletesAgainstRealHardware()
    {
        Assert.SkipUnless(IsEnabled(), $"{EnableVariable} is not set to 1; connect a SpacemiT board in BootROM FDL mode, "
            + $"point {PackageVariable} at the unpacked package and opt in to run this loop.");
        var packagePath = Environment.GetEnvironmentVariable(PackageVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(packagePath) || !Directory.Exists(packagePath),
            $"{PackageVariable} is not set to an existing unpacked package directory.");

        var package = SpacemiTFlashPackage.Load(packagePath);
        using var monitor = DeviceMonitorService.Create();
        var device = FindSpacemiT(monitor);
        Assert.SkipWhen(device is null, "No SpacemiT fastboot endpoint was discovered on this host.");

        var reported = new List<SpacemiTFlashProgress>();
        var progress = new CollectingProgress(reported);
        var options = new SpacemiTFlashOptions
        {
            FlashConfirmation = SpacemiTFlashOptions.RequiredConfirmation,
            RebootAfterFlash = false,
        };

        try
        {
            await monitor.RunSpacemiTFlashAsync(device, package, options, progress,
                TestContext.Current.CancellationToken);
        }
        catch (Exception error)
        {
            Assert.Fail("The run stopped before its final phase: " + string.Join(" | ", Snapshot(reported))
                + Environment.NewLine + error);
        }

        Assert.Contains(reported, report => report.Phase == "done");
        Assert.DoesNotContain(reported, report => report.Phase == "skip" && report.Message.Contains("FSBL", StringComparison.Ordinal));
    }

    /// <summary>
    /// Renders the phase-level progress trail, so a failing run shows exactly which actions completed.
    /// <para>渲染阶段级进度轨迹，使失败的运行能显示究竟哪些动作已经完成。</para>
    /// </summary>
    private static IEnumerable<string> Snapshot(List<SpacemiTFlashProgress> reports)
    {
        lock (reports)
        {
            return reports
                .Where(report => report.BytesCompleted is null)
                .Select(report => $"[{report.Phase}] {report.Message}")
                .ToArray();
        }
    }

    private static bool IsEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal);

    /// <summary>
    /// Finds the SpacemiT K1/K3 fastboot gadget by the exact VID/PID pair its BootROM and U-Boot stages both report.
    /// <para>按 BootROM 与 U-Boot 两阶段共同上报的精确 VID/PID 对查找进迭时空 K1/K3 fastboot gadget。</para>
    /// </summary>
    private static DetectedDevice? FindSpacemiT(DeviceMonitorService monitor) =>
        monitor.Scan().FirstOrDefault(candidate =>
            candidate.Protocol == DeviceProtocolKind.Fastboot &&
            candidate.VendorId == SpacemiTConstants.UsbVendorId &&
            candidate.ProductId == SpacemiTConstants.UsbProductId);

    /// <summary>
    /// Collects every progress report the flasher emits so the test can assert the run reached its final phase.
    /// <para>收集烧录器发出的每条进度报告，使用例能够断言运行抵达最终阶段。</para>
    /// </summary>
    private sealed class CollectingProgress(List<SpacemiTFlashProgress> reports) : IProgress<SpacemiTFlashProgress>
    {
        public void Report(SpacemiTFlashProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }
}