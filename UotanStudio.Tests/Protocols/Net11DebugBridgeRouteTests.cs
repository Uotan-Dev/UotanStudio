using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Routing;
using Xunit;

namespace UotanStudio.Tests.Protocols;

[CollectionDefinition("Net11 debug bridge route", DisableParallelization = true)]
public sealed class Net11DebugBridgeRouteCollection;

/// <summary>
/// Exercises the .NET 11 debug bridge connect path against isolated loopback listeners.
/// <para>使用隔离的回环监听器验证 .NET 11 调试桥连接路径。</para>
/// </summary>
[Collection("Net11 debug bridge route")]
public sealed class Net11DebugBridgeRouteTests
{
    [Fact]
    public async Task IsListeningAsync_LiveLoopbackListener_ReturnsTrue()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryProbeAsync_LiveLoopbackListener_ReportsListening()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var outcome = await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken);

        Assert.Equal(DebugBridgeProbeOutcome.Listening, outcome);
    }

    [Fact]
    public async Task LoopbackProbes_ClosedPort_ReportRefused()
    {
        var port = ReserveClosedPort();

        Assert.False(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
        Assert.Equal(DebugBridgeProbeOutcome.Refused,
            await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsListeningAsync_StoppedListenerWithinPositiveTtl_UsesCachedResult()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
            listener.Stop();

            Assert.True(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TryProbeAsync_ListenerStartsAfterRefusal_ReportsListening()
    {
        var port = ReserveClosedPort();
        Assert.Equal(DebugBridgeProbeOutcome.Refused,
            await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken));

        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        Assert.Equal(DebugBridgeProbeOutcome.Listening,
            await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryProbeAsync_CallerCancelsPendingConnect_PropagatesCallerToken()
    {
        await using var saturated = await SaturatedLoopback.CreateAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = DebugBridgeRoute.TryProbeAsync("127.0.0.1", saturated.Port, caller.Token);
        Assert.False(probe.IsCompleted);

        caller.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            probe.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task IsListeningAsync_CallerCancelsPendingConnect_PropagatesCallerToken()
    {
        await using var saturated = await SaturatedLoopback.CreateAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = DebugBridgeRoute.IsListeningAsync("127.0.0.1", saturated.Port, caller.Token);
        Assert.False(probe.IsCompleted);

        caller.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            probe.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task ConnectAsync_CancelledDeadline_ReportsOperationAborted()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();

        var result = await DebugBridgeRoute.ConnectAsync("127.0.0.1", port, deadline.Token,
            TestContext.Current.CancellationToken);

        Assert.Equal(SocketError.OperationAborted, result.Error);
        Assert.True(result.LinkedCancellationRequested);
    }

    [Fact]
    public async Task ConnectAsync_CallerCancellationTakesPrecedenceOverDeadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        caller.Cancel();
        deadline.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DebugBridgeRoute.ConnectAsync("127.0.0.1", port, deadline.Token, caller.Token));

        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    private static int ReserveClosedPort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    private sealed class SaturatedLoopback : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly List<Socket> _sockets = [];
        private readonly List<Task> _connects = [];

        private SaturatedLoopback()
        {
        }

        public int Port { get; private set; }

        public static Task<SaturatedLoopback> CreateAsync()
        {
            var saturated = new SaturatedLoopback();
            saturated._listener.Start(1);
            saturated.Port = ((IPEndPoint)saturated._listener.LocalEndpoint).Port;
            var endpoint = new IPEndPoint(IPAddress.Loopback, saturated.Port);
            for (var index = 0; index < 64; index++)
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                saturated._sockets.Add(socket);
                saturated._connects.Add(socket.ConnectAsync(endpoint, saturated._cancellation.Token).AsTask());
            }

            return Task.FromResult(saturated);
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }

            try
            {
                await Task.WhenAll(_connects).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            _cancellation.Dispose();
        }
    }
}