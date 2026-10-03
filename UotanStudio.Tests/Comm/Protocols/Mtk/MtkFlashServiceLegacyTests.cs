using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task PrepareLegacyAsync_PreviewResolvesNamedExtentWithoutDeviceWrites()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, GptReaderTests.CreateDisk(512, 128, LegacyGptBlockCount));
        using var dir = new TempDirectory();
        var image = dir.WriteBytes("boot.img", Enumerable.Repeat((byte)0x33, 512).ToArray());
        var before = transport.Writes.Count;

        using var preview = await MtkFlashService.PrepareLegacyAsync(
            _ => Task.FromResult(session), 8,
            [new MtkLegacyPartitionFlashStep("boot", MtkFlashStepKind.Raw, 0, 512, image)],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, preview.OperationCount);
        Assert.Equal(512, preview.TotalBytes);
        Assert.Equal(40 * 512, preview.Operations[0].Offset);
        Assert.Equal(8, preview.Operations[0].PartitionId);
        Assert.Equal(MtkFlashPlanState.Prepared, preview.State);
        Assert.True(transport.Writes.Count > before);
    }

    [Fact]
    public async Task PrepareLegacyAsync_ConnectorWithoutSession_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => MtkFlashService.PrepareLegacyAsync(
            _ => Task.FromResult<MtkSession>(null!), 8,
            [new MtkLegacyPartitionFlashStep("boot", MtkFlashStepKind.Erase, 0, 512)],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrepareLegacyAsync_EmptySelection_IsRejectedBeforeConnect()
    {
        var connected = false;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MtkFlashService.PrepareLegacyAsync(
            _ =>
            {
                connected = true;
                return Task.FromResult<MtkSession>(null!);
            }, 8, [], TestContext.Current.CancellationToken));

        Assert.False(connected);
    }
}