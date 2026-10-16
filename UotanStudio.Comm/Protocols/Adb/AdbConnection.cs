using System.Buffers;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Represents an ADB connection over a device transport: CNXN handshake, authentication,
/// message dispatch loop, and stream multiplexing.
/// <para>基于设备传输的 ADB 连接：CNXN 握手、认证、消息分发循环、流复用。</para>
/// </summary>
internal sealed class AdbConnection : IDisposable
{
    /// <summary>Deadline applied to one transport message, both when queueing for the send gate and when
    /// writing. <para>单条传输消息的期限，涵盖排队等待发送闸门与实际写入。</para></summary>
    private const int TransportDeadlineMs = 5000;

    /// <summary>How long <see cref="Dispose"/> waits for the dispatch loop to observe the shutdown.
    /// <para><see cref="Dispose"/> 等待分发循环观察到关停信号的时长。</para></summary>
    private static readonly TimeSpan MessageLoopJoinBudget = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<AdbAuthentication> _authentications;
    private readonly AdbServerServices? _server;
    private readonly ICommTransport? _transport;
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, AdbStream> _streams = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _peerReady = new(0, 1);
    private readonly CancellationTokenSource _loopCancellation = new();
    private uint _nextLocalId = 1;
    private bool _connected;
    private bool _disposed;
    private Exception? _loopFault;
    private Task? _messageLoop;

    internal AdbConnection(AdbServerServices server)
    {
        _server = server;
        _authentications = [];
    }

    /// <summary>Whether services are owned by an external ADB server. <para>服务是否由外部 ADB server 持有。</para></summary>
    /// <value>True when an external ADB server owns the services. <para>外部 ADB server 持有服务时为真。</para></value>
    public bool UsesServer => _server is not null;

    private int _authKeyIndex;

    /// <summary>
    /// Gets why the message loop stopped, or <c>null</c> while the loop is running.
    /// <para>获取消息循环停止的原因；循环运行时为 <c>null</c>。</para>
    /// </summary>
    /// <value>The captured fault, or <c>null</c> before the loop stops. <para>捕获的故障；循环停止前为 <c>null</c>。</para></value>
    internal Exception? LoopFault => Volatile.Read(ref _loopFault);

    /// <summary>
    /// Gets whether the connection is established.
    /// <para>获取连接是否已建立。</para>
    /// </summary>
    /// <value>True when the connection is established. <para>连接已建立时为真。</para></value>
    public bool IsConnected { get { lock (_lock) { return _connected; } } }

    /// <summary>
    /// Gets the peer protocol version.
    /// <para>获取对端协议版本。</para>
    /// </summary>
    /// <value>The version reported by the peer. <para>对端报告的版本。</para></value>
    public uint PeerVersion { get; private set; }

    /// <summary>
    /// Gets the peer max payload size.
    /// <para>获取对端最大负载大小。</para>
    /// </summary>
    /// <value>The max payload size reported by the peer. <para>对端报告的最大负载大小。</para></value>
    public uint PeerMaxPayload { get; private set; }

    /// <summary>
    /// Gets the peer banner / system info.
    /// <para>获取对端横幅 / 系统信息。</para>
    /// </summary>
    /// <value>The banner string, or <c>null</c> before CNXN arrives. <para>横幅字符串；CNXN 到达前为 <c>null</c>。</para></value>
    public string? PeerBanner { get; private set; }

    /// <summary>
    /// Gets the features advertised by the peer.
    /// <para>获取对端宣告的特性。</para>
    /// </summary>
    /// <value>The feature names parsed from the peer banner. <para>从对端横幅解析出的特性名。</para></value>
    public IReadOnlyList<string> PeerFeatures { get; private set; } = [];

    /// <summary>
    /// Gets the underlying transport.
    /// <para>获取底层传输。</para>
    /// </summary>
    /// <value>The transport backing this connection. <para>支撑本连接的传输。</para></value>
    public ICommTransport Transport => _transport ?? throw new NotSupportedException("Server services do not expose a raw daemon transport.");

    /// <summary>
    /// Initializes a new instance of the <see cref="AdbConnection"/> class with a
    /// single authentication identity.
    /// <para>使用单个认证身份初始化 <see cref="AdbConnection"/> 类的新实例。</para>
    /// </summary>
    /// <param name="transport">The device transport. <para>设备传输。</para></param>
    /// <param name="authentication">The RSA authentication identity. <para>RSA 认证身份。</para></param>
    public AdbConnection(ICommTransport transport, AdbAuthentication authentication)
        : this(transport, authentication is null ? [] : [authentication])
    {
    }

    /// <summary>
    /// Initializes a new instance with an ordered list of authentication
    /// identities, tried in order on AUTH token challenges and disposed with
    /// the connection. Index 0 is the user identity, whose public half carries
    /// the RSAPUBLICKEY announcement.
    /// <para>使用有序认证身份列表初始化新实例；AUTH 令牌挑战时按顺序尝试各身份，
    /// 所有身份随连接一起释放。索引 0 是用户身份，其公钥半部用于 RSAPUBLICKEY 通告。</para>
    /// </summary>
    /// <param name="transport">The device transport. <para>设备传输。</para></param>
    /// <param name="authentications">The ordered RSA authentication identities. <para>有序 RSA 认证身份列表。</para></param>
    public AdbConnection(ICommTransport transport, IReadOnlyList<AdbAuthentication> authentications)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authentications = authentications ?? throw new ArgumentNullException(nameof(authentications));
        if (_authentications.Count == 0)
        {
            throw new ArgumentException("At least one authentication identity is required.", nameof(authentications));
        }
    }

    /// <summary>
    /// Connects to the ADB peer (sends CNXN and starts the message loop).
    /// <para>连接到 ADB 对端（发送 CNXN 并启动消息循环）。</para>
    /// </summary>
    public void Connect()
    {
        if (!TryBeginHandshake())
        {
            return;
        }

        try
        {
            Send(AdbMessaging.BuildConnect());
        }
        catch (Exception ex) when ((ex is System.ComponentModel.Win32Exception or IOException)
            && _transport is UsbDeviceTransport)
        {
            _transport.Reset();
            Send(AdbMessaging.BuildConnect());
        }

        StartAfterHandshake();
    }

    /// <summary>
    /// Asynchronously connects to the ADB peer.
    /// <para>异步连接到 ADB 对端。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the handshake write. <para>取消握手写出。</para></param>
    /// <exception cref="ObjectDisposedException">The connection is disposed. <para>连接已被释放。</para></exception>
    /// <exception cref="TimeoutException">The handshake did not reach the transport within the transport
    /// deadline. <para>握手未在传输期限内抵达传输层。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginHandshake())
        {
            return;
        }

        try
        {
            await SendAsync(AdbMessaging.BuildConnect(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is System.ComponentModel.Win32Exception or IOException)
            && _transport is UsbDeviceTransport)
        {
            _transport.Reset();
            await SendAsync(AdbMessaging.BuildConnect(), cancellationToken).ConfigureAwait(false);
        }

        StartAfterHandshake();
    }

    /// <summary>
    /// Performs the dispose checks, the server-target validation and the USB interface claim.
    /// <para>完成释放检查、server 目标校验与 USB 接口申请。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the CNXN handshake still has to be written.
    /// <para>仍需写出 CNXN 握手时返回 <see langword="true"/>。</para></returns>
    /// <exception cref="ObjectDisposedException">The connection is disposed. <para>连接已被释放。</para></exception>
    private bool TryBeginHandshake()
    {
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed))
            {
                throw new ObjectDisposedException(nameof(AdbConnection));
            }

            if (_connected && _server is null)
            {
                return false;
            }
        }

        if (_server is not null)
        {
            try { _server.ValidateTarget(); }
            catch { lock (_lock) _connected = false; throw; }
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
                _connected = true;
            }

            Release(_peerReady);
            return false;
        }

        if (_transport is UsbDeviceTransport directUsb)
        {
            Transports.Usb.Core.UsbAccessPolicy.RequireDirect(0xff, 0x42, 1,
                directUsb.Session.DeviceInfo.SerialNumber);
        }

        return true;
    }

    /// <summary>
    /// Marks the connection usable and starts the dispatch loop after the handshake was written.
    /// <para>握手写出后把连接标记为可用并启动分发循环。</para>
    /// </summary>
    private void StartAfterHandshake()
    {
        lock (_lock)
        {
            _connected = true;
        }

        _authKeyIndex = 0;
        _messageLoop = Task.Factory.StartNew(
            () => MessageLoopAsync(_loopCancellation.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Opens a raw service stream with the sustained-output receive budget.
    /// <para>以面向持续产出的接收预算打开裸服务流。</para>
    /// </summary>
    /// <param name="service">The service destination string. <para>服务目标字符串。</para></param>
    /// <returns>An ADB stream. <para>ADB 流。</para></returns>
    public AdbStream OpenStream(string service)
        => OpenStream(service, AdbStream.RawServiceReceiveBudgetBytes, AdbStream.DefaultWritePermitWaitMs);

    /// <summary>
    /// Opens a service stream with an explicit receive budget and write-permit wait.
    /// <para>以显式接收预算与写入许可等待时长打开服务流。</para>
    /// </summary>
    /// <param name="service">The service destination string. <para>服务目标字符串。</para></param>
    /// <param name="receiveBudgetBytes">Queued payload bytes the stream tolerates before faulting.
    /// <para>流在判定故障前可容忍的队列负载字节数。</para></param>
    /// <param name="writePermitWaitMs">Milliseconds a write waits for the previous chunk acknowledgement.
    /// <para>写入等待上一块确认的毫秒数。</para></param>
    /// <returns>An ADB stream. <para>ADB 流。</para></returns>
    internal AdbStream OpenStream(string service, int receiveBudgetBytes, int writePermitWaitMs)
    {
        var stream = RegisterStream(service, receiveBudgetBytes, writePermitWaitMs);
        Send(AdbMessaging.BuildOpen(stream.LocalId, service));
        return stream;
    }

    /// <summary>
    /// Opens a protocol-client stream with the shell and sync receive budget.
    /// <para>以 shell/sync 接收预算打开协议客户端流。</para>
    /// </summary>
    /// <param name="service">The service destination string. <para>服务目标字符串。</para></param>
    /// <returns>An ADB stream. <para>ADB 流。</para></returns>
    internal AdbStream OpenProtocolStream(string service)
        => OpenStream(service, AdbStream.ProtocolReceiveBudgetBytes, AdbStream.DefaultWritePermitWaitMs);

    /// <summary>
    /// Opens a protocol-client stream without blocking a thread on the OPEN frame.
    /// <para>打开协议客户端流，等待 OPEN 帧时不占用线程。</para>
    /// </summary>
    /// <param name="service">The service destination string. <para>服务目标字符串。</para></param>
    /// <param name="cancellationToken">Cancels the OPEN write. <para>取消 OPEN 写出。</para></param>
    /// <returns>A task resolving to the opened stream. <para>结果为已打开流的任务。</para></returns>
    /// <exception cref="TimeoutException">The OPEN frame did not reach the transport within the transport
    /// deadline. <para>OPEN 帧未在传输期限内抵达传输层。</para></exception>
    internal async ValueTask<AdbStream> OpenProtocolStreamAsync(string service, CancellationToken cancellationToken)
    {
        var stream = RegisterStream(service, AdbStream.ProtocolReceiveBudgetBytes, AdbStream.DefaultWritePermitWaitMs);
        await SendAsync(AdbMessaging.BuildOpen(stream.LocalId, service), cancellationToken).ConfigureAwait(false);
        return stream;
    }

    /// <summary>
    /// Allocates and registers the stream object for a service destination.
    /// <para>为服务目标分配并登记流对象。</para>
    /// </summary>
    /// <param name="service">The service destination string. <para>服务目标字符串。</para></param>
    /// <param name="receiveBudgetBytes">Queued payload bytes tolerated before faulting.
    /// <para>判定故障前可容忍的队列负载字节数。</para></param>
    /// <param name="writePermitWaitMs">Milliseconds a write waits for an acknowledgement.
    /// <para>写入等待确认的毫秒数。</para></param>
    /// <returns>The registered stream. <para>已登记的流。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.
    /// <para><paramref name="service"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The connection has not been established.
    /// <para>连接尚未建立。</para></exception>
    private AdbStream RegisterStream(string service, int receiveBudgetBytes, int writePermitWaitMs)
    {
        ArgumentNullException.ThrowIfNull(service);
        EnsureConnected();

        if (_server is not null)
        {
            var serviceStream = _server.Open(service);
            lock (_lock)
            {
                if (Volatile.Read(ref _disposed))
                {
                    serviceStream.Dispose();
                    throw new ObjectDisposedException(nameof(AdbConnection));
                }

                var id = _nextLocalId++;
                var serverStream = new AdbStream(this, id, serviceStream);
                _streams.Add(id, serverStream);
                return serverStream;
            }
        }

        lock (_lock)
        {
            var localId = _nextLocalId++;
            var stream = new AdbStream(this, localId, receiveBudgetBytes, writePermitWaitMs);
            _streams[localId] = stream;
            return stream;
        }
    }

    /// <summary>
    /// Waits for the peer CNXN banner to arrive (handshake complete).
    /// <para>等待对端 CNXN 横幅到达（握手完成）。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时（毫秒）。</para></param>
    /// <param name="cancellationToken">Cancels the handshake wait. <para>取消握手等待。</para></param>
    /// <returns><c>true</c> if the peer responded; otherwise, <c>false</c>. <para>对端是否响应。</para></returns>
    public bool WaitForPeer(int timeoutMs = 10000, CancellationToken cancellationToken = default)
        => _peerReady.Wait(timeoutMs, cancellationToken);

    /// <summary>
    /// Waits for the peer CNXN banner without holding a thread.
    /// <para>异步等待对端 CNXN 横幅，等待期间不占用线程。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时（毫秒）。</para></param>
    /// <param name="cancellationToken">Cancels the handshake wait. <para>取消握手等待。</para></param>
    /// <returns>A task whose result reports whether the peer banner arrived before the deadline.
    /// <para>结果指示对端横幅是否在期限前到达。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public Task<bool> WaitForPeerAsync(int timeoutMs, CancellationToken cancellationToken) =>
        _peerReady.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);

    /// <summary>
    /// Writes data to the specified stream on a caller thread that already owns it.
    /// <para>在已持有该流的调用线程上同步向流写入数据。</para>
    /// </summary>
    /// <param name="stream">The target stream. <para>目标流。</para></param>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <exception cref="InvalidOperationException">The stream was not acknowledged by the peer.
    /// <para>对端未确认该流。</para></exception>
    internal void WriteToStream(AdbStream stream, byte[] data)
    {
        EnsureConnected();

        if (stream.RemoteId == 0)
        {
            stream.WaitReady(5000);
        }

        if (stream.RemoteId == 0)
        {
            throw new InvalidOperationException("Stream was not acknowledged by the peer.");
        }

        Send(AdbMessaging.BuildWrite(stream.LocalId, stream.RemoteId, data));
    }

    /// <summary>
    /// Writes one acknowledged chunk of a stream without holding a thread.
    /// <para>异步写入流的一个已确认数据块，等待期间不占用线程。</para>
    /// </summary>
    /// <param name="stream">The target stream. <para>目标流。</para></param>
    /// <param name="data">The chunk payload. <para>块负载。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消写入。</para></param>
    /// <exception cref="InvalidOperationException">The stream was not acknowledged by the peer.
    /// <para>对端未确认该流。</para></exception>
    /// <exception cref="TimeoutException">The OPEN acknowledgement or the transport write exceeded its deadline
    /// while <paramref name="cancellationToken"/> stayed unset.
    /// <para>OPEN 确认或传输写出超过期限，且 <paramref name="cancellationToken"/> 未触发。</para></exception>
    internal async ValueTask WriteToStreamAsync(AdbStream stream, byte[] data, CancellationToken cancellationToken)
    {
        EnsureConnected();

        if (stream.RemoteId == 0)
        {
            await stream.WaitReadyAsync(cancellationToken).ConfigureAwait(false);
            if (stream.RemoteId == 0)
            {
                throw new InvalidOperationException("Stream was not acknowledged by the peer.");
            }
        }

        await SendAsync(AdbMessaging.BuildWrite(stream.LocalId, stream.RemoteId, data), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the specified stream and notifies the peer.
    /// <para>关闭指定流并通知对端。</para>
    /// </summary>
    /// <param name="stream">The stream to close. <para>要关闭的流。</para></param>
    internal void CloseStream(AdbStream stream)
    {
        lock (_lock) { _streams.Remove(stream.LocalId); }
        if (_server is null) Send(AdbMessaging.BuildClose(stream.LocalId, stream.RemoteId));
    }

    /// <summary>
    /// Closes the specified stream and notifies the peer without holding a thread.
    /// <para>异步关闭指定流并通知对端，等待期间不占用线程。</para>
    /// </summary>
    /// <param name="stream">The stream to close. <para>要关闭的流。</para></param>
    internal async ValueTask CloseStreamAsync(AdbStream stream)
    {
        lock (_lock) { _streams.Remove(stream.LocalId); }
        if (_server is null)
        {
            await SendAsync(AdbMessaging.BuildClose(stream.LocalId, stream.RemoteId), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a message to the transport from a synchronous boundary.
    /// <para>从同步边界向传输发送一条消息。</para>
    /// </summary>
    /// <param name="message">The message to send. <para>要发送的消息。</para></param>
    /// <exception cref="TimeoutException">The message did not reach the transport within the transport
    /// deadline. <para>消息未在传输期限内抵达传输层。</para></exception>
    private void Send(AdbMessage message)
    {
        if (!_sendLock.Wait(TransportDeadlineMs))
        {
            throw new TimeoutException("ADB transport send queue timed out.");
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        try
        {
            var header = AdbMessaging.SerializeHeader(message);
            Transport.Write(header, 0, header.Length, TransportDeadlineMs);

            if (message.Payload is { Length: > 0 } payload)
            {
                Transport.Write(payload, 0, payload.Length, TransportDeadlineMs);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends a message to the transport asynchronously.
    /// <para>异步向传输发送一条消息。</para>
    /// </summary>
    /// <param name="message">The message to send. <para>要发送的消息。</para></param>
    /// <param name="cancellationToken">Cancels the queue wait and both writes. <para>取消排队与两次写入。</para></param>
    /// <exception cref="ObjectDisposedException">The connection is disposed. <para>连接已被释放。</para></exception>
    /// <exception cref="TimeoutException">The message did not reach the transport within the transport deadline
    /// while <paramref name="cancellationToken"/> stayed unset.
    /// <para>消息未在传输期限内抵达传输层，且 <paramref name="cancellationToken"/> 未触发。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    private async ValueTask SendAsync(AdbMessage message, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TransportDeadlineMs);
        var token = deadline.Token;
        try
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("ADB transport send queue timed out.");
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            var header = AdbMessaging.SerializeHeader(message);
            await Transport.WriteAsync(header, 0, header.Length, Timeout.Infinite, token).ConfigureAwait(false);

            if (message.Payload is { Length: > 0 } payload)
            {
                await Transport.WriteAsync(payload, 0, payload.Length, Timeout.Infinite, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ADB transport write timed out after {TransportDeadlineMs} ms.");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Reads the next ADB message from the transport.
    /// <para>从传输读取下一条 ADB 消息。</para>
    /// </summary>
    /// <param name="cancellationToken">Stops the read when the connection shuts down. <para>连接关停时停止读取。</para></param>
    /// <returns>The next message read from the transport. <para>从传输读取的下一条消息。</para></returns>
    /// <exception cref="InvalidDataException">The frame failed header or payload validation.
    /// <para>该帧未通过头部或负载校验。</para></exception>
    /// <exception cref="TimeoutException">A partially received frame made no progress within the transport
    /// deadline. <para>已部分接收的帧在传输期限内没有进展。</para></exception>
    private async ValueTask<AdbMessage> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(AdbConstants.HeaderSize, TransportDeadlineMs, allowIdle: true,
            cancellationToken).ConfigureAwait(false);
        (var command, var arg0, var arg1, var payloadLength, var payloadCrc) = AdbMessaging.ParseHeader(header);

        if (payloadLength > AdbConstants.MaxPayload)
        {
            throw new InvalidDataException($"ADB payload length {payloadLength} exceeds the protocol maximum.");
        }

        byte[]? payload = null;
        if (payloadLength > 0)
        {
            payload = await ReadExactAsync((int)payloadLength, TransportDeadlineMs, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AdbMessaging.VerifyCrc(payload, payloadCrc);
        }

        return new AdbMessage(command, arg0, arg1, payload);
    }

    /// <summary>
    /// Main message dispatch loop running as a long-running task.
    /// <para>以长任务运行的主消息分发循环。</para>
    /// </summary>
    /// <param name="cancellationToken">Stops the loop when the connection is disposed. <para>连接释放时停止循环。</para></param>
    private async Task MessageLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!Volatile.Read(ref _disposed) && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                switch (message.Command)
                {
                    case AdbCommand.Auth:
                        await HandleAuthAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case AdbCommand.Cnxn:
                        HandleConnect(message);
                        break;
                    case AdbCommand.Open:
                        await HandleOpenAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case AdbCommand.Okay:
                        await HandleOkayAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case AdbCommand.Wrte:
                        await HandleWriteAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case AdbCommand.Clse:
                        HandleClose(message);
                        break;
                    case AdbCommand.Sync:
                        break;
                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FaultAllStreams(ex);
        }
    }

    /// <summary>
    /// Handles the AUTH handshake from the peer (TOKEN / SIGNATURE / RSAPUBLICKEY).
    /// <para>处理来自对端的 AUTH 握手（TOKEN / SIGNATURE / RSAPUBLICKEY）。</para>
    /// </summary>
    /// <remarks>
    /// Mirrors upstream <c>send_auth_response()</c>: each token is answered with the next identity of the
    /// chain and, once the chain is exhausted, the <c>RSAPUBLICKEY</c> announcement carries the public half
    /// of the user identity at index 0 (<c>adb_auth_get_userkey()</c> reads only the user key file), which is
    /// what makes the device show the authorization prompt for this host.
    /// <para>与上游 <c>send_auth_response()</c> 一致：每个令牌用身份链的下一个身份应答，链耗尽后的
    /// <c>RSAPUBLICKEY</c> 通告携带索引 0 处用户身份的公钥半部（<c>adb_auth_get_userkey()</c> 只读用户密钥
    /// 文件），正是这条通告让设备为本网段主机弹出授权提示。</para>
    /// </remarks>
    /// <param name="message">The AUTH message. <para>AUTH 消息。</para></param>
    /// <param name="cancellationToken">Cancels the reply write. <para>取消应答写出。</para></param>
    private async ValueTask HandleAuthAsync(AdbMessage message, CancellationToken cancellationToken)
    {
        switch ((AdbAuthType)message.Arg0)
        {
            case AdbAuthType.Token:
                var token = message.Payload ?? [];
                if (_authKeyIndex < _authentications.Count)
                {
                    var signature = _authentications[_authKeyIndex].SignToken(token);
                    _authKeyIndex++;
                    await SendAsync(AdbMessaging.BuildAuth(AdbAuthType.Signature, signature), cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    var publicKey = _authentications[0].BuildPublicKeyPayload();
                    await SendAsync(AdbMessaging.BuildAuth(AdbAuthType.RsaPublicKey, publicKey), cancellationToken)
                        .ConfigureAwait(false);
                }

                break;

            case AdbAuthType.RsaPublicKey:
                var explicitPublicKey = _authentications[0].BuildPublicKeyPayload();
                await SendAsync(AdbMessaging.BuildAuth(AdbAuthType.RsaPublicKey, explicitPublicKey), cancellationToken)
                    .ConfigureAwait(false);
                break;

            case AdbAuthType.Signature:
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Handles the CNXN connect message from the peer.
    /// <para>处理来自对端的 CNXN 连接消息。</para>
    /// </summary>
    /// <param name="message">The CNXN message. <para>CNXN 消息。</para></param>
    private void HandleConnect(AdbMessage message)
    {
        PeerVersion = message.Arg0;
        PeerMaxPayload = message.Arg1;
        PeerBanner = message.PayloadAsString();
        PeerFeatures = ParseFeatures(PeerBanner);
        Release(_peerReady);
    }

    /// <summary>
    /// Handles the OPEN message from the peer.
    /// <para>处理来自对端的 OPEN 消息。</para>
    /// </summary>
    /// <param name="message">The OPEN message. <para>OPEN 消息。</para></param>
    /// <param name="cancellationToken">Cancels the OKAY reply. <para>取消 OKAY 应答。</para></param>
    private async ValueTask HandleOpenAsync(AdbMessage message, CancellationToken cancellationToken)
    {
        AdbStream stream;
        lock (_lock)
        {
            stream = new AdbStream(this, _nextLocalId++, AdbStream.RawServiceReceiveBudgetBytes,
                AdbStream.DefaultWritePermitWaitMs);
            _streams[stream.LocalId] = stream;
        }

        await SendAsync(AdbMessaging.BuildOkay(stream.LocalId, message.Arg0), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the OKAY message from the peer.
    /// <para>处理来自对端的 OKAY 消息。</para>
    /// </summary>
    /// <param name="message">The OKAY message. <para>OKAY 消息。</para></param>
    /// <param name="cancellationToken">Cancels the close reply sent to an unknown stream.
    /// <para>取消向未知流发送的关闭帧。</para></param>
    private async ValueTask HandleOkayAsync(AdbMessage message, CancellationToken cancellationToken)
    {
        AdbStream? stream;
        lock (_lock)
        {
            _streams.TryGetValue(message.Arg1, out stream);
        }

        if (stream is null)
        {
            await SendAsync(AdbMessaging.BuildClose(message.Arg1, message.Arg0), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        stream.SetRemoteId(message.Arg0);
    }

    /// <summary>
    /// Handles the WRTE message from the peer.
    /// <para>处理来自对端的 WRTE 消息。</para>
    /// </summary>
    /// <param name="message">The WRTE message. <para>WRTE 消息。</para></param>
    /// <param name="cancellationToken">Cancels the acknowledgement or the close reply.
    /// <para>取消确认或关闭应答。</para></param>
    private async ValueTask HandleWriteAsync(AdbMessage message, CancellationToken cancellationToken)
    {
        AdbStream? stream;
        lock (_lock)
        {
            _streams.TryGetValue(message.Arg1, out stream);
        }

        if (stream is null)
        {
            await SendAsync(AdbMessaging.BuildClose(message.Arg1, message.Arg0), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        stream.EnqueueData(message.Payload ?? []);
        await SendAsync(AdbMessaging.BuildOkay(message.Arg1, message.Arg0), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the CLSE message from the peer.
    /// <para>处理来自对端的 CLSE 消息。</para>
    /// </summary>
    /// <param name="message">The CLSE message. <para>CLSE 消息。</para></param>
    private void HandleClose(AdbMessage message)
    {
        AdbStream? stream;
        lock (_lock)
        {
            if (_streams.TryGetValue(message.Arg1, out stream))
            {
                _streams.Remove(message.Arg1);
            }
        }

        if (stream is not null)
        {
            stream.MarkClosed();
        }
    }

    /// <summary>
    /// Faults all open streams due to an error.
    /// <para>因错误将所有打开的流标记为故障。</para>
    /// </summary>
    /// <param name="ex">The exception. <para>异常。</para></param>
    private void FaultAllStreams(Exception ex)
    {
        AdbStream[] streams;
        Volatile.Write(ref _loopFault, ex);
        lock (_lock)
        {
            streams = [.. _streams.Values];
            _streams.Clear();
            _connected = false;
        }

        foreach (var stream in streams)
        {
            stream.MarkFaulted(ex);
        }
    }

    /// <summary>
    /// Parses the features list from the peer banner.
    /// <para>从对端横幅解析特性列表。</para>
    /// </summary>
    /// <param name="banner">The peer banner string. <para>对端横幅字符串。</para></param>
    /// <returns>The parsed feature names. <para>解析出的特性名列表。</para></returns>
    private static IReadOnlyList<string> ParseFeatures(string? banner)
    {
        if (string.IsNullOrEmpty(banner))
        {
            return [];
        }

        var idx = banner.IndexOf("features=", StringComparison.Ordinal);
        if (idx < 0)
        {
            return [];
        }

        var remainder = banner[(idx + "features=".Length)..];
        var end = remainder.IndexOf(';');
        var value = end >= 0 ? remainder[..end] : remainder;
        return [.. value
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(feature => feature.Trim())
            .Where(feature => feature.Length > 0)];
    }

    /// <summary>
    /// Reads an exact number of bytes from the transport into a pooled frame buffer and copies the frame out at
    /// its true length.
    /// <para>从传输精确读取指定字节数到池化帧缓冲区，再按真实长度拷出该帧。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时（毫秒）。</para></param>
    /// <param name="allowIdle">Keep waiting between packets; incomplete packets still time out.
    /// <para>在包之间持续等待；未完成的包仍会超时。</para></param>
    /// <param name="cancellationToken">Stops the read when the connection shuts down. <para>连接关停时停止读取。</para></param>
    /// <returns>A task resolving to the frame bytes. <para>结果为帧字节的任务。</para></returns>
    /// <exception cref="TimeoutException">A partially received frame made no progress within
    /// <paramref name="timeoutMs"/>. <para>已部分接收的帧在 <paramref name="timeoutMs"/> 内没有进展。</para></exception>
    /// <exception cref="ObjectDisposedException">The connection was disposed mid-read.
    /// <para>读取过程中连接被释放。</para></exception>
    private async ValueTask<byte[]> ReadExactAsync(int length, int timeoutMs, bool allowIdle = false,
        CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var totalRead = 0;
            while (totalRead < length)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
                int n;
                try
                {
                    n = await Transport.ReadAsync(buffer, totalRead, length - totalRead, timeoutMs, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException) when (allowIdle && totalRead == 0 && !Volatile.Read(ref _disposed)
                    && Transport.IsConnected)
                {
                    continue;
                }

                if (n == 0)
                {
                    if (allowIdle && totalRead == 0 && !Volatile.Read(ref _disposed) && Transport.IsConnected)
                    {
                        continue;
                    }

                    throw new TimeoutException("ADB read timeout");
                }

                totalRead += n;
            }

            var frame = new byte[length];
            Buffer.BlockCopy(buffer, 0, frame, 0, length);
            return frame;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Signals a one-shot readiness primitive, ignoring the extra signals a repeated banner cannot use.
    /// <para>触发一次性的就绪原语，并忽略重复横幅无法使用的多余信号。</para>
    /// </summary>
    /// <param name="signal">The primitive to signal. <para>待触发的原语。</para></param>
    private static void Release(SemaphoreSlim signal)
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>
    /// Ensures the connection is established before performing operations.
    /// <para>确保在执行操作之前已建立连接。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection has not been established.
    /// <para>连接尚未建立。</para></exception>
    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Connection not established. Call Connect() first.");
        }
    }

    /// <summary>
    /// Releases the transport, streams, authentication identities and loop resources
    /// held by the connection.
    /// <para>释放连接持有的传输、流、认证身份与循环资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        _loopCancellation.Cancel();

        lock (_lock)
        {
            _connected = false;
            foreach (var stream in _streams.Values)
            {
                stream.MarkClosed();
            }

            _streams.Clear();
        }

        _transport?.Dispose();

        try
        {
            _ = _messageLoop?.Wait(MessageLoopJoinBudget);
        }
        catch (Exception)
        {
        }

        foreach (var auth in _authentications)
        {
            auth.Dispose();
        }

        _loopCancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}