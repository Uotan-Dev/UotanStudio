using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Real-device RKDL probe: downloads a caller-supplied loader on the maskrom lease, waits for the
/// re-enumerated loader endpoint, and reports the chip, storage and capability answers of both stages.
/// Set <c>UOTAN_RKDL_E2E=1</c> and <c>UOTAN_RKDL_LOADER</c> to a RKBOOT loader image to run; the suite
/// skips itself everywhere else and writes its report to <c>%TEMP%/rkdl-probe-report.txt</c>.
/// <para>真机 RKDL 探针：在 maskrom 租约上下载调用方给定的 loader，等待重枚举出的 loader 端点，并报告两个阶段
/// 的芯片、存储与能力应答。设 <c>UOTAN_RKDL_E2E=1</c> 且把 <c>UOTAN_RKDL_LOADER</c> 指向 RKBOOT loader
/// 镜像后运行；其余环境一律自跳，报告写入 <c>%TEMP%/rkdl-probe-report.txt</c>。</para>
/// </summary>
public sealed class RkdlLoaderProbeE2ETests
{
    private const string EnableVariable = "UOTAN_RKDL_E2E";
    private const string LoaderVariable = "UOTAN_RKDL_LOADER";
    private const string ReportFileName = "rkdl-probe-report.txt";

    [Fact]
    public async Task Rkdl_LoaderDownload_ReportsChipStorageAndCapabilities()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal),
            $"{EnableVariable} is not set to 1; connect a Rockchip maskrom device and opt in to run this probe.");

        var loaderPath = Environment.GetEnvironmentVariable(LoaderVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(loaderPath) || !File.Exists(loaderPath),
            $"{LoaderVariable} does not point at an existing RKBOOT loader image.");

        using var monitor = DeviceMonitorService.Create();
        var device = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Rkdl);
        Assert.SkipWhen(device is null, "No RKDL endpoint was discovered on this host.");

        var token = TestContext.Current.CancellationToken;
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"endpoint: key={device!.Key} serial={device.Serial} state={device.State} " +
            $"vid=0x{device.VendorId:X4} pid=0x{device.ProductId:X4} transport={device.Transport} physical={device.PhysicalId ?? "<none>"}");
        report.AppendLine(CultureInfo.InvariantCulture, $"readiness probe: {monitor.ProbeRkdl(device)}");

        var loaderBytes = await File.ReadAllBytesAsync(loaderPath!, token);
        report.AppendLine(CultureInfo.InvariantCulture, $"loader: {loaderPath} ({loaderBytes.Length} B)");

        var transitioned = false;
        try
        {
            await monitor.RunRkdlLoaderTransitionAsync(device,
                async (session, phaseToken) =>
                {
                    AppendSessionReport(report, "pre", session);
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"transport: {session.Transport.GetType().Name}");
                    if (session.IsRockusbMode)
                    {
                        report.AppendLine("loader: device already reports loader mode; the download is skipped.");
                        return null;
                    }

                    if (!TryWarmUp(session, report))
                    {
                        report.AppendLine("loader: bulk warm-up failed; resetting the transport and retrying once.");
                        session.Transport.Reset();
                        if (!TryWarmUp(session, report))
                        {
                            throw new InvalidOperationException(
                                "The mask ROM port answers no bulk command even after a transport reset.");
                        }
                    }

                    var written = await session.DownloadBootLoaderAsync(loaderBytes, phaseToken);
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"loader: sent {written} B through the 0x0471/0x0472 vendor requests.");
                    return session.CreateLoaderTransitionOffer();
                },
                (session, _) =>
                {
                    transitioned = true;
                    AppendSessionReport(report, "post", session);
                    return Task.FromResult(true);
                },
                token);
        }
        catch (Exception error)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"loader transition failed: {error.GetType().Name}: {error.Message}");
            if (error.InnerException is { } inner)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"loader transition inner: {inner.GetType().Name}: {inner.Message}");
            }

            await AppendRecoveredReportAsync(report, monitor, token);
        }

        var reportPath = Path.Combine(Path.GetTempPath(), ReportFileName);
        await File.WriteAllTextAsync(reportPath, report.ToString(), token);
        Console.WriteLine($"RKDL probe report: {reportPath}");

        Assert.True(transitioned || report.ToString().Contains("recovered", StringComparison.Ordinal),
            $"The loader download did not produce a loader-stage session. Report: {report}");
    }

    /// <summary>
    /// Runs the bulk warm-up the loader download needs and reports which step failed, so a transport that
    /// rejects the very first CBW is told apart from a device that answers but refuses a command.
    /// <para>执行 loader 下载所需的批量预热并报告失败的那一步，使拒绝第一条 CBW 的传输与能应答但拒绝某条命令的设备区分开。</para>
    /// </summary>
    private static bool TryWarmUp(RkdlSession session, StringBuilder report)
    {
        try
        {
            var ready = session.TestDeviceReady(out var current, out var total);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"loader: TEST_UNIT_READY ready={ready} progress={current}/{total}");
            if (!ready)
            {
                return false;
            }

            var chip = session.ReadChipInfo();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"loader: warm-up chip id=0x{chip.ChipId:X8} tag='{chip.Tag}'");
            var flash = session.ReadFlashInfo();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"loader: warm-up flash sectors={flash.SectorCount} size={flash.Size} B");
            return true;
        }
        catch (Exception error)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"loader: warm-up FAILED {Describe(error)}");
            return false;
        }
    }

    /// <summary>
    /// Reports every information command the current stage answers, catching each failure separately so
    /// one unsupported command does not hide the others.
    /// <para>报告当前阶段应答的每条信息命令，逐条捕获失败，使一条不支持的命令不会掩盖其余结果。</para>
    /// </summary>
    private static void AppendSessionReport(StringBuilder report, string phase, RkdlSession session)
    {
        report.AppendLine(CultureInfo.InvariantCulture,
            $"[{phase}] vid=0x{session.VendorId:X4} pid=0x{session.ProductId:X4} mode={session.Mode} " +
            $"maskrom={session.IsMaskromMode} rockusb={session.IsRockusbMode}");

        try
        {
            var chip = session.ReadChipInfo();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"[{phase}] chip: id=0x{chip.ChipId:X8} tag='{chip.Tag}' name={chip.ChipName} revision=0x{chip.Revision:X8}");
        }
        catch (Exception error)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"[{phase}] chip: FAILED {Describe(error)}");
        }

        try
        {
            var capability = session.ReadCapability();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"[{phase}] capability: raw=0x{Convert.ToHexString(capability.Raw)} " +
                $"directLba={capability.DirectLba} first4M={capability.First4MAccess}");
        }
        catch (Exception error)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"[{phase}] capability: FAILED {Describe(error)}");
        }

        try
        {
            var flashId = session.ReadFlashId();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"[{phase}] flashId: 0x{Convert.ToHexString(flashId)}");
        }
        catch (Exception error)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"[{phase}] flashId: FAILED {Describe(error)}");
        }

        try
        {
            var flash = session.ReadFlashInfo();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"[{phase}] flash: sectors={flash.SectorCount} size={flash.Size} B block={flash.BlockSizeBytes} B " +
                $"blocks={flash.BlockCount} page={flash.PageSizeSectors * (long)flash.SectorSize} B ecc={flash.EccBits} " +
                $"accessTime={flash.AccessTime} manufacturer={flash.ManufacturerName}({flash.ManufacturerCode}) " +
                $"chips=[{string.Join(',', flash.FlashChips)}] raw=0x{Convert.ToHexString(flash.RawResponse)}");
        }
        catch (Exception error)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"[{phase}] flash: FAILED {Describe(error)}");
        }

        if (!session.IsRockusbMode)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"[{phase}] storage: skipped, mask ROM answers no READ_STORAGE.");
            return;
        }

        try
        {
            var mask = session.ReadStorageMask();
            var index = RkdlStorage.IndexOfMask(mask);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"[{phase}] storage: mask 0x{mask:X8} ({(index >= 0 ? RkdlStorage.Names[index] : "unmatched")})");
        }
        catch (Exception error)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"[{phase}] storage: FAILED {Describe(error)}");
        }
    }

    /// <summary>
    /// Rescans for the endpoint the loader download left behind and reports it, so a failed transition
    /// still produces the loader-stage answers instead of only an error.
    /// <para>重新扫描 loader 下载后留下的端点并报告它，使迁移失败时仍能拿到 loader 阶段的应答，而不是只有一条错误。</para>
    /// </summary>
    private static async Task AppendRecoveredReportAsync(StringBuilder report, DeviceMonitorService monitor, CancellationToken token)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var recovered = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Rkdl);
            if (recovered is not null)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"recovered endpoint: key={recovered.Key} serial={recovered.Serial} state={recovered.State} " +
                    $"pid=0x{recovered.ProductId:X4}");
                try
                {
                    await monitor.RunRkdlAsync(recovered, session =>
                    {
                        AppendSessionReport(report, "recovered", session);
                        return Task.FromResult(true);
                    }, token);
                }
                catch (Exception error)
                {
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"recovered session failed: {Describe(error)}");
                }

                return;
            }

            await Task.Delay(1000, token);
        }

        report.AppendLine("recovered endpoint: no RKDL endpoint was rediscovered within 15 s.");
    }

    private static string Describe(Exception error)
        => $"{error.GetType().Name}: {error.Message}";
}