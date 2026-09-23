using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkXFlashStorageKind.Emmc, 8u, 0)]
    [InlineData(MtkXFlashStorageKind.Ufs, 3u, 2)]
    public async Task EraseXFlashStorageAsync_ExplicitCompletion_PreservesSession(MtkXFlashStorageKind kind, uint partition, int continuations)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport, kind);
        EnqueueTransferStart(transport, read: false);
        for (var index = 0; index < continuations; index++)
        {
            EnqueueXFlashValue(transport, 0x40040004);
            EnqueueXFlashValue(transport, 0);
        }
        EnqueueXFlashValue(transport, 0x40040005);
        var before = transport.Writes.Count;

        await session.EraseXFlashStorageAsync(partition, 512, 512, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("03000100"), transport.Writes[before + 1]);
        var parameters = transport.Writes[before + 3];
        Assert.Equal(56, parameters.Length);
        Assert.Equal(kind == MtkXFlashStorageKind.Emmc ? 1u : 0x30u, BinaryPrimitives.ReadUInt32LittleEndian(parameters));
        Assert.Equal(partition, BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(4)));
        Assert.Equal(512ul, BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(8)));
        Assert.Equal(512ul, BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(16)));
        Assert.Equal(4 + continuations * 2, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0xFEEEEEEFu)]
    [InlineData(0xC0070004u)]
    public async Task EraseXFlashStorageAsync_NotACompletionStatus_FaultsWithoutRetry(uint status)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, status);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseXFlashStorageAsync(
            8, 0, 512, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EraseXFlashStorageAsync(
            8, 0, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(status, error.Status);
        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
    }

    [Theory]
    [InlineData(60001u)]
    [InlineData(uint.MaxValue)]
    public async Task EraseXFlashStorageAsync_UnboundedWait_RejectsWithoutAcknowledging(uint delay)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0x40040004);
        EnqueueXFlashValue(transport, delay);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseXFlashStorageAsync(
            8, 0, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task EraseXFlashStorageAsync_DeadlineDuringWait_FaultsWithUncertainOutcome()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0x40040004);
        EnqueueXFlashValue(transport, 60000);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => session.EraseXFlashStorageAsync(
            8, 0, 512, timeoutMs: 100, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("may still be erasing", error.Message, StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EraseXFlashStorageAsync_Cancellation_DistinguishesPreIoAndInFlight(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var cancellation = new CancellationTokenSource();
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.EraseXFlashStorageAsync(8, 0, 512, cancellationToken: cancellation.Token));

        Assert.Equal(before + (inFlight ? 1 : 0), transport.Writes.Count);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task EraseXFlashStorageAsync_ExcessiveContinuations_RejectsBoundedly()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        for (var index = 0; index < 4096; index++)
        {
            EnqueueXFlashValue(transport, 0x40040004);
            EnqueueXFlashValue(transport, 0);
        }
        EnqueueXFlashValue(transport, 0x40040004);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseXFlashStorageAsync(
            8, 0, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(3u, 0L, 512L, 1000)]
    [InlineData(8u, 4096L, 512L, 1000)]
    [InlineData(8u, 1L, 512L, 1000)]
    [InlineData(8u, 0L, 512L, 0)]
    public async Task EraseXFlashStorageAsync_InvalidRequest_RejectsBeforeIo(uint partition, long offset, long length, int timeout)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<Exception>(() => session.EraseXFlashStorageAsync(
            partition, offset, length, timeout, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    private static void EnqueueXFlashValue(ScriptedCommTransport transport, uint value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        EnqueueXFlashFrame(transport, payload);
    }
}