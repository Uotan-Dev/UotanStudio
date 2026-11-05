using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Real-device EDL closed-loop checks: the connected Qualcomm 9008 target must answer the protocol probe
/// with Sahara on every repeated attempt, accept a Firehose programmer upload, and then still report Firehose
/// to a fresh probe. Set <c>UOTAN_EDL_E2E=1</c> with a connected EDL target to run; the upload steps additionally
/// require <c>UOTAN_EDL_PROGRAMMER</c> pointing at a Firehose programmer image. The suite skips itself everywhere
/// else.
/// <para>真机 EDL 闭环检查：已接入的高通 9008 目标必须在每次重复探测时都以 Sahara 应答，能够接受
/// Firehose programmer 上传，并在之后的新一次探测中仍报告 Firehose。设 <c>UOTAN_EDL_E2E=1</c> 并接入
/// EDL 目标后运行；上传步骤还要求 <c>UOTAN_EDL_PROGRAMMER</c> 指向一个 Firehose programmer 镜像。
/// 其余环境一律自跳。</para>
/// </summary>
public sealed class EdlDeviceE2ETests
{
    private const string EnableVariable = "UOTAN_EDL_E2E";
    private const string ProgrammerVariable = "UOTAN_EDL_PROGRAMMER";
    private const int ProbeAttempts = 5;
    private const int ProbeBudgetSeconds = 20;

    [Fact]
    public async Task EdlEndpoint_RepeatedProbe_ReportsSaharaEveryTime()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal),
            $"{EnableVariable} is not set to 1; connect an EDL target and opt in to run this loop.");

        using var monitor = DeviceMonitorService.Create();
        var device = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Edl);
        Assert.SkipWhen(device is null, "No EDL endpoint was discovered on this host.");

        var observed = new List<EdlProtocolState>(ProbeAttempts);
        for (var attempt = 1; attempt <= ProbeAttempts; attempt++)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeBudgetSeconds));
            observed.Add(await monitor.ProbeEdlProtocolAsync(device!, budget.Token));
        }

        Assert.DoesNotContain(EdlProtocolState.Unknown, observed);
        Assert.All(observed, state => Assert.True(
            state is EdlProtocolState.Sahara or EdlProtocolState.SaharaMemoryDebug or EdlProtocolState.Firehose,
            $"Probe attempt {observed.IndexOf(state) + 1} reported {state}, which is not a validated EDL protocol."));
    }
    [Fact]
    public async Task EdlSession_UploadsProgrammer_ThenReportsFirehose()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal),
            $"{EnableVariable} is not set to 1; connect an EDL target and opt in to run this loop.");
        var programmerPath = Environment.GetEnvironmentVariable(ProgrammerVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(programmerPath),
            $"{ProgrammerVariable} is not set to a Firehose programmer image.");
        Assert.SkipWhen(!File.Exists(programmerPath),
            $"{ProgrammerVariable} points at '{programmerPath}', which does not exist.");

        using var monitor = DeviceMonitorService.Create();
        var device = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Edl);
        Assert.SkipWhen(device is null, "No EDL endpoint was discovered on this host.");

        var result = await monitor.RunEdlAsync(device!, async session =>
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await using var programmer = new FileStream(programmerPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            await session.InitializeAsync(programmer, budget.Token);
            return (session.ProtocolState, session.IsConfigured, session.Backend);
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsConfigured, "The programmer upload did not leave the session configured.");
        Assert.Equal(EdlProtocolState.Firehose, result.ProtocolState);
        Assert.Equal(EdlDownloadBackend.Firehose, result.Backend);
    }

    [Fact]
    public async Task EdlEndpoint_AfterFirehoseEntry_ProbeStillReportsFirehose()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal),
            $"{EnableVariable} is not set to 1; connect an EDL target and opt in to run this loop.");

        using var monitor = DeviceMonitorService.Create();
        var device = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Edl);
        Assert.SkipWhen(device is null, "No EDL endpoint was discovered on this host.");

        var state = await EnsureFirehoseAsync(monitor, device!);
        Assert.SkipWhen(state != EdlProtocolState.Firehose,
            $"The target reports {state} rather than Firehose; set {ProgrammerVariable} so this loop can bring it there.");

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeBudgetSeconds));
        var reprobed = await monitor.ProbeEdlProtocolAsync(device!, budget.Token);
        Assert.Equal(EdlProtocolState.Firehose, reprobed);
    }

    [Fact]
    public async Task EdlEndpoint_FirehoseRepeatProbe_ReportsFirehoseEveryTime()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal),
            $"{EnableVariable} is not set to 1; connect an EDL target and opt in to run this loop.");

        using var monitor = DeviceMonitorService.Create();
        var device = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Edl);
        Assert.SkipWhen(device is null, "No EDL endpoint was discovered on this host.");

        var state = await EnsureFirehoseAsync(monitor, device!);
        Assert.SkipWhen(state != EdlProtocolState.Firehose,
            $"The target reports {state} rather than Firehose; set {ProgrammerVariable} so this loop can bring it there.");

        var observed = new List<EdlProtocolState>(ProbeAttempts);
        for (var attempt = 1; attempt <= ProbeAttempts; attempt++)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeBudgetSeconds));
            observed.Add(await monitor.ProbeEdlProtocolAsync(device!, budget.Token));
        }

        Assert.DoesNotContain(EdlProtocolState.Unknown, observed);
        Assert.All(observed, state => Assert.Equal(EdlProtocolState.Firehose, state));
    }

    /// <summary>
    /// Brings the target to Firehose when a programmer image is configured, so the Firehose loops do not
    /// depend on another test having run first, and reports the state the probe finally observes.
    /// <para>在配置了 programmer 镜像时把目标带到 Firehose，使 Firehose 循环不依赖其他用例先运行，
    /// 并报告探测最终观测到的状态。</para>
    /// </summary>
    /// <param name="monitor">The device monitor owning the EDL session cache.
    /// <para>持有 EDL 会话缓存的设备监视器。</para></param>
    /// <param name="device">The discovered EDL endpoint. <para>已发现的 EDL 端点。</para></param>
    /// <returns>The protocol state the probe reports after the attempt.
    /// <para>该次尝试之后探测报告出的协议状态。</para></returns>
    private static async Task<EdlProtocolState> EnsureFirehoseAsync(
        DeviceMonitorService monitor, DetectedDevice device)
    {
        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeBudgetSeconds)))
        {
            var current = await monitor.ProbeEdlProtocolAsync(device, budget.Token);
            if (current == EdlProtocolState.Firehose)
            {
                return current;
            }
        }

        var programmerPath = Environment.GetEnvironmentVariable(ProgrammerVariable);
        if (string.IsNullOrWhiteSpace(programmerPath) || !File.Exists(programmerPath))
        {
            return EdlProtocolState.Unknown;
        }

        await monitor.RunEdlAsync(device, async session =>
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await using var programmer = new FileStream(programmerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await session.InitializeAsync(programmer, budget.Token);
            return session.ProtocolState;
        }, TestContext.Current.CancellationToken);

        using var verify = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeBudgetSeconds));
        return await monitor.ProbeEdlProtocolAsync(device, verify.Token);
    }
}