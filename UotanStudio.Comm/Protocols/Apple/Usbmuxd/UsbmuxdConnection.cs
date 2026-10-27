using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace UotanStudio.Comm.Protocols.Apple.Usbmuxd;

/// <summary>
/// One usbmuxd message exchanged over a daemon connection; the payload is either a serialized plist
/// or a binary struct depending on <see cref="MessageType"/>.
/// <para>经由守护进程连接交换的单条 usbmuxd 消息；依据 <see cref="MessageType"/>，负载为序列化的
/// 属性列表或二进制结构。</para>
/// </summary>
/// <param name="Version">The protocol version carried in the header. <para>头部携带的协议版本。</para></param>
/// <param name="MessageType">The message type code. <para>消息类型码。</para></param>
/// <param name="Tag">The request tag echoed by results. <para>结果回显的请求标签。</para></param>
/// <param name="Payload">The raw payload bytes. <para>原始负载字节。</para></param>
internal readonly record struct UsbmuxdMessage(uint Version, uint MessageType, uint Tag, byte[] Payload)
{
    /// <summary>Gets the fixed header size in bytes. <para>获取头部固定字节数。</para></summary>
    public const int HeaderSize = 16;
}

/// <summary>
/// A single connection to the usbmuxd daemon: frames messages, resolves the endpoint and owns the
/// underlying socket. After a successful CONNECT handshake the connection degenerates into a raw
/// byte stream (<see cref="DetachStream"/>) carrying one device TCP session.
/// <para>到 usbmuxd 守护进程的单条连接：组帧、解析端点并持有底层套接字。CONNECT 握手成功后，
/// 连接退化为承载一路设备 TCP 会话的原始字节流（<see cref="DetachStream"/>）。</para>
/// </summary>
internal sealed class UsbmuxdConnection : IDisposable
{
    private const int ConnectTimeoutMs = 5_000;
    private const int HandshakeTimeoutMs = 5_000;

    private readonly Stream _stream;
    private bool _detached;

    private UsbmuxdConnection(Stream stream) => _stream = stream;

    /// <summary>
    /// Opens a new daemon connection using the supplied endpoint resolution.
    /// <para>按给定端点解析方式打开新的守护进程连接。</para>
    /// </summary>
    /// <param name="options">The endpoint options. <para>端点配置。</para></param>
    /// <param name="cancellationToken">Cancels the connect. <para>取消连接。</para></param>
    /// <returns>The opened connection. <para>已打开的连接。</para></returns>
    /// <exception cref="UsbmuxdException">The endpoint cannot be resolved or connected. <para>端点无法解析或无法连接时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public static UsbmuxdConnection Open(UsbmuxdClientOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeoutMs);
            return new UsbmuxdConnection(ConnectEndpoint(options, timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsbmuxdException("Connecting to the usbmuxd endpoint timed out.");
        }
        catch (SocketException exception)
        {
            throw new UsbmuxdException($"Connecting to the usbmuxd endpoint failed: {exception.Message}", exception);
        }
    }

    private static Stream ConnectEndpoint(UsbmuxdClientOptions options, CancellationToken cancellationToken)
    {
        var (kind, host, port, socketPath) = ResolveEndpoint(options);
        var socket = new Socket(kind == UsbmuxdEndpointKind.UnixSocket
            ? AddressFamily.Unix
            : AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            var endpoint = kind == UsbmuxdEndpointKind.UnixSocket
                ? (EndPoint)new UnixDomainSocketEndPoint(socketPath!)
                : new IPEndPoint(IPAddress.Parse(host!), port!.Value);
            socket.ConnectAsync(endpoint, cancellationToken).AsTask().GetAwaiter().GetResult();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static (UsbmuxdEndpointKind Kind, string? Host, int? Port, string? SocketPath) ResolveEndpoint(
        UsbmuxdClientOptions options)
    {
        var overrideValue = Environment.GetEnvironmentVariable(AppleConstants.UsbmuxdSocketAddressEnvironmentVariable);
        if (options.Kind == UsbmuxdEndpointKind.Auto && !string.IsNullOrWhiteSpace(overrideValue))
        {
            if (overrideValue.Contains(':') && overrideValue.LastIndexOf(':') is { } separator &&
                int.TryParse(overrideValue[(separator + 1)..], out var port))
            {
                return (UsbmuxdEndpointKind.Tcp, overrideValue[..separator], port, null);
            }

            return (UsbmuxdEndpointKind.UnixSocket, null, null, overrideValue);
        }

        switch (options.Kind)
        {
            case UsbmuxdEndpointKind.Tcp:
                return (UsbmuxdEndpointKind.Tcp,
                    string.IsNullOrWhiteSpace(options.TcpHost) ? "127.0.0.1" : options.TcpHost,
                    options.TcpPort ?? AppleConstants.UsbmuxdWindowsPort,
                    null);
            case UsbmuxdEndpointKind.UnixSocket:
                return (UsbmuxdEndpointKind.UnixSocket, null, null,
                    string.IsNullOrWhiteSpace(options.UnixSocketPath)
                        ? AppleConstants.UsbmuxdUnixSocketPath
                        : options.UnixSocketPath);
            default:
                return OperatingSystem.IsWindows()
                    ? (UsbmuxdEndpointKind.Tcp, "127.0.0.1", AppleConstants.UsbmuxdWindowsPort, null)
                    : (UsbmuxdEndpointKind.UnixSocket, null, null, AppleConstants.UsbmuxdUnixSocketPath);
        }
    }

    /// <summary>
    /// Writes one framed message with the plist payload.
    /// <para>写入一条以属性列表为负载的组帧消息。</para>
    /// </summary>
    /// <param name="tag">The request tag. <para>请求标签。</para></param>
    /// <param name="body">The plist dictionary to send. <para>待发送的属性列表字典。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消写入。</para></param>
    public void WritePlistMessage(uint tag, Dictionary<string, object> body, CancellationToken cancellationToken)
    {
        var payload = ApplePlist.ToXml(body);
        WriteMessage(AppleConstants.UsbmuxdProtocolVersionPlist, AppleConstants.UsbmuxdMessagePlist, tag,
            payload, cancellationToken);
    }

    /// <summary>
    /// Writes one framed binary-protocol message.
    /// <para>写入一条二进制协议的组帧消息。</para>
    /// </summary>
    /// <param name="tag">The request tag. <para>请求标签。</para></param>
    /// <param name="messageType">The message type code. <para>消息类型码。</para></param>
    /// <param name="payload">The raw payload, or <see langword="null"/> for none. <para>原始负载；无负载时为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消写入。</para></param>
    public void WriteLegacyMessage(uint tag, uint messageType, byte[]? payload, CancellationToken cancellationToken) =>
        WriteMessage(AppleConstants.UsbmuxdProtocolVersionBinary, messageType, tag, payload, cancellationToken);

    private void WriteMessage(uint version, uint messageType, uint tag, byte[]? payload,
        CancellationToken cancellationToken)
    {
        var length = UsbmuxdMessage.HeaderSize + (payload?.Length ?? 0);
        if (length > AppleConstants.UsbmuxdMaxMessageBytes)
        {
            throw new UsbmuxdException("The usbmuxd message exceeds the protocol size limit.");
        }

        var header = new byte[UsbmuxdMessage.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), messageType);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), tag);
        WriteAll(header, cancellationToken);
        if (payload is { Length: > 0 })
        {
            WriteAll(payload, cancellationToken);
        }
    }

    /// <summary>
    /// Reads one framed message with bounded length.
    /// <para>读取一条长度有界的组帧消息。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>The received message. <para>收到的消息。</para></returns>
    /// <exception cref="UsbmuxdException">The header, length or stream is invalid. <para>头部、长度或流非法时抛出。</para></exception>
    public UsbmuxdMessage ReadMessage(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HandshakeTimeoutMs);
            var header = new byte[UsbmuxdMessage.HeaderSize];
            ReadAll(header, timeout.Token);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var messageType = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var tag = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            if (length < UsbmuxdMessage.HeaderSize || length > AppleConstants.UsbmuxdMaxMessageBytes)
            {
                throw new UsbmuxdException($"The usbmuxd message length {length} is out of range.");
            }

            var payloadSize = (int)(length - UsbmuxdMessage.HeaderSize);
            var payload = new byte[payloadSize];
            if (payloadSize > 0)
            {
                ReadAll(payload, timeout.Token);
            }

            return new UsbmuxdMessage(version, messageType, tag, payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsbmuxdException("Reading a usbmuxd message timed out.");
        }
        catch (SocketException exception)
        {
            throw new UsbmuxdException($"Reading a usbmuxd message failed: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Parses a plist payload of a MESSAGE_PLIST message into the object graph.
    /// <para>将 MESSAGE_PLIST 消息的负载解析为对象图。</para>
    /// </summary>
    /// <param name="message">The received message. <para>收到的消息。</para></param>
    /// <returns>The decoded plist dictionary. <para>解码后的属性列表字典。</para></returns>
    /// <exception cref="UsbmuxdException">The message is not a plist message or the payload is malformed.
    /// <para>消息不是属性列表消息或负载畸形时抛出。</para></exception>
    public static Dictionary<string, object> ParsePlistPayload(UsbmuxdMessage message)
    {
        if (message.MessageType != AppleConstants.UsbmuxdMessagePlist)
        {
            throw new UsbmuxdException($"Expected a plist message but received type {message.MessageType}.");
        }

        if (ApplePlist.Parse(message.Payload) is not Dictionary<string, object> body)
        {
            throw new UsbmuxdException("The usbmuxd plist message does not carry a dictionary.");
        }

        return body;
    }

    /// <summary>
    /// Returns the raw stream after a successful CONNECT handshake, transferring ownership.
    /// <para>在 CONNECT 握手成功后返回原始流并转移所有权。</para>
    /// </summary>
    /// <returns>The device byte stream. <para>设备字节流。</para></returns>
    public Stream DetachStream()
    {
        _detached = true;
        return _stream;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_detached)
        {
            _stream.Dispose();
        }
    }

    private void WriteAll(byte[] bytes, CancellationToken cancellationToken)
    {
        _stream.WriteAsync(bytes, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    private void ReadAll(byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = _stream.ReadAsync(buffer.AsMemory(read), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            if (count <= 0)
            {
                throw new UsbmuxdException("The usbmuxd endpoint closed the connection during a read.");
            }

            read += count;
        }
    }
}