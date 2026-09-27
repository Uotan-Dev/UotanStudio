using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XFlashStorageTransferAsync_DeadlineDuringCallerStream_FaultsAndReleasesGate(bool write)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        session.XFlashStorageTransferTimeoutMs = 100;
        EnqueueTransferStart(transport, read: !write);
        if (!write)
        {
            EnqueueXFlashFrame(transport, new byte[512]);
        }
        using var stream = new DeadlineStorageStream();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<TimeoutException>(() => write
            ? session.WriteXFlashStorageAsync(8, 0, 512, stream, progress, TestContext.Current.CancellationToken)
            : session.ReadXFlashStorageAsync(8, 0, 512, stream, progress, TestContext.Current.CancellationToken));

        Assert.Contains("deadline", error.Message);
        Assert.Equal(before + 4, transport.Writes.Count);
        Assert.Empty(progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXFlashStorageAsync(
            8, 0, 512, output, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86400001)]
    public void XFlashStorageTransferTimeoutMs_InvalidBudget_RejectsWithoutIo(int budget)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);

        Assert.Throws<ArgumentOutOfRangeException>(() => session.XFlashStorageTransferTimeoutMs = budget);

        Assert.Equal(86400000, session.XFlashStorageTransferTimeoutMs);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ReadXFlashStorageAsync_ConsecutiveIdleStatuses_FaultsWithoutAcknowledgementOrReplay()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: true);
        for (var index = 0; index < 65; index++)
        {
            EnqueueXFlashFrame(transport, new byte[4]);
        }
        using var destination = new MemoryStream();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashStorageAsync(
            8, 0, 512, destination, progress, TestContext.Current.CancellationToken));

        Assert.Contains("no-progress", error.Message);
        Assert.Equal(0, destination.Length);
        Assert.Empty(progress.Values);
        Assert.Equal(before + 4, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXFlashStorageAsync(
            8, 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before + 4, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadXFlashStorageAsync_DataBetweenIdleStatuses_ResetsConsecutiveLimit()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: true);
        for (var packet = 0; packet < 2; packet++)
        {
            for (var index = 0; index < 64; index++)
            {
                EnqueueXFlashFrame(transport, new byte[4]);
            }
            EnqueueXFlashFrame(transport, Enumerable.Repeat((byte)(packet + 1), 512).ToArray());
        }
        EnqueueXFlashFrame(transport, new byte[4]);
        using var destination = new MemoryStream();
        var progress = new ProgressRecorder();

        await session.ReadXFlashStorageAsync(8, 0, 1024, destination, progress, TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Repeat((byte)1, 512).Concat(Enumerable.Repeat((byte)2, 512)), destination.ToArray());
        Assert.Equal(new long[] { 512, 1024 }, progress.Values);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    private sealed class DeadlineStorageStream : MemoryStream
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}