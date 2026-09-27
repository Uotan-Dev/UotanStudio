using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(0x6592)]
    [InlineData(0x8127)]
    [InlineData(0x8163)]
    public async Task LegacyBootstrapAsync_ChipSpecificTrailingFields_DoesNotConsumeNextCommand(ushort hardwareCode)
    {
        using var dir = new TempDirectory();
        var options = LegacyBootstrapOptions(dir);
        var bytes = BootstrapContainer();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(110), hardwareCode);
        dir.WriteBytes("loader.bin", bytes);
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, false, hardwareCode: hardwareCode);
        transport.Enqueue([0x5A, 0x5A]);

        using var session = await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy, options,
            TestContext.Current.CancellationToken);
        await session.ShutdownLegacyAsync(MtkLegacyShutdownMode.NormalBoot, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
    }

    [Fact]
    public async Task WriteLegacyStorageAsync_BlockedSource_ExpiresDeadlineAndFaults()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue([0x5A]);
        using var source = new PausedStorageSource();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<TimeoutException>(() => session.WriteLegacyStorageAsync(8, 0, 512, source,
            timeoutMs: 50, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyBootstrapAsync_Cancelled_ClosesWithoutReplayingStartup(bool inFlight)
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        EnqueueLegacyBootstrap(transport, false);
        if (inFlight)
        {
            transport.OnWrite = bytes =>
            {
                if (bytes.Length == 18 && bytes[0] == 5 && bytes[1] == 0xFE)
                {
                    cancellation.Cancel();
                }
                return [];
            };
        }
        else
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkBootstrap.InitializeAsync(transport,
            MtkDaProtocol.Legacy, LegacyBootstrapOptions(dir), cancellation.Token));

        Assert.Equal(1, transport.DisposeCount);
        Assert.DoesNotContain(transport.Writes, bytes => bytes.Length == 12 && bytes[0] == 0x40);
    }

    [Fact]
    public async Task WriteLegacyStorageAsync_NonSeekableTruncatedSource_FaultsWithoutPadding()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue([0x5A, 0x69]);
        using var source = new NonSeekableStorageSource(new byte[768]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<EndOfStreamException>(() => session.WriteLegacyStorageAsync(8, 0, 1024, source,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Fact]
    public async Task EraseLegacyStorageAsync_RepeatedProgress_IsBounded()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A015A5A"));
        for (var index = 0; index < 4096; index++)
        {
            transport.Enqueue(Convert.FromHexString("5A5A0000000000"));
        }

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyStorageAsync(8, 0, 512,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownLegacyAsync_RejectedAcknowledgement_FaultsWithoutRetry(bool firstAccepted)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue(firstAccepted ? [0x5A, 0xA5] : [0xA5]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ShutdownLegacyAsync(MtkLegacyShutdownMode.Fastboot,
            TestContext.Current.CancellationToken));

        Assert.Equal(firstAccepted ? 2 : 1, transport.Writes.Count - before);
        Assert.False(session.IsConnected);
        Assert.Null(session.LegacyStorage);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }
}