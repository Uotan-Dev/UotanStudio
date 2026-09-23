using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.Protocols;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MtkFlashServiceTests
{
    [Fact]
    public async Task PrepareAsync_ValidSelection_PreviewsWithoutWritingAndDisposesOnAbandonment()
    {
        using var transport = new ScriptedCommTransport();
        var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        MtkSessionTests.EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        var before = transport.Writes.Count;

        var preparation = await MtkFlashService.PrepareAsync(_ => Task.FromResult(session), 8,
            [new(MtkFlashStepKind.Erase, "boot", 512, 512)], TestContext.Current.CancellationToken);

        Assert.Equal(MtkFlashPlanState.Prepared, preparation.State);
        Assert.Equal(512, preparation.TotalBytes);
        Assert.Equal(41 * 512, Assert.Single(preparation.Operations).Offset);
        Assert.Equal(4, transport.Writes.Skip(before).Count(bytes => bytes.Length == 56));
        Assert.Equal(0, transport.DisposeCount);
        preparation.Dispose();
        Assert.Equal(MtkFlashPlanState.Disposed, preparation.State);
        Assert.Equal(1, transport.DisposeCount);
        preparation.Dispose();
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitCall_WritesOnceAndRejectsReplay()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteBytes("boot.img", new byte[512]);
        using var transport = new ScriptedCommTransport();
        var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        MtkSessionTests.EnqueueGpt(transport, disk);
        using var preparation = await MtkFlashService.PrepareAsync(_ => Task.FromResult(session), 8,
            [new(MtkFlashStepKind.Raw, "boot", 0, 512, path)], TestContext.Current.CancellationToken);
        MtkSessionTests.EnqueueGpt(transport, disk);
        MtkSessionTests.EnqueuePlanWrite(transport);
        var disposalRejected = false;
        transport.OnWrite = _ =>
        {
            Assert.Throws<InvalidOperationException>(() => preparation.Dispose());
            Assert.Equal(0, transport.DisposeCount);
            disposalRejected = true;
            return [];
        };

        var result = await preparation.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(disposalRejected);
        Assert.Equal(new MtkFlashPlanResult(1, 512, 0, 0), result);
        Assert.Equal(MtkFlashPlanState.Completed, preparation.State);
        var before = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            preparation.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task PrepareAsync_InvalidSource_ReleasesOwnedSession()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteBytes("short.img", new byte[1]);
        using var transport = new ScriptedCommTransport();
        var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        MtkSessionTests.EnqueueGpt(transport, GptReaderTests.CreateDisk(512));

        await Assert.ThrowsAsync<InvalidDataException>(() => MtkFlashService.PrepareAsync(_ => Task.FromResult(session), 8,
            [new(MtkFlashStepKind.Raw, "boot", 0, 512, path)], TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.DisposeCount);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(1, exclusive.Length);
    }

    [Fact]
    public async Task PrepareAsync_CancelledBeforeConnect_DoesNotAcquireSession()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkFlashService.PrepareAsync(_ =>
        {
            called = true;
            throw new InvalidOperationException();
        }, 8, [new(MtkFlashStepKind.Erase, "boot", 0, 512)], cancellation.Token));

        Assert.False(called);
    }

    [Fact]
    public async Task PrepareAsync_SelectionMutatedDuringConnect_UsesOriginalSelection()
    {
        using var transport = new ScriptedCommTransport();
        var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        MtkSessionTests.EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        MtkPartitionFlashStep[] steps = [new(MtkFlashStepKind.Erase, "boot", 0, 512)];

        using var preparation = await MtkFlashService.PrepareAsync(_ =>
        {
            steps[0] = new(MtkFlashStepKind.Erase, "missing", 0, 512);
            return Task.FromResult(session);
        }, 8, steps, TestContext.Current.CancellationToken);

        Assert.Equal(40 * 512, Assert.Single(preparation.Operations).Offset);
    }

    [Fact]
    public async Task PrepareAsync_DamagedGpt_ReleasesOwnedSession()
    {
        using var transport = new ScriptedCommTransport();
        var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        MtkSessionTests.EnqueueGpt(transport, new byte[128 * 512]);

        await Assert.ThrowsAsync<InvalidDataException>(() => MtkFlashService.PrepareAsync(_ => Task.FromResult(session), 8,
            [new(MtkFlashStepKind.Erase, "boot", 0, 512)], TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task PrepareAsync_CancelledDuringConnect_DisposesBeforeReadingGpt()
    {
        using var transport = new ScriptedCommTransport();
        var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkFlashService.PrepareAsync(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(session);
        }, 8, [new(MtkFlashStepKind.Erase, "boot", 0, 512)], cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(1, transport.DisposeCount);
    }
}