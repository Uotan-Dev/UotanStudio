using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Fact]
    public async Task NandTransferAsync_CancelledQueuedRead_PreservesExclusiveActiveWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        using var source = new PausedNandSource();
        using var destination = new MemoryStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var options = NandOptions(MtkXFlashNandTransferMode.PageAndSpare);
        var write = session.WriteXFlashNandStorageAsync(0, 2048, options, source,
            cancellationToken: TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        try
        {
            var read = session.ReadXFlashNandStorageAsync(0, 2048, options, destination, cancellationToken: cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.Equal(before, transport.Writes.Count);
        }
        finally
        {
            source.Resume.TrySetResult();
            await write;
        }
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NandTransferAsync_FinalRejection_FaultsWithoutReplay(bool write)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        EnqueueTransferStart(transport, read: !write);
        EnqueueXFlashFrame(transport, write ? new byte[4] : new byte[2048]);
        EnqueueXFlashValue(transport, 0xC0070004);
        using var stream = new MemoryStream(new byte[2048]);
        var options = NandOptions();

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => write
            ? session.WriteXFlashNandStorageAsync(0, 2048, options, stream, cancellationToken: TestContext.Current.CancellationToken)
            : session.ReadXFlashNandStorageAsync(0, 2048, options, stream, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0xC0070004u, error.Status);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
        var after = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXFlashNandStorageAsync(
            0, 2048, options, stream, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(after, transport.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NandTransferAsync_DeadlineDuringCallerStream_FaultsAndReleasesGate(bool write)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        session.XFlashStorageTransferTimeoutMs = 100;
        EnqueueTransferStart(transport, read: !write);
        if (!write)
        {
            EnqueueXFlashFrame(transport, new byte[2048]);
        }
        using var stream = new DeadlineStorageStream();
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        await Assert.ThrowsAsync<TimeoutException>(() => write
            ? session.WriteXFlashNandStorageAsync(0, 2048, NandOptions(), stream, progress, TestContext.Current.CancellationToken)
            : session.ReadXFlashNandStorageAsync(0, 2048, NandOptions(), stream, progress, TestContext.Current.CancellationToken));

        Assert.Equal(before + 4, transport.Writes.Count);
        Assert.Empty(progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXFlashNandStorageAsync(
            0, 2048, NandOptions(), stream, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadXFlashNandStorageAsync_Cancellation_DistinguishesBeforeIoAndInFlight(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        var before = transport.Writes.Count;
        if (inFlight)
        {
            transport.OnWrite = _ =>
            {
                cancellation.Cancel();
                return [];
            };
        }
        else
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXFlashNandStorageAsync(
            0, 2048, NandOptions(), output, cancellationToken: cancellation.Token));

        Assert.Equal(before + (inFlight ? 1 : 0), transport.Writes.Count);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("short")]
    [InlineData("type")]
    [InlineData("idle")]
    public async Task ReadXFlashNandStorageAsync_InvalidPacket_RejectsWithoutAcknowledgingOrPublishing(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        EnqueueTransferStart(transport, read: true);
        if (failure == "idle")
        {
            for (var index = 0; index < 65; index++)
            {
                EnqueueXFlashValue(transport, 0);
            }
        }
        else
        {
            EnqueueXFlashFrame(transport, new byte[failure switch { "oversized" => 4097, "short" => 3, _ => 2048 }],
                failure == "type" ? 2u : 1u);
        }
        using var output = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashNandStorageAsync(
            0, 2048, NandOptions(), output, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, output.Length);
        Assert.Equal(before + 4, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private sealed class PausedNandSource() : MemoryStream(new byte[2112])
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}