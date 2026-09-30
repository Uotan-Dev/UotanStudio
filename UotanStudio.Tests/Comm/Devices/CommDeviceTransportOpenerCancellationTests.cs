using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class CommDeviceTransportOpenerCancellationTests
{
    [Fact]
    public async Task OpenAsync_AdbValidationStalls_CancellationEndsTheOpenPromptly()
    {
        using var server = new FakeAdbServer(FakeAdbServer.ReplyMode.Stall);
        var opener = new CommDeviceTransportOpener(null!, "127.0.0.1", server.Port);
        var endpoint = AdbEndpoint(server.Port, "serial-stall");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var openTask = opener.OpenAsync(endpoint, cancellation.Token);
        await server.TransportRequestSeen.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => openTask);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OpenAsync_AdbTargetValidatedByServer_DeliversOwnedTransportForSameSerial()
    {
        using var server = new FakeAdbServer(FakeAdbServer.ReplyMode.Okay);
        var opener = new CommDeviceTransportOpener(null!, "127.0.0.1", server.Port);
        var endpoint = AdbEndpoint(server.Port, "serial-a");
        await using var transport = await opener.OpenAsync(endpoint, TestContext.Current.CancellationToken);
        Assert.NotNull(transport);
        Assert.Contains("host:transport:serial-a", server.TransportRequests.ToArray());
    }

    [Fact]
    public async Task OpenAsync_AdbTargetRejectedByServer_SurfacesServerMessageWithoutHandingOutTransport()
    {
        using var server = new FakeAdbServer(FakeAdbServer.ReplyMode.Fail);
        var opener = new CommDeviceTransportOpener(null!, "127.0.0.1", server.Port);
        var endpoint = AdbEndpoint(server.Port, "serial-fail");
        var exception = await Assert.ThrowsAsync<IOException>(() =>
            opener.OpenAsync(endpoint, TestContext.Current.CancellationToken));
        Assert.Contains("device not found", exception.Message);
        Assert.Contains("host:transport:serial-fail", server.TransportRequests.ToArray());
    }

    [Fact]
    public async Task OpenAsync_AdbServerEndpointRefused_FailsWithoutAttemptingTargetValidation()
    {
        var port = ReserveUnusedLoopbackPort();
        var opener = new CommDeviceTransportOpener(null!, "127.0.0.1", port);
        var endpoint = AdbEndpoint(port, "serial-refused");
        var exception = await Assert.ThrowsAsync<IOException>(() =>
            opener.OpenAsync(endpoint, TestContext.Current.CancellationToken));
        Assert.Equal("ADB server is unavailable.", exception.Message);
    }

    private static DeviceEndpoint AdbEndpoint(int port, string serial) => new()
    {
        Source = $"adb-server@127.0.0.1:{port}",
        Transport = "adb-server",
        Route = "server",
        Address = serial,
        Serial = serial,
    };

    private static int ReserveUnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeAdbServer : IDisposable
    {
        public enum ReplyMode
        {
            Okay,
            Fail,
            Stall,
        }

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ReplyMode _mode;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly ConcurrentQueue<string> _transportRequests = [];

        public FakeAdbServer(ReplyMode mode)
        {
            _mode = mode;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; private set; }

        public ConcurrentQueue<string> TransportRequests => _transportRequests;

        public TaskCompletionSource TransportRequestSeen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task AcceptLoopAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                try
                {
                    var header = new byte[4];
                    await stream.ReadExactlyAsync(header, _lifetime.Token).ConfigureAwait(false);
                    var length = int.Parse(Encoding.ASCII.GetString(header),
                        NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (length is <= 0 or > 65535)
                    {
                        return;
                    }
                    var payload = new byte[length];
                    await stream.ReadExactlyAsync(payload, _lifetime.Token).ConfigureAwait(false);
                    var request = Encoding.UTF8.GetString(payload);
                    if (!request.StartsWith("host:transport:", StringComparison.Ordinal))
                    {
                        return;
                    }
                    _transportRequests.Enqueue(request);
                    TransportRequestSeen.TrySetResult();
                    switch (_mode)
                    {
                        case ReplyMode.Okay:
                            await stream.WriteAsync("OKAY"u8.ToArray(), _lifetime.Token).ConfigureAwait(false);
                            break;
                        case ReplyMode.Fail:
                            var message = Encoding.UTF8.GetBytes("device not found");
                            await stream.WriteAsync("FAIL"u8.ToArray(), _lifetime.Token).ConfigureAwait(false);
                            await stream.WriteAsync(
                                Encoding.ASCII.GetBytes(message.Length.ToString("X4", CultureInfo.InvariantCulture)),
                                _lifetime.Token).ConfigureAwait(false);
                            await stream.WriteAsync(message, _lifetime.Token).ConfigureAwait(false);
                            break;
                        default:
                            await Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token).ConfigureAwait(false);
                            break;
                    }
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _listener.Stop();
            _lifetime.Dispose();
        }
    }
}