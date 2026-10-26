using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Protocols.Apple;
using UotanStudio.Comm.Protocols.Apple.Usbmuxd;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Client behavior against an in-process fake usbmuxd daemon listening on a loopback TCP port.
/// <para>针对监听回环 TCP 端口的进程内 usbmuxd 假守护进程验证客户端行为。</para>
/// </summary>
public sealed class UsbmuxdClientTests
{
    [Fact(Timeout = 15000)]
    public async Task ListDevices_ParsesDeviceListFromPlistResponse()
    {
        await using var daemon = await FakeUsbmuxdDaemon.StartAsync(TestContext.Current.CancellationToken);
        using var client = new UsbmuxdClient(new UsbmuxdClientOptions(
            UsbmuxdEndpointKind.Tcp, "127.0.0.1", daemon.Port, null));

        var devices = client.ListDevices(TestContext.Current.CancellationToken);

        var device = Assert.Single(devices);
        Assert.Equal(7, device.DeviceId);
        Assert.Equal("00112233445566778899AABBCCDDEEFF00112233", device.SerialNumber);
        Assert.Equal("USB", device.ConnectionType);
        Assert.Equal((ushort)4776, device.ProductId);
        Assert.Equal(123, device.LocationId);
    }

    [Fact(Timeout = 15000)]
    public async Task Connect_RefusedPort_ThrowsWithResultCode()
    {
        await using var daemon = await FakeUsbmuxdDaemon.StartAsync(TestContext.Current.CancellationToken);
        using var client = new UsbmuxdClient(new UsbmuxdClientOptions(
            UsbmuxdEndpointKind.Tcp, "127.0.0.1", daemon.Port, null));

        var exception = Assert.Throws<UsbmuxdException>(() =>
            client.Connect("00112233445566778899AABBCCDDEEFF00112233", 12345,
                TestContext.Current.CancellationToken));

        Assert.Equal(AppleConstants.UsbmuxdResultConnectionRefused, exception.ResultCode);
    }

    [Fact(Timeout = 15000)]
    public async Task Connect_AcceptedPort_ReturnsRawStreamThatCarriesDeviceBytes()
    {
        await using var daemon = await FakeUsbmuxdDaemon.StartAsync(TestContext.Current.CancellationToken);
        using var client = new UsbmuxdClient(new UsbmuxdClientOptions(
            UsbmuxdEndpointKind.Tcp, "127.0.0.1", daemon.Port, null));

        using var stream = client.Connect("00112233445566778899AABBCCDDEEFF00112233", 62078,
            TestContext.Current.CancellationToken);
        var payload = Encoding.ASCII.GetBytes("ping");
        stream.Write(payload, 0, payload.Length);

        var received = await daemon.WaitForDeviceBytesAsync(4, TestContext.Current.CancellationToken);

        Assert.Equal("ping", Encoding.ASCII.GetString(received));
    }

    [Fact(Timeout = 15000)]
    public void Header_EncodesLengthVersionMessageAndTagLittleEndian()
    {
        _ = TestContext.Current.CancellationToken;
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 42);

        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(AppleConstants.UsbmuxdMessagePlist, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)));
    }

    /// <summary>
    /// Minimal usbmuxd daemon fake: answers plist protocol requests and can relay device bytes.
    /// <para>最小 usbmuxd 守护进程假件：应答 plist 协议请求并可中继设备字节。</para>
    /// </summary>
    private sealed class FakeUsbmuxdDaemon : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _acceptLoop;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<byte[]> _deviceBytes = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeUsbmuxdDaemon(TcpListener listener)
        {
            _listener = listener;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static async Task<FakeUsbmuxdDaemon> StartAsync(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            await Task.Yield();
            _ = cancellationToken;
            return new FakeUsbmuxdDaemon(listener);
        }

        public Task<byte[]> WaitForDeviceBytesAsync(int count, CancellationToken cancellationToken) =>
            _deviceBytes.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    _ = Task.Run(() => HandleAsync(client, _cancellation.Token));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using var buffered = client;
            var stream = buffered.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var header = new byte[16];
                if (!await FillAsync(stream, header, cancellationToken))
                {
                    return;
                }

                var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header));
                var message = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                if (length is < 16 or > (int)AppleConstants.UsbmuxdMaxMessageBytes)
                {
                    return;
                }

                var payload = new byte[length - 16];
                if (payload.Length > 0 && !await FillAsync(stream, payload, cancellationToken))
                {
                    return;
                }

                if (message != AppleConstants.UsbmuxdMessagePlist)
                {
                    continue;
                }

                var body = Assert.IsType<Dictionary<string, object>>(ApplePlist.Parse(payload));
                var type = Assert.IsType<string>(body["MessageType"]);
                switch (type)
                {
                    case "ListDevices":
                        Reply(stream, new Dictionary<string, object>
                        {
                            ["MessageType"] = "Result",
                            ["Number"] = 0L,
                            ["DeviceList"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["DeviceID"] = 7L,
                                    ["Properties"] = new Dictionary<string, object>
                                    {
                                        ["SerialNumber"] = "00112233445566778899AABBCCDDEEFF00112233",
                                        ["ProductID"] = 4776L,
                                        ["ConnectionType"] = "USB",
                                        ["LocationID"] = 123L,
                                    },
                                },
                            },
                        });
                        break;
                    case "ReadBuid":
                        Reply(stream, new Dictionary<string, object>
                        {
                            ["MessageType"] = "Result",
                            ["Number"] = 0L,
                            ["BUID"] = "BUID1234",
                        });
                        break;
                    case "Connect":
                        {
                            var port = Assert.IsType<long>(body["PortNumber"]);
                            var refused = port == Swap(12345);
                            Reply(stream, new Dictionary<string, object>
                            {
                                ["MessageType"] = "Result",
                                ["Number"] = refused ? 3L : 0L,
                            });
                            if (!refused)
                            {
                                await RelayAsync(stream, cancellationToken);
                                return;
                            }

                            break;
                        }
                }
            }
        }

        private async Task RelayAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[256];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    return;
                }

                _deviceBytes.TrySetResult(buffer[..read]);
            }
        }

        private static void Reply(NetworkStream stream, Dictionary<string, object> body)
        {
            var payload = ApplePlist.ToXml(body);
            var header = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(16 + payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), AppleConstants.UsbmuxdMessagePlist);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
            stream.Write(header);
            stream.Write(payload);
            stream.Flush();
        }

        private static ushort Swap(ushort value) => (ushort)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));

        private static async Task<bool> FillAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
                if (count <= 0)
                {
                    return false;
                }

                read += count;
            }

            return true;
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
            catch (Exception)
            {
            }

            _cancellation.Dispose();
        }
    }
}