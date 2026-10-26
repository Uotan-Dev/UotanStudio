using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Protocols.Apple.Usbmuxd;

/// <summary>
/// The iproxy equivalent: listens on a local TCP port and relays every accepted connection to a
/// device port over the multiplexer (for example an SSH daemon on a jailbroken device). The forward
/// is paired-free and keeps running until disposed; each client gets its own multiplexer stream.
/// <para>iproxy 等价物：监听本地 TCP 端口，把每个接受的连接经复用器中继到设备端口（例如越狱设备上的
/// SSH 守护进程）。转发无需配对，持续运行直到释放；每个客户端各占一路复用流。</para>
/// </summary>
public sealed class UsbmuxdPortForward : IDisposable
{
    /// <summary>Gets the loopback address the listener binds to. <para>获取监听绑定的回环地址。</para></summary>
    public const string BindAddress = "127.0.0.1";

    private readonly TcpListener _listener;
    private readonly Func<CancellationToken, Stream> _openDevicePort;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _clientsLock = new();
    private readonly List<Stream> _clients = [];
    private readonly Thread _acceptThread;
    private bool _disposed;

    private UsbmuxdPortForward(TcpListener listener, Func<CancellationToken, Stream> openDevicePort)
    {
        _listener = listener;
        _openDevicePort = openDevicePort;
        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "apple-port-forward",
        };
    }

    /// <summary>
    /// Starts a forward from <c>127.0.0.1:<paramref name="localPort"/></c> to the device port.
    /// <para>启动从 <c>127.0.0.1:<paramref name="localPort"/></c> 到设备端口的转发。</para>
    /// </summary>
    /// <param name="session">The Apple device session used to open device ports. <para>用于打开设备端口的 Apple 会话。</para></param>
    /// <param name="localPort">The local TCP port; 0 picks a free port. <para>本地 TCP 端口；0 表示自动选择空闲端口。</para></param>
    /// <param name="devicePort">The device TCP port. <para>设备 TCP 端口。</para></param>
    /// <param name="cancellationToken">Cancels the start. <para>取消启动。</para></param>
    /// <returns>The running forward. <para>运行中的转发。</para></returns>
    /// <exception cref="IOException">The local port cannot be bound. <para>本地端口无法绑定时抛出。</para></exception>
    public static UsbmuxdPortForward Start(IAppleDeviceSession session, int localPort, int devicePort,
        CancellationToken cancellationToken = default)
    {
        if (session is not AppleSession appleSession)
        {
            throw new ArgumentException("The session must be an Apple session.", nameof(session));
        }

        var listener = new TcpListener(IPAddress.Parse(BindAddress), localPort);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            throw new IOException($"Binding the local port {localPort} failed.", exception);
        }

        var boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var forward = new UsbmuxdPortForward(listener, ct =>
        {
            _ = devicePort;
            return appleSession.OpenDevicePort(devicePort, ct);
        });
        forward._acceptThread.Start();
        _ = cancellationToken;
        return forward;
    }

    /// <summary>Gets the actually bound local port. <para>获取实际绑定的本地端口。</para></summary>
    public int LocalPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private void AcceptLoop()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                var acceptTask = _listener.AcceptTcpClientAsync(_cancellation.Token).AsTask();
                acceptTask.Wait();
                client = acceptTask.Result;
            }
            catch (Exception exception) when (exception is OperationCanceledException ||
                exception is SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => RelayAsync(client, _cancellation.Token));
        }
    }

    private async Task RelayAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var ownedClient = client;
        Stream? device = null;
        try
        {
            device = _openDevicePort(cancellationToken);
            lock (_clientsLock)
            {
                if (_disposed)
                {
                    return;
                }

                _clients.Add(device);
            }

            var localStream = client.GetStream();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var copyToDevice = CopyAsync(localStream, device, linked.Token);
            var copyToLocal = CopyAsync(device, localStream, linked.Token);
            await Task.WhenAny(copyToDevice, copyToLocal).ConfigureAwait(false);
            linked.Cancel();
        }
        catch (Exception)
        {
        }
        finally
        {
            if (device is not null)
            {
                lock (_clientsLock)
                {
                    _clients.Remove(device);
                }

                device.Dispose();
            }
        }
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_clientsLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cancellation.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        lock (_clientsLock)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            _clients.Clear();
        }

        _cancellation.Dispose();
    }
}