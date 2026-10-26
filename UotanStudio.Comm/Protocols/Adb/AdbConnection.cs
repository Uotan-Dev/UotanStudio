using System.Buffers;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Represents an ADB connection over a device transport: CNXN handshake, authentication,
/// message dispatch loop, stream multiplexing, and the TLS upgrade when the peer answers CNXN with A_STLS.
/// <para>基于设备传输的 ADB 连接：CNXN 握手、认证、消息分发循环、流复用，以及对端用 A_STLS 应答 CNXN 时的 TLS 升级。</para>
/// </summary>
internal sealed class AdbConnection : IDisposable
{
    /// <summary>Deadline applied to one transport step: queueing for the send gate, writing a frame, or waiting for
    /// the next readable bytes. A read that keeps making progress resets this deadline per arrival.
    /// <para>单个传输步骤的期限：排队等待发送闸门、写出一帧，或等待下一批可读字节。持续有进展的读取会按每次
    /// 到达重置该期限。</para></summary>
    private const int TransportDeadlineMs = 5000;

    /// <summary>Largest frame read directly into its result array instead of through a pooled staging buffer.
    /// <para>直接读入结果数组、而不经池化暂存缓冲的最大帧长度。</para></summary>
    private const int DirectFrameReadThreshold = 64 * 1024;

    /// <summary>Number of inbound frames decoded on this connection, used to keep the first frame's raw header at
    /// debug level and the rest at trace level.
    /// <para>本连接已解码的入站帧数量；用于让首帧的原始头部落在 debug 级别、其余落在 trace 级别。</para>
    /// </summary>
    private int _inboundMessageCount;

    /// <summary>Set once a rejected first inbound frame triggered the one bounded handshake resynchronization.
    /// <para>首个入站帧被拒并触发唯一一次有界握手重同步后置位。</para>
    /// </summary>
    private int _handshakeResyncUsed;

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
    private int _peerReadyObserved;
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
    /// Gets whether the connection is established and its transport still reports a usable link.
    /// <para>获取连接是否已建立且其传输仍报告可用链路。</para>
    /// </summary>
    /// <value>True when the connection is established and the transport has not faulted.
    /// <para>连接已建立且传输未失效时为真。</para></value>
    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _connected && (_transport is null || _transport.IsConnected);
            }
        }
    }

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
            catch { lock (_lock) { _connected = false; ResetPeerReady(); } throw; }
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
                _connected = true;
            }

            Volatile.Write(ref _peerReadyObserved, 1);
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
        if (_server is null)
        {
            Send(AdbMessaging.BuildOpen(stream.LocalId, service));
        }

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
        if (_server is null)
        {
            await SendAsync(AdbMessaging.BuildOpen(stream.LocalId, service), cancellationToken).ConfigureAwait(false);
        }

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
    /// Waits for the peer CNXN banner to arrive (handshake complete). A failed message loop releases the wait and
    /// its captured fault is rethrown here, so callers observe the real handshake failure instead of waiting for the
    /// full timeout.
    /// <para>等待对端 CNXN 横幅到达（握手完成）。消息循环失败时会释放等待，并在此重新抛出捕获到的故障，
    /// 使调用方立即看到真实握手失败，而不是等满超时。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时（毫秒）。</para></param>
    /// <param name="cancellationToken">Cancels the handshake wait. <para>取消握手等待。</para></param>
    /// <returns><c>true</c> if the peer responded; otherwise, <c>false</c>. <para>对端是否响应。</para></returns>
    /// <exception cref="AdbTlsRequiredException">The peer requires TLS but the transport is plaintext.
    /// <para>对端要求 TLS，但传输为明文。</para></exception>
    /// <exception cref="IOException">The message loop captured an I/O failure before CNXN arrived.
    /// <para>CNXN 到达前消息循环捕获到 I/O 故障。</para></exception>
    /// <exception cref="TimeoutException">The message loop captured a non-I/O failure.
    /// <para>消息循环捕获到非 I/O 故障。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public bool WaitForPeer(int timeoutMs = 10000, CancellationToken cancellationToken = default)
    {
        ThrowIfLoopFaulted();
        if (Volatile.Read(ref _peerReadyObserved) != 0)
        {
            DrainAndObservePeer();
            ThrowIfLoopFaulted();
            return true;
        }

        if (!_peerReady.Wait(timeoutMs, cancellationToken))
        {
            ThrowIfLoopFaulted();
            return false;
        }

        ThrowIfLoopFaulted();
        return Volatile.Read(ref _peerReadyObserved) != 0;
    }

    /// <summary>
    /// Waits for the peer CNXN banner without holding a thread. A failed message loop releases the wait and its
    /// captured fault is rethrown here, exactly as in <see cref="WaitForPeer"/>.
    /// <para>异步等待对端 CNXN 横幅，等待期间不占用线程。消息循环失败时会释放等待，并像
    /// <see cref="WaitForPeer"/> 一样在此重新抛出捕获到的故障。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时（毫秒）。</para></param>
    /// <param name="cancellationToken">Cancels the handshake wait. <para>取消握手等待。</para></param>
    /// <returns>A task whose result reports whether the peer banner arrived before the deadline.
    /// <para>结果指示对端横幅是否在期限前到达。</para></returns>
    /// <exception cref="AdbTlsRequiredException">The peer requires TLS but the transport is plaintext.
    /// <para>对端要求 TLS，但传输为明文。</para></exception>
    /// <exception cref="IOException">The message loop captured an I/O failure before CNXN arrived.
    /// <para>CNXN 到达前消息循环捕获到 I/O 故障。</para></exception>
    /// <exception cref="TimeoutException">The message loop captured a non-I/O failure.
    /// <para>消息循环捕获到非 I/O 故障。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async Task<bool> WaitForPeerAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        ThrowIfLoopFaulted();
        if (Volatile.Read(ref _peerReadyObserved) != 0)
        {
            DrainAndObservePeer();
            ThrowIfLoopFaulted();
            return true;
        }

        if (!await _peerReady.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false))
        {
            ThrowIfLoopFaulted();
            return false;
        }

        ThrowIfLoopFaulted();
        return Volatile.Read(ref _peerReadyObserved) != 0;
    }

    /// <summary>
    /// Rethrows the message loop's captured failure in the handshake shape callers can act on: a plaintext
    /// transport that needs TLS keeps its dedicated exception, an I/O failure stays an <see cref="IOException"/>
    /// carrying the authorization hint for secure transports, and any other failure becomes a
    /// <see cref="TimeoutException"/>.
    /// <para>按调用方可处理的形式重新抛出消息循环捕获的故障：需要 TLS 的明文传输保留专用异常；I/O 故障保持
    /// <see cref="IOException"/> 并为安全传输附带授权提示；其余故障转为 <see cref="TimeoutException"/>。</para>
    /// </summary>
    /// <exception cref="AdbTlsRequiredException">The peer requires TLS but the transport is plaintext.
    /// <para>对端要求 TLS，但传输为明文。</para></exception>
    /// <exception cref="IOException">The captured failure is an I/O failure. <para>捕获的故障是 I/O 故障。</para></exception>
    /// <exception cref="TimeoutException">The captured failure is not an I/O failure.
    /// <para>捕获的故障不是 I/O 故障。</para></exception>
    private string TransportTag => _transport?.GetType().Name ?? "transport";

    private void ThrowIfLoopFaulted()
    {
        if (LoopFault is not { } fault)
        {
            return;
        }

        CommTrace.Error("adb-link", $"handshake fault ({TransportTag}): {fault.GetType().Name}: {fault.Message}");

        if (fault is AdbTlsRequiredException tlsRequired)
        {
            throw tlsRequired;
        }

        if (fault is IOException ioFailure)
        {
            var hint = _transport is Transports.Tcp.AdbTlsTransport
                ? " The device may not have authorized this host's key; re-run pairing or accept its debugging prompt."
                : string.Empty;
            throw new IOException("ADB handshake failed: " + ioFailure.Message + hint, ioFailure);
        }

        throw new TimeoutException("ADB handshake failed: " + fault.Message, fault);
    }

    private bool DrainAndObservePeer()
    {
        _peerReady.Wait(0);
        return true;
    }

    private void ResetPeerReady()
    {
        Volatile.Write(ref _peerReadyObserved, 0);
        while (_peerReady.Wait(0))
        {
        }
    }

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
    /// <exception cref="IOException">The transport accepted fewer bytes than requested; the connection is closed.
    /// <para>传输接受的字节数少于请求值；连接已被关闭。</para></exception>
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
            try
            {
                VerifyWrite(Transport.Write(header, 0, header.Length, TransportDeadlineMs), header.Length);

                if (message.Payload is { Length: > 0 } payload)
                {
                    VerifyWrite(Transport.Write(payload, 0, payload.Length, TransportDeadlineMs), payload.Length);
                }
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                InvalidateFailedTransport(ex);
                throw;
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
    /// <exception cref="IOException">The transport accepted fewer bytes than requested; the connection is closed.
    /// <para>传输接受的字节数少于请求值；连接已被关闭。</para></exception>
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
            var header = AdbMessaging.SerializeHeader(message, PeerVersion);
            try
            {
                VerifyWrite(
                    await Transport.WriteAsync(header, 0, header.Length, TransportDeadlineMs, token).ConfigureAwait(false),
                    header.Length);

                if (message.Payload is { Length: > 0 } payload)
                {
                    VerifyWrite(
                        await Transport.WriteAsync(payload, 0, payload.Length, TransportDeadlineMs, token).ConfigureAwait(false),
                        payload.Length);
                }
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                InvalidateFailedTransport(ex);
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var stalled = new TimeoutException($"ADB transport write timed out after {TransportDeadlineMs} ms.");
            InvalidateFailedTransport(stalled);
            throw stalled;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Rejects a transport write that accepted fewer bytes than it was given and tears the connection down, because
    /// the stream is desynchronized once a frame is short written and the remainder must not be appended to it.
    /// <para>拒绝实际写入字节数少于请求值的传输写入并断开连接：帧被短写后流已失步，剩余字节不得再追加进去。</para>
    /// </summary>
    /// <param name="written">The byte count the transport reported. <para>传输报告的写入字节数。</para></param>
    /// <param name="length">The byte count the connection asked for. <para>连接请求写入的字节数。</para></param>
    /// <exception cref="IOException">The transport accepted fewer bytes than requested and the connection was closed.
    /// <para>传输接受的字节数少于请求值，连接已被关闭。</para></exception>
    private void VerifyWrite(long written, int length)
    {
        if (written == length)
        {
            return;
        }

        lock (_lock)
        {
            _connected = false;
            ResetPeerReady();
        }

        try
        {
            _transport?.Dispose();
        }
        catch (Exception)
        {
        }

        throw new IOException($"ADB transport accepted {written} of {length} bytes; the connection was closed.");
    }

    /// <summary>
    /// Decides whether a failure means the transport pipe itself is unusable, instead of only the one command.
    /// <para>判定一次失败是否表示传输管道本身不可用，而不只是这一条命令失败。</para>
    /// </summary>
    /// <param name="exception">The failure raised by the transport.<para>传输抛出的故障。</para></param>
    /// <returns><see langword="true"/> for I/O, timeout, socket-layer and disposal failures.
    /// <para>I/O、超时、套接字层与释放类故障返回 <see langword="true"/>。</para></returns>
    private static bool IsTransportFailure(Exception exception)
        => exception is IOException or TimeoutException or ObjectDisposedException
            or System.ComponentModel.Win32Exception;

    /// <summary>
    /// Takes the connection out of service after a failed transport read or write: the pipe may already hold a partial
    /// frame, so no later command may reuse it. The transport is closed, which faults the dispatch loop and every open
    /// stream, and <see cref="IsConnected"/> reports <see langword="false"/> so a shared or pooled session is rebuilt
    /// instead of writing into a dead connection again. A failure before the handshake completes is left to the connect
    /// path, which owns its own transport reset and retry.
    /// <para>传输读取或写入失败之后让连接停止服务：管道中可能已残留半帧，后续命令不得再复用它。关闭传输会使分发循环
    /// 与所有已打开的流进入故障状态，<see cref="IsConnected"/> 随之为 <see langword="false"/>，共享或池化的会话因此
    /// 重建，而不是继续往已失效的连接里写。握手完成之前的失败留给连接路径处理，它自己负责传输重置与重试。</para>
    /// </summary>
    /// <param name="failure">The failure that ended the transport. <para>终止传输的故障。</para></param>
    private void InvalidateFailedTransport(Exception failure)
    {
        lock (_lock)
        {
            if (!_connected)
            {
                return;
            }

            _connected = false;
            ResetPeerReady();
        }

        CommTrace.Debug("adb-link",
            $"closing the {TransportTag} connection after a transport failure: {failure.GetType().Name}: {failure.Message}");
        try
        {
            _transport?.Dispose();
        }
        catch (Exception)
        {
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
        while (true)
        {
            if (await TryReadFrameAsync(cancellationToken).ConfigureAwait(false) is { } message)
            {
                return message;
            }
        }
    }

    /// <summary>
    /// Reads one inbound frame, or returns <c>null</c> when the bounded handshake resynchronization cleared the pipe
    /// and re-sent CNXN, in which case the caller reads again.
    /// <para>读取一条入站帧；有界握手重同步已清零管道并重发 CNXN 时返回 <c>null</c>，由调用方重新读取。</para>
    /// </summary>
    /// <param name="cancellationToken">Stops the read when the connection shuts down. <para>连接关停时停止读取。</para></param>
    /// <returns>The next frame, or <c>null</c> after a resynchronization. <para>下一条帧；重同步后为 <c>null</c>。</para></returns>
    private async ValueTask<AdbMessage?> TryReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(AdbConstants.HeaderSize, TransportDeadlineMs, allowIdle: true,
            cancellationToken).ConfigureAwait(false);
        var sequence = Interlocked.Increment(ref _inboundMessageCount);
        var headerPreview = PayloadPreview.Format(header, AdbConstants.HeaderSize);
        CommTrace.Write(sequence == 1 ? CommLogLevel.Debug : CommLogLevel.Trace, "adb-link",
            $"rx #{sequence} header ({TransportTag}): {headerPreview}");

        (AdbCommand Command, uint Arg0, uint Arg1, uint PayloadLength, uint PayloadCrc) decoded;
        try
        {
            decoded = AdbMessaging.ParseHeader(header);
        }
        catch (InvalidDataException ex)
        {
            CommTrace.Error("adb-link",
                $"rx #{sequence} header rejected ({TransportTag}): {headerPreview} — {ex.Message} " +
                "A plausible command with a wrong magic means the frame started mid-stream; fully foreign bytes mean " +
                "another reader (adb server, a second host handle, or the wrong USB interface) is splitting the stream.");

            if (await TryResynchronizeHandshakeAsync(headerPreview, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            await ProbeForeignStreamAsync(sequence, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var (command, arg0, arg1, payloadLength, payloadCrc) = decoded;
        if (payloadLength > InboundPayloadLimit())
        {
            CommTrace.Error("adb-link",
                $"rx #{sequence} header ({TransportTag}): {headerPreview} — payload length {payloadLength} exceeds the limit {InboundPayloadLimit()}.");
            throw new InvalidDataException(
                $"ADB payload length {payloadLength} exceeds the peer's negotiated payload limit.");
        }

        byte[]? payload = null;
        if (payloadLength > 0)
        {
            payload = await ReadExactAsync((int)payloadLength, TransportDeadlineMs, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            CommTrace.Write(CommLogLevel.Trace, "adb-link",
                $"rx #{sequence} {command} payload {payload.Length} B ({TransportTag}).");
            CommTrace.Payload("adb-link", $"rx #{sequence} {command}", payload);
            if (payloadCrc != 0 && payloadCrc != AdbMessaging.ComputeCrc(payload))
            {
                // The legacy additive checksum is not an integrity gate upstream: `check_header` validates the
                // magic and the payload length only, and modern peers always send zero here. A non-zero mismatch
                // is therefore worth a diagnostic but never a rejected frame.
                CommTrace.Write(CommLogLevel.Debug, "adb-link",
                    $"rx #{sequence} {command} carries legacy checksum 0x{payloadCrc:X8} that does not match the " +
                    $"payload; upstream does not validate this field, accepting the frame.");
            }
        }

        return new AdbMessage(command, arg0, arg1, payload);
    }

    /// <summary>
    /// Sends one CLSE frame for every open stream, the ADB wire format's own close, and waits at most
    /// <paramref name="timeout"/> for those writes so the device ends the sessions it still believes are open.
    /// <para>为每条已打开的流发送一帧 CLSE（ADB 线协议自身的关闭帧），并最多等待 <paramref name="timeout"/> 让这些
    /// 写入完成，使设备结束它仍认为打开的会话。</para>
    /// </summary>
    /// <param name="timeout">Upper bound on the whole farewell. <para>整段告别的上限。</para></param>
    /// <param name="cancellationToken">Cancels the farewell. <para>取消告别。</para></param>
    /// <returns>A task completing when every close frame was written or the budget expired.
    /// <para>所有关闭帧写出或预算耗尽时完成的任务。</para></returns>
    internal async ValueTask CloseAllStreamsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        AdbStream[] streams;
        lock (_lock)
        {
            streams = [.. _streams.Values];
        }

        if (streams.Length == 0)
        {
            return;
        }

        var budget = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMilliseconds(500);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(budget);
        var sent = 0;
        foreach (var stream in streams)
        {
            var remoteId = stream.RemoteId;
            if (remoteId == 0)
            {
                continue;
            }

            try
            {
                await SendAsync(AdbMessaging.BuildClose(stream.LocalId, remoteId), cancellation.Token)
                    .ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }
        }

        CommTrace.Write(CommLogLevel.Info, "adb-link",
            $"shutdown farewell ({TransportTag}): closed {sent} of {streams.Length} stream(s).");
    }

    /// <summary>
    /// Clears the inbound pipe and re-sends CNXN once when the very first frame of a direct device link is rejected.
    /// A stale remainder (a previous host session that stopped mid-frame, or bytes still queued in the endpoint when
    /// the interface was claimed) desynchronizes the stream before the banner, and without this the whole session —
    /// every later detail query and operation — fails on a link that only needed its pipe cleared.
    /// <para>直连设备链路的首个帧被拒时清空入站管道并重发一次 CNXN。上一主机会话停在帧中途、或申请接口时端点里仍
    /// 排着字节，都会在横幅到达前把流弄错位；没有这一步，整条会话（之后每一次详情查询与操作）都会在一个只需清零
    /// 管道即可恢复的链路上持续失败。</para>
    /// </summary>
    /// <param name="rejectedHeader">Preview of the rejected frame header for the trace. <para>被拒帧头部的预览文本。</para></param>
    /// <param name="cancellationToken">Stops the resynchronization when the connection shuts down.
    /// <para>连接关停时停止重同步。</para></param>
    /// <returns><see langword="true"/> when the pipe was cleared and CNXN re-sent, so the caller reads again.
    /// <para>已清零管道并重发 CNXN、调用方应重新读取时为 <see langword="true"/>。</para></returns>
    private async ValueTask<bool> TryResynchronizeHandshakeAsync(string rejectedHeader, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _peerReadyObserved) != 0 || _server is not null || _transport is null ||
            _transport is Transports.Tcp.AdbTlsTransport)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _handshakeResyncUsed, 1, 0) != 0)
        {
            return false;
        }

        CommTrace.Error("adb-link",
            $"{DescribeLink()} first inbound frame {rejectedHeader} is not an ADB frame; clearing the inbound pipe and re-sending CNXN once.");

        Transport.Reset();
        await SendAsync(AdbMessaging.BuildConnect(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Decides whether the endpoint keeps streaming foreign bytes or was only holding one stale remainder. Once the
    /// single resynchronization has been spent, up to three short follow-up reads measure whether data keeps arriving on
    /// its own: continuous arrivals mean the bound endpoint carries another service (diagnostics composition, a stopped
    /// adbd, or a second owner), while silence means the first frame was residual data and the link itself is fine. A TLS
    /// transport is not probed: a foreign first frame there cannot be explained by a shared endpoint, and the bounded
    /// probe read would break the record layer of a connection that is already being discarded.
    /// <para>判定端点是在持续流出陌生字节，还是只残留了一段旧数据。一次性重同步用掉之后，最多再做三次短读，看数据是否
    /// 自行继续到达：持续到达说明绑定的端点承载的是别的服务（诊断复合、adbd 未运行，或另一占用方），静默则说明首帧只是
    /// 残留数据、链路本身没问题。TLS 传输不做该探测：其中的陌生首帧无法用端点共用解释，而带期限的探测读取会破坏本就要
    /// 丢弃的连接上的记录层。</para>
    /// </summary>
    /// <param name="sequence">The rejected frame's sequence number for the trace.
    /// <para>被拒帧的序号，用于日志。</para></param>
    /// <param name="cancellationToken">Stops the probe when the connection shuts down.
    /// <para>连接关停时停止探测。</para></param>
    private async ValueTask ProbeForeignStreamAsync(int sequence, CancellationToken cancellationToken)
    {
        if (_transport is Transports.Tcp.AdbTlsTransport)
        {
            return;
        }

        const int ProbeCount = 3;
        const int ProbeTimeoutMs = 300;
        for (var probe = 1; probe <= ProbeCount; probe++)
        {
            var started = Environment.TickCount64;
            byte[] next;
            try
            {
                next = await ReadExactAsync(AdbConstants.HeaderSize, ProbeTimeoutMs,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                CommTrace.Error("adb-link",
                    $"{DescribeLink()} probe {probe}/{ProbeCount} after rejected frame #{sequence}: no further bytes within " +
                    $"{ProbeTimeoutMs} ms, so the endpoint is idle and the rejected frame was a stale remainder rather than a live service.");
                return;
            }

            var elapsed = Environment.TickCount64 - started;
            if (IsAdbHeader(next))
            {
                CommTrace.Error("adb-link",
                    $"{DescribeLink()} probe {probe}/{ProbeCount} got a valid ADB header after {elapsed} ms " +
                    $"({PayloadPreview.Format(next, AdbConstants.HeaderSize)}); the peer started speaking ADB, but this probe consumed " +
                    "that frame, so the session has to be recreated.");
                return;
            }

            CommTrace.Error("adb-link",
                $"{DescribeLink()} probe {probe}/{ProbeCount}: still foreign after {elapsed} ms — " +
                PayloadPreview.Format(next, AdbConstants.HeaderSize));
        }

        var serverListening = await IsAdbServerListeningAsync().ConfigureAwait(false);
        CommTrace.Error("adb-link",
            $"{DescribeLink()} kept streaming foreign frames at full rate; the bound endpoint is not carrying ADB and the device " +
            "never answered CNXN. " + (serverListening
                ? "An ADB server is listening on 127.0.0.1:5037; Windows allows several handles on one WinUSB interface, so a server " +
                  "that already attached this device splits every frame with this session. Stop it (adb kill-server) or use the server route."
                : "No ADB server is listening, so the endpoint itself is streaming non-ADB data (diagnostics/fastboot composition, or " +
                  "the phone's USB adb function is not active)."));
    }

    /// <summary>
    /// Reports whether an ADB server currently accepts connections on its default endpoint; an inconclusive probe is
    /// treated as "listening" because a shared WinUSB interface is worse than a refused open.
    /// <para>报告 ADB server 是否正在默认端点上接受连接；探测无结论时按"正在监听"处理，因为共用 WinUSB 接口的代价高于
    /// 拒绝一次打开。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the server accepts a connection or the probe was inconclusive.
    /// <para>server 接受连接或探测无结论时为 <see langword="true"/>。</para></returns>
    private static async ValueTask<bool> IsAdbServerListeningAsync()
    {
        try
        {
            return await DebugBridgeRoute.IsListeningAsync(DebugBridgeEndpoints.ServerHost,
                DebugBridgeEndpoints.AdbServerPort).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// Renders one interface from the configuration descriptor with its class triple and endpoints.
    /// <para>渲染配置描述符中的一个接口及其类别三元组与端点。</para>
    /// </summary>
    /// <param name="iface">Interface metadata from the configuration descriptor. <para>来自配置描述符的接口元数据。</para></param>
    /// <returns>Text such as <c>#02 FF/42/01 eps [0x86/02/512B, 0x03/02/512B]</c>.
    /// <para>形如 <c>#02 FF/42/01 eps [0x86/02/512B, 0x03/02/512B]</c> 的文本。</para></returns>
    private static string DescribeInterface(UsbInterfaceInfo iface) =>
        $"#{iface.InterfaceNumber:X2} {iface.Class:X2}/{iface.SubClass:X2}/{iface.Protocol:X2} eps [" +
        string.Join(", ", iface.Endpoints.Select(endpoint =>
            $"0x{endpoint.EndpointAddress:X2}/{endpoint.Attributes:X2}/{endpoint.MaxPacketSize}B")) + "]";

    /// <summary>
    /// Reports whether a raw 24-byte header starts a well-formed ADB message.
    /// <para>判断原始 24 字节头部是否构成一条格式正确的 ADB 消息。</para>
    /// </summary>
    /// <param name="header">Candidate header bytes. <para>候选头部字节。</para></param>
    /// <returns><see langword="true"/> when the magic inverts the command word.
    /// <para>magic 与命令字按位取反匹配时为 <see langword="true"/>。</para></returns>
    private static bool IsAdbHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < AdbConstants.HeaderSize)
        {
            return false;
        }

        var command = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        return (command ^ 0xFFFFFFFF) == magic;
    }

    /// <summary>
    /// Describes the link for the trace: the transport type plus, for a direct USB link, the claimed interface and the
    /// bulk endpoints actually in use, which is what distinguishes a desynchronized stream from the wrong endpoints.
    /// <para>为追踪描述链路：传输类型，以及直连 USB 链路上实际使用的接口与批量端点——它正是「流错位」与「端点选错」
    /// 的区别所在。</para>
    /// </summary>
    /// <returns>One-line link description. <para>单行链路描述。</para></returns>
    private string DescribeLink()
    {
        if (_transport is not UsbDeviceTransport usb)
        {
            return TransportTag;
        }

        var info = usb.Session.DeviceInfo;
        var description = $"{TransportTag}[{info.VendorId:X4}:{info.ProductId:X4} " +
            $"iface {info.InterfaceClass:X2}/{info.InterfaceSubClass:X2}/{info.InterfaceProtocol:X2} " +
            $"ep in {usb.Session.EndpointIn:X2} out {usb.Session.EndpointOut:X2} " +
            $"serial {info.SerialNumber ?? "-"}]";
        if (info.EnumeratedInterfaces.Count > 0)
        {
            description += " descriptor interfaces: " + string.Join("; ", info.EnumeratedInterfaces.Select(DescribeInterface));
        }

        return description;
    }

    /// <summary>
    /// Returns the largest inbound payload accepted on this connection: the peer's advertised A_MAX_PAYLOAD capped
    /// at the protocol maximum, or the protocol maximum itself until the peer advertises one.
    /// <para>返回本连接接受的入站负载上限：对端宣告的 A_MAX_PAYLOAD 且不超过协议上限；对端尚未宣告时直接使用协议上限。</para>
    /// </summary>
    /// <returns>The inbound payload ceiling in bytes. <para>入站负载上限（字节）。</para></returns>
    private uint InboundPayloadLimit() => PeerMaxPayload == 0
        ? AdbConstants.MaxPayload
        : Math.Min(PeerMaxPayload, AdbConstants.MaxPayload);

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
                    case AdbCommand.Fail:
                        await HandleFailAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case AdbCommand.Stls:
                        await HandleStlsAsync(message, cancellationToken).ConfigureAwait(false);
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
        if (_transport is Transports.Tcp.AdbTlsTransport { IsEncrypted: true })
        {
            // Upstream ignores every AUTH message once the transport runs inside TLS.
            return;
        }

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
        PeerVersion = Math.Min(message.Arg0, AdbConstants.ProtocolVersion);
        PeerMaxPayload = Math.Min(message.Arg1, AdbConstants.MaxPayload);
        PeerBanner = message.PayloadAsString();
        PeerFeatures = ParseFeatures(PeerBanner);
        Volatile.Write(ref _peerReadyObserved, 1);
        Release(_peerReady);
    }

    /// <summary>
    /// Handles the peer's A_STLS offer: replies with the 24-byte STLS header carrying our current STLS version and
    /// upgrades the transport to TLS, after which the loop keeps reading ADB messages over the encrypted stream.
    /// Upstream ordering makes this the answer to our CNXN, so a plaintext device simply never sends it.
    /// <para>处理对端的 A_STLS 提议：回复携带本端当前 STLS 版本的 24 字节 STLS 头并把传输升级为 TLS，之后循环继续在
    /// 加密流上读取 ADB 消息。按上游顺序它是对主机 CNXN 的应答，因此明文设备根本不会发送它。</para>
    /// </summary>
    /// <param name="message">The A_STLS message. <para>A_STLS 消息。</para></param>
    /// <param name="cancellationToken">Cancels the reply write. <para>取消应答写出。</para></param>
    /// <exception cref="AdbTlsRequiredException">The transport is plaintext and cannot provide the requested
    /// TLS upgrade. <para>传输为明文，无法提供所请求的 TLS 升级。</para></exception>
    /// <exception cref="InvalidDataException">The peer requested an STLS version older than
    /// <see cref="AdbConstants.StlsVersionMin"/>. <para>对端请求的 STLS 版本早于
    /// <see cref="AdbConstants.StlsVersionMin"/>。</para></exception>
    /// <exception cref="IOException">The reply write or the TLS handshake failed.
    /// <para>应答写出或 TLS 握手失败。</para></exception>
    private async ValueTask HandleStlsAsync(AdbMessage message, CancellationToken cancellationToken)
    {
        if (_transport is not Transports.Tcp.AdbTlsTransport tls)
        {
            throw new AdbTlsRequiredException(
                "The ADB peer answered with A_STLS, but this transport is plaintext. Reconnect over the secure ADB service.");
        }

        if (message.Arg0 < AdbConstants.StlsVersionMin)
        {
            throw new InvalidDataException($"Unsupported ADB STLS version 0x{message.Arg0:X8}.");
        }

        await SendAsync(new AdbMessage(AdbCommand.Stls, AdbConstants.StlsVersion, 0), cancellationToken)
            .ConfigureAwait(false);
        tls.UpgradeToTls();
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
    /// Handles the FAIL message from the peer: a sync or shell service reports a failed request with an ADB FAIL
    /// frame carrying the textual reason, which faults the addressed stream so awaiting callers observe the
    /// device's message instead of a silent close.
    /// <para>处理来自对端的 FAIL 消息：sync 或 shell 服务以携带文本原因的 ADB FAIL 帧报告请求失败，使被寻址的流
    /// 转入故障，等待方观察到设备消息而不是无提示的关闭。</para>
    /// </summary>
    /// <param name="message">The FAIL message.<para>FAIL 消息。</para></param>
    /// <param name="cancellationToken">Cancels the close reply.<para>取消关闭应答。</para></param>
    private async ValueTask HandleFailAsync(AdbMessage message, CancellationToken cancellationToken)
    {
        AdbStream? stream;
        lock (_lock)
        {
            _streams.TryGetValue(message.Arg1, out stream);
        }

        var reason = message.Payload is { Length: > 0 } payload
            ? System.Text.Encoding.UTF8.GetString(payload)
            : "unknown peer failure";
        stream?.MarkFaulted(new IOException($"The device reported a failure: {reason}"));
        await SendAsync(AdbMessaging.BuildClose(message.Arg1, message.Arg0), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the OKAY message from the peer. A zero arg0 carries no remote stream id, so it is treated as the
    /// flow-control acknowledgement of a WRTE, which releases the stream's outstanding write permit and never closes
    /// an unknown stream; only a non-zero arg0 names the peer's remote id for our OPEN.
    /// <para>处理来自对端的 OKAY 消息。arg0 为零时不携带远端流标识，因此按 WRTE 的流控确认处理：释放该流的在途
    /// 写入许可，且绝不关闭未知流；只有非零 arg0 才携带对端为我们的 OPEN 分配的远端流标识。</para>
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

        if (message.Arg0 == 0)
        {
            stream?.AcknowledgeWrite();
            return;
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

        if (stream.IsClosed)
        {
            await CloseStreamAsync(stream).ConfigureAwait(false);
            return;
        }

        stream.EnqueueData(message.Payload ?? []);
        await SendAsync(AdbMessaging.BuildOkay(message.Arg1, message.Arg0), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the CLSE message from the peer. The frame is only applied when its arg1 names a locally known stream
    /// and its arg0 either names that stream's peer id or is zero; unknown or foreign ownership is not treated as a
    /// normal close, because a stale CLSE must never end a live stream.
    /// <para>处理来自对端的 CLSE 消息。只有 arg1 指向本地已知的流，且 arg0 指向该流的对端 id 或为零时才应用；
    /// 未知或不属于该流的归属不按正常关闭处理，陈旧的 CLSE 绝不能结束仍在使用的流。</para>
    /// </summary>
    /// <param name="message">The CLSE message. <para>CLSE 消息。</para></param>
    private void HandleClose(AdbMessage message)
    {
        AdbStream? stream = null;
        lock (_lock)
        {
            if (message.Arg1 != 0 && _streams.TryGetValue(message.Arg1, out stream))
            {
                if (message.Arg0 != 0 && stream.RemoteId != 0 && message.Arg0 != stream.RemoteId)
                {
                    return;
                }

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

        Release(_peerReady);

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
    /// Reads an exact number of bytes from the transport. Frames within <see cref="DirectFrameReadThreshold"/> are
    /// filled in place, so a small frame is never copied through a second buffer.
    /// <para>从传输精确读取指定字节数。不超过 <see cref="DirectFrameReadThreshold"/> 的帧原地填充，因此小帧不会
    /// 经过第二个缓冲区复制。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">Deadline applied once the frame has started arriving. <para>帧开始到达后适用的期限。</para></param>
    /// <param name="allowIdle">Wait for the next frame without a deadline; a frame that stopped mid-way still times out.
    /// <para>等待下一帧时不设期限；中途停止的帧仍会超时。</para></param>
    /// <param name="cancellationToken">Stops the read when the connection shuts down. <para>连接关停时停止读取。</para></param>
    /// <returns>A task resolving to the frame bytes. <para>结果为帧字节的任务。</para></returns>
    /// <exception cref="TimeoutException">A partially received frame made no progress within
    /// <paramref name="timeoutMs"/>. <para>已部分接收的帧在 <paramref name="timeoutMs"/> 内没有进展。</para></exception>
    /// <exception cref="ObjectDisposedException">The connection was disposed mid-read.
    /// <para>读取过程中连接被释放。</para></exception>
    private async ValueTask<byte[]> ReadExactAsync(int length, int timeoutMs, bool allowIdle = false,
        CancellationToken cancellationToken = default)
    {
        if (length <= DirectFrameReadThreshold)
        {
            var frame = new byte[length];
            await ReadExactIntoAsync(frame, length, timeoutMs, allowIdle, cancellationToken).ConfigureAwait(false);
            return frame;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactIntoAsync(buffer, length, timeoutMs, allowIdle, cancellationToken).ConfigureAwait(false);
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
    /// Fills the leading <paramref name="length"/> bytes of a buffer from the transport, keeping the idle-wait rules
    /// of <see cref="ReadExactAsync"/>.
    /// <para>从传输填充缓冲区的前 <paramref name="length"/> 字节，并保持 <see cref="ReadExactAsync"/> 的空闲等待规则。</para>
    /// </summary>
    /// <param name="buffer">Destination buffer; only its first <paramref name="length"/> bytes are written.
    /// <para>目标缓冲区；只写入前 <paramref name="length"/> 字节。</para></param>
    /// <param name="length">Number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">Deadline applied once a frame has started arriving. <para>帧已开始到达后适用的期限。</para></param>
    /// <param name="allowIdle">Wait for the next frame without a deadline. The first byte of a frame must wait
    /// indefinitely because an idle link is healthy and a TLS record layer cannot survive an interrupted read; a frame
    /// that stopped mid-way keeps the deadline, and its expiry faults the transport instead of being retried.
    /// <para>等待下一帧时不设期限。一帧的首字节必须无限等待，因为空闲链路本身健康、而 TLS 记录层无法承受被打断的读取；
    /// 中途停止的帧保留期限，期限到达时让传输失效而不是重试。</para></param>
    /// <param name="cancellationToken">Stops the read when the connection shuts down. <para>连接关停时停止读取。</para></param>
    /// <exception cref="TimeoutException">A partially received frame made no progress within
    /// <paramref name="timeoutMs"/>. <para>已部分接收的帧在 <paramref name="timeoutMs"/> 内没有进展。</para></exception>
    /// <exception cref="ObjectDisposedException">The connection was disposed mid-read.
    /// <para>读取过程中连接被释放。</para></exception>
    private async ValueTask ReadExactIntoAsync(byte[] buffer, int length, int timeoutMs, bool allowIdle,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < length)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            var deadlineMs = allowIdle && totalRead == 0 ? Timeout.Infinite : timeoutMs;
            int n;
            try
            {
                n = await Transport.ReadAsync(buffer, totalRead, length - totalRead, deadlineMs, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                InvalidateFailedTransport(ex);
                throw;
            }

            if (n == 0)
            {
                if (Transport is Transports.Tcp.AdbTcpTransport or Transports.Tcp.AdbTlsTransport ||
                    !Transport.IsConnected)
                {
                    throw new EndOfStreamException("ADB transport closed by the peer.");
                }

                if (allowIdle && totalRead == 0 && !Volatile.Read(ref _disposed))
                {
                    continue;
                }

                throw new TimeoutException("ADB read timeout");
            }

            totalRead += n;
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
            ResetPeerReady();
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