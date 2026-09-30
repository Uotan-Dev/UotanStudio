using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Routing;
using Xunit;

namespace UotanStudio.Tests.Comm.Routing;

public sealed class DebugBridgeRouteProbeTests
{
    [Fact]
    public async Task TryProbeAsync_LiveListener_ReturnsListening()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var outcome = await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken);
            Assert.Equal(DebugBridgeProbeOutcome.Listening, outcome);
        }
        finally
        {
            DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        }
    }

    [Fact]
    public async Task TryProbeAsync_RefusedPort_ReturnsRefused()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        try
        {
            var outcome = await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken);
            Assert.Equal(DebugBridgeProbeOutcome.Refused, outcome);
        }
        finally
        {
            DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        }
    }

    [Fact]
    public async Task TryProbeAsync_UnresolvableHost_ReturnsUnknownWithoutReportingFlip()
    {
        const string host = "uotan-probe-missing.invalid";
        const int port = 50391;
        var reports = new ConcurrentQueue<bool>();
        void OnState(string reportedHost, int reportedPort, bool listening)
        {
            if (reportedHost == host)
            {
                reports.Enqueue(listening);
            }
        }
        DebugBridgeRoute.ServerAvailabilityChanged += OnState;
        try
        {
            var first = await DebugBridgeRoute.TryProbeAsync(host, port, TestContext.Current.CancellationToken);
            var second = await DebugBridgeRoute.TryProbeAsync(host, port, TestContext.Current.CancellationToken);
            Assert.Equal(DebugBridgeProbeOutcome.Unknown, first);
            Assert.Equal(DebugBridgeProbeOutcome.Unknown, second);
            Assert.Empty(reports);
        }
        finally
        {
            DebugBridgeRoute.ServerAvailabilityChanged -= OnState;
            DebugBridgeRoute.InvalidateProbe(host, port);
        }
    }

    [Fact]
    public async Task TryProbeAsync_NonRefusalConnectFault_ReturnsUnknown()
    {
        const string host = "192.0.2.1";
        const int port = 50390;
        DebugBridgeRoute.InvalidateProbe(host, port);
        try
        {
            var outcome = await DebugBridgeRoute.TryProbeAsync(host, port, TestContext.Current.CancellationToken);
            Assert.Equal(DebugBridgeProbeOutcome.Unknown, outcome);
        }
        finally
        {
            DebugBridgeRoute.InvalidateProbe(host, port);
        }
    }

    [Fact]
    public async Task TryProbeAsync_CallerCanceled_ThrowsInsteadOfReturningUnknown()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DebugBridgeRoute.TryProbeAsync("127.0.0.1", 5037, cancellation.Token));
    }

    [Fact]
    public async Task TryProbeAsync_ListeningResult_IsCachedPositiveUntilInvalidated()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.Equal(DebugBridgeProbeOutcome.Listening,
                await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
            listener.Stop();
            Assert.Equal(DebugBridgeProbeOutcome.Listening,
                await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
            DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
            Assert.Equal(DebugBridgeProbeOutcome.Refused,
                await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
        }
        finally
        {
            DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        }
    }

    [Fact]
    public async Task IsListeningAsync_UnresolvableHost_StillThrowsSocketException()
    {
        await Assert.ThrowsAnyAsync<SocketException>(() =>
            DebugBridgeRoute.IsListeningAsync("uotan-probe-missing.invalid", 50392, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsListeningAsync_OutOfRangePort_StillThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            DebugBridgeRoute.IsListeningAsync("127.0.0.1", -1, TestContext.Current.CancellationToken));
    }
}