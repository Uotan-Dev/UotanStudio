using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using UotanStudio.Comm.Routing;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public class DebugBridgeProbeLoggingTests
{
    [Fact]
    public async Task IsListeningAsync_Refused_ReportsStateWithoutThrowingSocketException()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var active = new AsyncLocal<bool> { Value = true };
        var exceptions = new ConcurrentQueue<Exception>();
        var states = new ConcurrentQueue<bool>();
        void OnException(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (active.Value && args.Exception is SocketException)
            {
                exceptions.Enqueue(args.Exception);
            }
        }
        void OnState(string host, int observedPort, bool listening)
        {
            if (observedPort == port)
            {
                states.Enqueue(listening);
            }
        }
        AppDomain.CurrentDomain.FirstChanceException += OnException;
        DebugBridgeRoute.ServerAvailabilityChanged += OnState;
        try
        {
            Assert.False(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
            Assert.False(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
            Assert.Empty(exceptions);
            Assert.Equal([false], states);
            using var restarted = new TcpListener(IPAddress.Loopback, port);
            restarted.Start();
            Assert.True(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
            restarted.Stop();
            DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
            Assert.False(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
            Assert.Equal([false, true, false], states);
        }
        finally
        {
            active.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnException;
            DebugBridgeRoute.ServerAvailabilityChanged -= OnState;
        }
    }

    [Fact]
    public void LogServerAvailability_Refused_WritesGlobalInfoLog()
    {
        var entries = new ConcurrentQueue<LogEntry>();
        var unsubscribe = LogService.Instance.Subscribe((_, entry) => entries.Enqueue(entry));
        try
        {
            UsbLogBridge.LogServerAvailability("probe-test-host", 5037, false);
            var entry = Assert.Single(entries, entry => entry.Source == "DeviceCommunication" &&
                entry.Message.Contains("probe-test-host:5037", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Info, entry.Level);
            Assert.Contains("connection refused", entry.Message, StringComparison.Ordinal);
            Assert.Contains("direct USB", entry.Message, StringComparison.Ordinal);
        }
        finally
        {
            unsubscribe();
        }
    }
}