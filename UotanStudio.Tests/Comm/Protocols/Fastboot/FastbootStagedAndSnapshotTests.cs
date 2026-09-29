using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Fastboot.Images;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Fastboot;

/// <summary>
/// Staged-data and snapshot-update tests: a staged transfer must surface its terminal frame instead of
/// leaving a silently truncated file, and a device asking for a fastbootd reboot must never be rebooted
/// by the session on its own.
/// <para>暂存数据与快照更新测试：暂存传输必须暴露其终止帧，而不是留下一个被静默截断的文件；设备请求
/// 重启到 fastbootd 时，会话绝不能自行重启设备。</para>
/// </summary>
public class FastbootStagedAndSnapshotTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static FastbootSession NewSession(ScriptedCommTransport transport)
        => new(transport, timeoutMs: 250);

    private static List<string> Commands(ScriptedCommTransport transport)
        => [.. transport.Writes.Select(Encoding.UTF8.GetString)];

    [Fact]
    public void GetStagedToStream_DeviceConfirmsTransfer_ReturnsSuccessResponse()
    {
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);
        var transport = new ScriptedCommTransport();
        transport.Enqueue($"DATA{payload.Length:x8}");
        transport.Enqueue(payload);
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        using var output = new MemoryStream();
        var response = session.GetStagedToStream(output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FastbootResponseState.Success, response.State);
        Assert.Equal(payload, output.ToArray());
        Assert.Equal(["get_staged"], Commands(transport));
    }

    [Fact]
    public void GetStagedToStream_DeviceFailsAfterPayload_SurfacesFailure()
    {
        var payload = new byte[32];
        var transport = new ScriptedCommTransport();
        transport.Enqueue($"DATA{payload.Length:x8}");
        transport.Enqueue(payload);
        transport.Enqueue("FAILremote fault");

        using var session = NewSession(transport);
        using var output = new MemoryStream();
        var response = session.GetStagedToStream(output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FastbootResponseState.Fail, response.State);
        Assert.Equal("remote fault", response.Info);
        Assert.Throws<FastbootException>(response.ThrowIfFailed);
    }

    [Fact]
    public void GetStaged_DeviceRejectsCommand_ReturnsFailureWithoutPayload()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("FAILunknown command");

        using var session = NewSession(transport);
        using var dir = new TempDirectory();
        var outputPath = Path.Combine(dir.Path, "staged.img");

        Assert.Throws<FastbootException>(() => session.GetStaged(outputPath,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, new FileInfo(outputPath).Length);
    }

    [Fact]
    public void GetStaged_WritesPayloadAndReportsProgress()
    {
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        var transport = new ScriptedCommTransport();
        transport.Enqueue($"DATA{payload.Length:x8}");
        transport.Enqueue(payload);
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        using var dir = new TempDirectory();
        var outputPath = Path.Combine(dir.Path, "staged.img");
        var progress = new RecordingProgress();

        var response = session.GetStaged(outputPath, progress, TestContext.Current.CancellationToken);

        Assert.Equal(FastbootResponseState.Success, response.State);
        Assert.Equal(payload, File.ReadAllBytes(outputPath));
        Assert.Equal(payload.Length, Assert.Single(progress.Values));
    }

    [Fact]
    public void GetStaged_CancelledToken_ThrowsBeforeWritingPayload()
    {
        var transport = new ScriptedCommTransport();
        using var session = NewSession(transport);
        using var dir = new TempDirectory();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => session.GetStaged(
            Path.Combine(dir.Path, "staged.img"), cancellationToken: cancelled.Token));
    }

    [Fact]
    public void SnapshotUpdate_DeviceAsksForFastbootdReboot_ReportsRequestWithoutRebooting()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("INFOFastboot doesn't support updating super partition metadata");
        transport.Enqueue("INFOReboot into fastbootd: reboot fastboot");
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        var response = session.SnapshotUpdate("cancel");

        Assert.Equal(FastbootResponseState.Success, response.State);
        Assert.True(response.RequestsFastbootReboot);
        Assert.Equal(2, response.InfoLines.Count);
        Assert.Equal(["snapshot-update:cancel"], Commands(transport));
    }

    [Fact]
    public void SnapshotUpdate_PlainReply_DoesNotReportRebootRequest()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        var response = session.SnapshotUpdate("cancel");

        Assert.False(response.RequestsFastbootReboot);
        Assert.Equal(["snapshot-update:cancel"], Commands(transport));
    }

    [Fact]
    public void CancelSnapshotIfNeeded_PendingMergeWithRebootHint_ReportsRebootRequestAndDoesNotReboot()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAYmerging");
        transport.Enqueue("INFOreboot fastboot to continue");
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);

        Assert.True(session.CancelSnapshotIfNeeded());
        Assert.Equal(["getvar:snapshot-update-status", "snapshot-update:cancel"], Commands(transport));
    }

    [Fact]
    public void CancelSnapshotIfNeeded_PendingMergeCompleting_DoesNotReportRebootRequest()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAYmerging");
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);

        Assert.False(session.CancelSnapshotIfNeeded());
        Assert.Equal(["getvar:snapshot-update-status", "snapshot-update:cancel"], Commands(transport));
    }

    [Fact]
    public void CancelSnapshotIfNeeded_NoPendingMerge_IssuesNoSnapshotCommand()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAYnone");

        using var session = NewSession(transport);

        Assert.False(session.CancelSnapshotIfNeeded());
        Assert.Equal(["getvar:snapshot-update-status"], Commands(transport));
    }

    [Fact]
    public void CancelSnapshotIfNeeded_CancelRejected_PropagatesFailure()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAYmerging");
        transport.Enqueue("FAILcannot cancel");

        using var session = NewSession(transport);

        Assert.Throws<FastbootException>(() => session.CancelSnapshotIfNeeded());
    }

    [Fact]
    public void IsLogicalOptimized_PreloadedMetadataIsEmpty_FallsBackToDeviceQuery()
    {
        using var dir = new TempDirectory();
        var notSuper = dir.WriteBytes("junk.img", new byte[16384]);
        var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Utf8("OKAYyes")];

        using var session = NewSession(transport);
        session.LoadLogicalPartitionsFromMetadata(notSuper);

        Assert.True(session.IsLogicalOptimized("system_a"));
        Assert.Contains("getvar:is-logical:system_a", Commands(transport));
    }

    [Fact]
    public void IsLogicalOptimized_PreloadedMetadataListsPartition_AnswersWithoutDeviceQuery()
    {
        using var dir = new TempDirectory();
        var superPath = dir.WriteBytes("super.img", BuildSuperImage());
        var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Utf8("OKAYyes")];

        using var session = NewSession(transport);
        session.LoadLogicalPartitionsFromMetadata(superPath);

        Assert.True(session.IsLogicalOptimized("system"));
        Assert.False(session.IsLogicalOptimized("boot_a"));
        Assert.DoesNotContain(Commands(transport), c => c.StartsWith("getvar:", StringComparison.Ordinal));
    }

    [Fact]
    public void IsLogicalOptimized_MissingMetadataFile_FallsBackToDeviceQuery()
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Utf8("OKAYno")];

        using var session = NewSession(transport);
        session.LoadLogicalPartitionsFromMetadata(
            Path.Combine(Path.GetTempPath(), $"uotan-missing-super-{Guid.NewGuid():N}.img"));

        Assert.False(session.IsLogicalOptimized("boot_a"));
        Assert.Contains("getvar:is-logical:boot_a", Commands(transport));
    }

    private static byte[] BuildSuperImage()
        => new SuperImageBuilder().Build(
            [new SuperBuildPartition("system", "default", 0x10000)],
            deviceSize: 4 * 1024 * 1024,
            metadataMaxSize: 64 * 1024,
            metadataSlotCount: 2);

    private sealed class RecordingProgress : IProgress<long>
    {
        public List<long> Values { get; } = [];

        public void Report(long value) => Values.Add(value);
    }
}