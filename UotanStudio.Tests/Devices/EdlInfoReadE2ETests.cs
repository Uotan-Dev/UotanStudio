using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Real-device checks for the EDL device-information read: the Sahara stage must report the bootrom facts,
/// the Firehose stage must report the storage facts once a programmer runs, and every row must carry the
/// stage that produced it. Set <c>UOTAN_EDL_INFO_E2E=1</c> with a connected EDL target to run; the Firehose
/// half additionally needs <c>UOTAN_EDL_PROGRAMMER</c>.
/// <para>EDL 设备信息读取的真机检查：Sahara 阶段必须报告 bootrom 事实，Firehose 阶段必须在 programmer
/// 运行后报告存储事实，且每一行都必须标明产生它的阶段。设 <c>UOTAN_EDL_INFO_E2E=1</c> 并接入 EDL 目标
/// 后运行；Firehose 部分还要求 <c>UOTAN_EDL_PROGRAMMER</c>。</para>
/// </summary>
public sealed class EdlInfoReadE2ETests
{
    private const string EnableVariable = "UOTAN_EDL_INFO_E2E";
    private const string ProgrammerVariable = "UOTAN_EDL_PROGRAMMER";

    [Fact]
    public async Task SaharaStage_ReportsHelloFactsAndFormatsThem()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal),
            $"{EnableVariable} is not set to 1; connect an EDL target and opt in to run this loop.");

        using var monitor = DeviceMonitorService.Create();
        var device = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Edl);
        Assert.SkipWhen(device is null, "No EDL endpoint was discovered on this host.");

        var snapshot = await monitor.RunEdlAsync(device!, session =>
        {
            return EdlInfoCollector.CollectSaharaAsync(session, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        Assert.SkipWhen(snapshot.Protocol == EdlProtocolState.Firehose,
            "The target already runs Firehose, so no Sahara facts exist to read; the Firehose loop covers that state.");
        Assert.NotNull(snapshot.Sahara);
        var rows = EdlInfoFormatter.Format(snapshot);
        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row.Key == EdlInfoFormatter.ProtocolKey);
        Assert.Contains(rows, row => row.Key == EdlInfoFormatter.SaharaVersionKey && row.Stage == EdlInfoStage.Sahara);
        Assert.All(rows, row => Assert.NotEmpty(row.Value));
    }

    [Fact]
    public async Task FirehoseStage_ReportsStorageFactsAfterProgrammerUpload()
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

        var snapshot = await monitor.RunEdlAsync(device!, async session =>
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var sahara = await EdlInfoCollector.CollectSaharaAsync(session, budget.Token);
            await using var programmer = new FileStream(programmerPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            await session.InitializeAsync(programmer, budget.Token);
            var firehose = await EdlInfoCollector.CollectFirehoseAsync(session, 0, true, budget.Token);
            return EdlInfoCollector.Merge(sahara, firehose);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(EdlProtocolState.Firehose, snapshot.Protocol);
        var rows = EdlInfoFormatter.Format(snapshot);
        Assert.Contains(rows, row => row.Key == EdlInfoFormatter.StorageTypeKey && row.Stage == EdlInfoStage.Firehose);
        Assert.Contains(rows, row => row.Key == EdlInfoFormatter.SectorSizeKey && row.Stage == EdlInfoStage.Firehose);
        Assert.Contains(rows, row => row.Key == EdlInfoFormatter.ProtocolKey);
        Assert.All(rows, row => Assert.NotEmpty(row.Value));
    }

    [Fact]
    public async Task RepeatRead_RowSetStaysStable()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal),
            $"{EnableVariable} is not set to 1; connect an EDL target and opt in to run this loop.");

        using var monitor = DeviceMonitorService.Create();
        var device = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Edl);
        Assert.SkipWhen(device is null, "No EDL endpoint was discovered on this host.");

        var keys = new List<IReadOnlyList<string>>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = await monitor.RunEdlAsync(device!, session =>
                EdlInfoCollector.CollectSaharaAsync(session, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);
            keys.Add([.. EdlInfoFormatter.Format(snapshot).Select(row => row.Key)]);
        }

        Assert.SkipWhen(keys[0].Count == 0,
            "The target runs Firehose, so a Sahara read yields no rows to compare; the Firehose loop covers that state.");
        Assert.All(keys, set => Assert.Equal(keys[0], set));
    }
}