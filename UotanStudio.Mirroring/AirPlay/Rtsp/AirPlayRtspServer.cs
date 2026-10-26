using System.Net;
using System.Net.Sockets;

namespace UotanStudio.Mirroring.AirPlay.Rtsp;

/// <summary>
/// Minimal RTSP listener for the AirPlay receiver: one bounded request/response loop per connection plus the
/// <c>$</c>-prefixed interleaved media framing a client uses when it does not stream over UDP.
/// <para>AirPlay 接收端的最小 RTSP 监听器：每条连接一个带界的请求/应答循环，以及客户端不使用 UDP 时采用的
/// <c>$</c> 前缀交织媒体分帧。</para>
/// </summary>
/// <remarks>
/// <para>
/// The server never initiates a connection and holds no device identity: it binds the configured port, hands
/// each parsed request to <see cref="Handler"/> and forwards interleaved payloads to <see cref="MediaPacket"/>.
/// The receiver is therefore the passive side, which is why this listener lives in the mirroring layer instead
/// of the device I/O layer.
/// </para>
/// <para>
/// 服务器从不主动连接、也不持有任何设备标识：它绑定配置的端口，把解析后的请求交给 <see cref="Handler"/>，
/// 并把交织载荷转给 <see cref="MediaPacket"/>。接收端因此是被动方，这也是该监听器位于投屏层而不是设备 I/O 层
/// 的原因。
/// </para>
/// </remarks>
internal sealed class AirPlayRtspServer : IDisposable
{
    private const int MaximumConnections = 4;
    private readonly int _requestedPort;
    private readonly Action<string>? _logSink;
    private readonly List<TcpClient> _connections = [];
    private readonly Lock _connectionLock = new();
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private int _disposed;

    /// <summary>Creates a listener that has not bound its port yet. <para>创建尚未绑定端口的监听器。</para></summary>
    /// <param name="port">TCP port to bind. <para>要绑定的 TCP 端口。</para></param>
    /// <param name="logSink">Optional diagnostic sink; may be called from connection threads.
    /// <para>可选的诊断接收端；可能由连接线程调用。</para></param>
    public AirPlayRtspServer(int port, Action<string>? logSink)
    {
        _requestedPort = port;
        _logSink = logSink;
    }

    /// <summary>Port the listener is bound to after <see cref="Start"/>. <para><see cref="Start"/> 之后监听器绑定的端口。</para></summary>
    public int Port { get; private set; }

    /// <summary>Handles one parsed request and produces the response to write.
    /// <para>处理一条解析后的请求并产生要写入的应答。</para>
    /// </summary>
    public Func<AirPlayRtspRequest, AirPlayRtspResponse>? Handler { get; set; }

    /// <summary>Receives one interleaved media payload and the channel it arrived on.
    /// <para>接收一个交织媒体载荷及其到达的通道号。</para>
    /// </summary>
    public Action<int, byte[]>? MediaPacket { get; set; }

    /// <summary>Binds the port and starts accepting connections. <para>绑定端口并开始接受连接。</para></summary>
    /// <exception cref="SocketException">The port cannot be bound. <para>端口无法绑定。</para></exception>
    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Any, _requestedPort);
        listener.Start(MaximumConnections);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "airplay-rtsp" };
        _acceptThread.Start();
    }

    private void AcceptLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException
                or InvalidOperationException)
            {
                return;
            }

            lock (_connectionLock)
            {
                if (_disposed != 0)
                {
                    client.Dispose();
                    return;
                }

                _connections.Add(client);
            }

            var thread = new Thread(() => Serve(client)) { IsBackground = true, Name = "airplay-rtsp-client" };
            thread.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            while (Volatile.Read(ref _disposed) == 0)
            {
                var first = stream.ReadByte();
                if (first < 0)
                {
                    return;
                }

                if (first == '$')
                {
                    if (!ReadInterleaved(stream))
                    {
                        return;
                    }

                    continue;
                }

                if (!ServeRequest(stream, (byte)first))
                {
                    return;
                }
            }
        }
        catch (Exception error) when (error is IOException or SocketException or InvalidDataException
            or ObjectDisposedException or ArgumentException)
        {
            _logSink?.Invoke("AirPlay RTSP connection closed: " + error.Message);
        }
        finally
        {
            lock (_connectionLock)
            {
                _connections.Remove(client);
            }

            client.Dispose();
        }
    }

    private bool ReadInterleaved(NetworkStream stream)
    {
        var head = new byte[3];
        if (!ReadExactly(stream, head))
        {
            return false;
        }

        int channel = head[0];
        var length = (head[1] << 8) | head[2];
        if (length > AirPlayConstants.MaximumRtpPacketBytes)
        {
            throw new InvalidDataException("An interleaved media frame exceeds the accepted size.");
        }

        if (length == 0)
        {
            return true;
        }

        var payload = new byte[length];
        if (!ReadExactly(stream, payload))
        {
            return false;
        }

        MediaPacket?.Invoke(channel, payload);
        return true;
    }

    private bool ServeRequest(NetworkStream stream, byte first)
    {
        var header = new List<byte>(256) { first };
        var complete = false;
        while (header.Count < AirPlayConstants.MaximumHeaderBytes)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                return false;
            }

            header.Add((byte)value);
            var count = header.Count;
            if (count >= 4 && header[count - 4] == (byte)'\r' && header[count - 3] == (byte)'\n' &&
                header[count - 2] == (byte)'\r' && header[count - 1] == (byte)'\n')
            {
                complete = true;
                break;
            }
        }

        if (!complete)
        {
            throw new InvalidDataException("The RTSP header block exceeds the accepted size.");
        }

        var headerBytes = header.ToArray().AsSpan(0, header.Count - 4).ToArray();
        var contentLength = AirPlayRtspParser.ReadContentLength(headerBytes);
        var body = new byte[contentLength];
        if (contentLength > 0 && !ReadExactly(stream, body))
        {
            return false;
        }

        var request = AirPlayRtspParser.Parse(headerBytes, body);
        var handler = Handler ?? throw new InvalidOperationException("No RTSP handler is attached.");
        var response = handler(request);
        stream.Write(AirPlayRtspParser.Serialize(response, request.CSeq));
        stream.Flush();
        return request.Method != "TEARDOWN";
    }

    private static bool ReadExactly(NetworkStream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = stream.Read(buffer[offset..]);
            if (count == 0)
            {
                return false;
            }

            offset += count;
        }

        return true;
    }

    /// <summary>Stops accepting connections and closes every open one. <para>停止接受连接并关闭所有已打开的连接。</para></summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _listener?.Stop();
        }
        finally
        {
            _listener = null;
            _acceptThread?.Join(TimeSpan.FromSeconds(2));
            _acceptThread = null;
            lock (_connectionLock)
            {
                foreach (var connection in _connections)
                {
                    connection.Dispose();
                }

                _connections.Clear();
            }
        }
    }
}