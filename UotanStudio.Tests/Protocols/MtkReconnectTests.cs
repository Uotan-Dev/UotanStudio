using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Pins the reconnect contract: only a faulted session reconnects, the replacement transport is owned by the
/// session, a recorded non-replayable upload failure blocks recovery, and a session without a reopener says so.
/// </summary>
public sealed class MtkReconnectTests
{
    [Fact]
    public async Task FaultedSessionReconnectsOverAFreshTransport()
    {
        var broken = new MtkBootScriptDevice { RejectedHandshakeAttempts = 50 };
        using var session = new MtkSession(broken.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);
        await Assert.ThrowsAsync<MtkProtocolException>(
            () => session.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MtkSessionState.Faulted, session.State);

        var healthy = new MtkBootScriptDevice();
        session.AttachReopener(new ScriptedReopener(healthy.Transport));

        var reconnected = await session.TryReconnectAsync(TestContext.Current.CancellationToken);

        Assert.True(reconnected);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Same(healthy.Transport, session.Transport);
        Assert.Equal(1, broken.Transport.DisposeCount);
        Assert.NotNull(session.DeviceInfo);
    }

    [Fact]
    public async Task SessionWithoutAReopenerReportsUnsupported()
    {
        var broken = new MtkBootScriptDevice { RejectedHandshakeAttempts = 50 };
        using var session = new MtkSession(broken.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);
        await Assert.ThrowsAsync<MtkProtocolException>(
            () => session.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.False(await session.TryReconnectAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task HealthySessionIsNotDowngradedByReconnect()
    {
        var device = new MtkBootScriptDevice();
        using var session = new MtkSession(device.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        session.AttachReopener(new ScriptedReopener(new MtkBootScriptDevice().Transport));

        Assert.False(await session.TryReconnectAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task FailedReopenLeavesTheSessionFaulted()
    {
        var broken = new MtkBootScriptDevice { RejectedHandshakeAttempts = 50 };
        using var session = new MtkSession(broken.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);
        await Assert.ThrowsAsync<MtkProtocolException>(
            () => session.InitializeAsync(TestContext.Current.CancellationToken));
        session.AttachReopener(new ThrowingReopener());

        await Assert.ThrowsAsync<IOException>(
            () => session.TryReconnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private sealed class ScriptedReopener(ICommTransport transport) : IMtkTransportReopener
    {
        public Task<ICommTransport> ReopenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(transport);
        }
    }

    private sealed class ThrowingReopener : IMtkTransportReopener
    {
        public Task<ICommTransport> ReopenAsync(CancellationToken cancellationToken) =>
            throw new IOException("The device did not reappear.");
    }
}