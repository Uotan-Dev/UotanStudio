using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Fact]
    public async Task ShutdownXFlashAsync_LocalDisconnectFails_RetainsAcknowledgedTerminalState()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        transport.OnDisconnect = () => throw new IOException("Local disconnect failed.");
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<IOException>(() => session.ShutdownXFlashAsync(
            MtkXFlashShutdownMode.PowerOff, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.XFlashShutdownAcknowledged, session.State);
        Assert.True(transport.IsConnected);
        Assert.False(session.IsConnected);
        Assert.Null(session.XFlashStorage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ShutdownXFlashAsync(
            MtkXFlashShutdownMode.PowerOff, TestContext.Current.CancellationToken));
        Assert.Equal(4, transport.Writes.Count - before);
        session.Dispose();
        Assert.Equal(MtkSessionState.Disposed, session.State);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task ShutdownXFlashAsync_BootHandshakeNotStarted_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ShutdownXFlashAsync(
            MtkXFlashShutdownMode.PowerOff, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(MtkSessionState.Created, session.State);
    }

    [Theory]
    [InlineData(MtkXFlashShutdownMode.PowerOff)]
    [InlineData(MtkXFlashShutdownMode.NormalBoot)]
    [InlineData(MtkXFlashShutdownMode.Fastboot)]
    public async Task ShutdownXFlashAsync_TwoAcknowledgements_SendsExplicitModeAndTerminates(MtkXFlashShutdownMode mode)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        var before = transport.Writes.Count;

        await session.ShutdownXFlashAsync(mode, TestContext.Current.CancellationToken);

        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(Convert.FromHexString("07000100"), transport.Writes[before + 1]);
        var expected = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, mode == MtkXFlashShutdownMode.PowerOff ? 0u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(12), (uint)mode);
        Assert.Equal(expected, transport.Writes[before + 3]);
        Assert.Equal(MtkSessionState.XFlashShutdownAcknowledged, session.State);
        Assert.False(session.IsConnected);
        Assert.False(transport.IsConnected);
        Assert.Null(session.XFlashStorage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ShutdownXFlashAsync(mode, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EraseXFlashStorageAsync(
            8, 0, 512, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(4, transport.Writes.Count - before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ShutdownXFlashAsync_RejectedOrMissingStatus_FaultsWithoutRetry(bool requestAccepted, bool missingFinal)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueXFlashValue(transport, requestAccepted ? 0u : 0xC0010001u);
        if (requestAccepted && !missingFinal)
        {
            EnqueueXFlashValue(transport, 0xC0010001);
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ShutdownXFlashAsync(
            MtkXFlashShutdownMode.PowerOff, TestContext.Current.CancellationToken));

        Assert.Equal(requestAccepted ? 4 : 2, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.False(session.IsConnected);
        Assert.Null(session.XFlashStorage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ShutdownXFlashAsync(
            MtkXFlashShutdownMode.PowerOff, TestContext.Current.CancellationToken));
        Assert.Equal(requestAccepted ? 4 : 2, transport.Writes.Count - before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownXFlashAsync_Cancellation_DistinguishesPreIoAndUncertainRequest(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
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
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ShutdownXFlashAsync(
            MtkXFlashShutdownMode.PowerOff, cancellation.Token));

        Assert.Equal(inFlight ? 1 : 0, transport.Writes.Count - before);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ShutdownXFlashAsync_InvalidMode_RejectsWithoutChangingSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ShutdownXFlashAsync(
            (MtkXFlashShutdownMode)3, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ShutdownXFlashAsync_StorageOperationQueuedDuringShutdown_CannotReachTransport()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        Task? queued = null;
        transport.OnWrite = _ =>
        {
            queued ??= session.EraseXFlashStorageAsync(8, 0, 512, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            return [];
        };
        var before = transport.Writes.Count;

        await session.ShutdownXFlashAsync(MtkXFlashShutdownMode.NormalBoot, TestContext.Current.CancellationToken);

        Assert.NotNull(queued);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Equal(4, transport.Writes.Count - before);
    }
}