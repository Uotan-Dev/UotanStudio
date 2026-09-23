using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.Protocols;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

public class MtkScatterServiceTests
{
    [Fact]
    public async Task PrepareScatterAsync_ValidRawSelection_PreviewsThenExecutesExplicitly()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("boot.img", new byte[512]);
        using var transport = new ScriptedCommTransport();
        var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        MtkSessionTests.EnqueueGpt(transport, disk);
        var before = transport.Writes.Count;

        using var preparation = await MtkFlashService.PrepareScatterAsync(_ => Task.FromResult(session), 8,
            MtkSessionTests.ScatterLayout(MtkXFlashStorageKind.Emmc),
            [new("SYS2", MtkFlashStepKind.Raw, 0, 512, path)], TestContext.Current.CancellationToken);

        Assert.Equal(MtkFlashPlanState.Prepared, preparation.State);
        Assert.Equal(512, preparation.TotalBytes);
        Assert.Equal(40 * 512, Assert.Single(preparation.Operations).Offset);
        Assert.Equal(4, transport.Writes.Skip(before).Count(bytes => bytes.Length == 56));
        MtkSessionTests.EnqueueGpt(transport, disk);
        MtkSessionTests.EnqueuePlanWrite(transport);
        var result = await preparation.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new MtkFlashPlanResult(1, 512, 0, 0), result);
        preparation.Dispose();
        Assert.Equal(1, transport.DisposeCount);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(512, exclusive.Length);
    }

    [Fact]
    public async Task PrepareScatterAsync_CallerMutatesCollectionsDuringConnect_UsesCapturedInput()
    {
        using var transport = new ScriptedCommTransport();
        var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        MtkSessionTests.EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        var original = MtkSessionTests.ScatterLayout(MtkXFlashStorageKind.Emmc);
        var fields = original.Partitions[0].Fields.ToDictionary();
        var partitions = original.Partitions.ToArray();
        partitions[0] = partitions[0] with { Fields = fields };
        var layout = original with { Partitions = partitions };
        MtkScatterFlashStep[] selections = [new("SYS2", MtkFlashStepKind.Erase, 0, 512)];
        var ready = new TaskCompletionSource<MtkSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        var preparing = MtkFlashService.PrepareScatterAsync(_ => ready.Task, 8, layout, selections,
            TestContext.Current.CancellationToken);
        Assert.False(preparing.IsCompleted);
        fields["empty_boot_needed"] = "true";
        partitions[0] = partitions[0] with { Name = "missing" };
        selections[0] = new("SYS0", MtkFlashStepKind.Erase, 0, 512);
        ready.SetResult(session);
        using var preparation = await preparing;

        Assert.Equal(40 * 512, Assert.Single(preparation.Operations).Offset);
        Assert.Equal(512, preparation.TotalBytes);
    }

    [Fact]
    public async Task PrepareScatterAsync_GptMismatch_ReleasesConnectionWithoutWrites()
    {
        using var transport = new ScriptedCommTransport();
        var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        MtkSessionTests.EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        var original = MtkSessionTests.ScatterLayout(MtkXFlashStorageKind.Emmc);
        var layout = original with { Partitions = [original.Partitions[0] with { Size = 512 }] };
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() => MtkFlashService.PrepareScatterAsync(
            _ => Task.FromResult(session), 8, layout, [new("SYS2", MtkFlashStepKind.Erase, 0, 512)],
            TestContext.Current.CancellationToken));

        Assert.Equal(4, transport.Writes.Skip(before).Count(bytes => bytes.Length == 56));
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null-step")]
    [InlineData("oversized-fields")]
    public async Task PrepareScatterAsync_InvalidLocalSelection_DoesNotConnect(string failure)
    {
        var layout = MtkSessionTests.ScatterLayout(MtkXFlashStorageKind.Emmc);
        MtkScatterFlashStep[] steps = failure switch
        {
            "empty" => [],
            "null-step" => [null!],
            _ => [new("SYS2", MtkFlashStepKind.Erase, 0, 512)]
        };
        if (failure == "oversized-fields")
        {
            layout = layout with { Fields = new Dictionary<string, string> { ["x"] = new('x', 4097) } };
        }
        var connected = false;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => MtkFlashService.PrepareScatterAsync(_ =>
        {
            connected = true;
            throw new InvalidOperationException();
        }, 8, layout, steps, TestContext.Current.CancellationToken));

        Assert.False(connected);
    }

    [Fact]
    public async Task PrepareScatterAsync_PreCancelled_DoesNotConnect()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var connected = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkFlashService.PrepareScatterAsync(_ =>
        {
            connected = true;
            throw new InvalidOperationException();
        }, 8, MtkSessionTests.ScatterLayout(MtkXFlashStorageKind.Emmc),
            [new("SYS2", MtkFlashStepKind.Erase, 0, 512)], cancellation.Token));

        Assert.False(connected);
    }
}