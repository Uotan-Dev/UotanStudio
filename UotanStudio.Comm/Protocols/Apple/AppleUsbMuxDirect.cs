using System.Buffers.Binary;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Apple;

/// <summary>
/// Speaks the Apple USB multiplexer protocol directly over the device's vendor-specific USB interface
/// (class 0xFF, subclass 0xFE, protocol 2), bypassing the host usbmuxd daemon. One instance owns the
/// USB session: it performs the version/SETUP handshake, runs the receive pump that reassembles mux
/// messages across bulk transfers, and multiplexes one mini-TCP connection per device port.
/// <para>直接在设备厂商自定义 USB 接口（class 0xFF、subclass 0xFE、protocol 2）上讲 Apple USB 复用协议，
/// 绕过主机 usbmuxd 守护进程。每个实例独占 USB 会话：执行版本/SETUP 握手，运行重组跨 bulk 传输的
/// mux 消息的接收泵，并按设备端口复用一路迷你 TCP 连接。</para>
/// </summary>
internal sealed class AppleUsbMuxDirect : IDisposable
{
    private const ushort ProtocolVersion = 2;
    private const uint MuxMagic = 0xFEEDFACE;
    private const uint MuxProtoVersion = 0;
    private const uint MuxProtoSetup = 2;
    private const uint MuxProtoTcp = 6;
    private const int MuxMaxMessageBytes = 32 * 1024 * 1024;
    private const int UsbTransferTimeoutMs = 1_000;
    private const int WindowBytes = 131_072;
    private const int MaxBulkPayload = 0x4000;

    private const byte TcpFlagFin = 0x01;
    private const byte TcpFlagSyn = 0x02;
    private const byte TcpFlagRst = 0x04;
    private const byte TcpFlagAck = 0x10;

    private readonly IUsbDeviceSession _usb;
    private readonly Lock _sendLock = new();
    private readonly Lock _stateLock = new();
    private readonly Dictionary<ushort, MuxTcpConnection> _connections = [];
    private readonly PacketBuffer _incoming = new();
    private readonly Thread _receiveThread;
    private readonly CancellationTokenSource _pumpCancellation = new();
    private ushort _txSeq;
    private ushort _rxSeq;
    private bool _handshaken;

    private AppleUsbMuxDirect(IUsbDeviceSession usb)
    {
        _usb = usb;
        _receiveThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "apple-usb-mux-rx",
        };
    }

    /// <summary>
    /// Attaches to an opened Apple USB interface and performs the mux handshake.
    /// <para>附加到已打开的 Apple USB 接口并执行复用握手。</para>
    /// </summary>
    /// <param name="usb">The opened USB device session claiming the multiplexer interface. <para>已声明复用接口的 USB 设备会话。</para></param>
    /// <param name="cancellationToken">Cancels the handshake. <para>取消握手。</para></param>
    /// <returns>The attached multiplexer. <para>已附加的复用器。</para></returns>
    /// <exception cref="IOException">The handshake failed or the device speaks an unsupported version. <para>握手失败或设备使用不受支持的版本时抛出。</para></exception>
    public static AppleUsbMuxDirect Attach(IUsbDeviceSession usb, CancellationToken cancellationToken)
    {
        var mux = new AppleUsbMuxDirect(usb);
        try
        {
            mux._receiveThread.Start();
            mux.SendMux(MuxProtoVersion, CreateVersionPayload(), cancellationToken);
            var version = mux.WaitForVersion(cancellationToken);
            if (version.Major < ProtocolVersion)
            {
                throw new IOException($"The device mux speaks legacy protocol {version.Major}; direct USB requires version {ProtocolVersion}.");
            }

            mux.SendMux(MuxProtoSetup, [0x07], cancellationToken);
            lock (mux._stateLock)
            {
                mux._handshaken = true;
            }

            return mux;
        }
        catch
        {
            mux.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens one mini-TCP connection to a device port over the multiplexer.
    /// <para>经复用器打开一条到设备端口的迷你 TCP 连接。</para>
    /// </summary>
    /// <param name="port">The device TCP port. <para>设备 TCP 端口。</para></param>
    /// <param name="cancellationToken">Cancels the connect. <para>取消连接。</para></param>
    /// <returns>The connection byte stream. <para>连接字节流。</para></returns>
    /// <exception cref="IOException">The handshake was refused or the connection timed out. <para>握手被拒或连接超时时抛出。</para></exception>
    public Stream Connect(int port, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_pumpCancellation.IsCancellationRequested, this);
        if (port is <= 0 or > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "The device port must fit in 16 bits.");
        }

        ushort sport;
        var connection = new MuxTcpConnection(this, (ushort)port, WindowBytes);
        try
        {
            while (true)
            {
                sport = (ushort)Random.Shared.Next(0x8000, 0xFFFF);
                lock (_stateLock)
                {
                    if (!_connections.ContainsKey(sport))
                    {
                        connection.SourcePort = sport;
                        _connections.Add(sport, connection);
                        break;
                    }
                }
            }

            connection.SendFlags(TcpFlagSyn, initial: true, ReadOnlySpan<byte>.Empty, cancellationToken);
            if (!connection.WaitForEstablished(TimeSpan.FromSeconds(5), cancellationToken))
            {
                throw new IOException($"The device refused or ignored the connection to port {port}.");
            }

            return new MuxTcpStream(connection);
        }
        catch
        {
            RemoveConnection(connection.SourcePort);
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _pumpCancellation.Cancel();
        List<MuxTcpConnection> connections;
        lock (_stateLock)
        {
            connections = [.. _connections.Values];
            _connections.Clear();
        }

        foreach (var connection in connections)
        {
            connection.Abort();
        }

        _pumpCancellation.Dispose();
    }

    private static byte[] CreateVersionPayload()
    {
        var payload = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(payload, ProtocolVersion);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 0);
        return payload;
    }

    private (ushort Major, ushort Minor) WaitForVersion(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_incoming.TryDequeueMessage(out var protocol, out _, out var payload))
            {
                if (protocol != MuxProtoVersion || payload.Length < 4)
                {
                    throw new IOException("The device mux answered with an unexpected handshake message.");
                }

                var major = BinaryPrimitives.ReadUInt32BigEndian(payload);
                var minor = payload.Length >= 8 ? BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4)) : 0u;
                if (major is 0 or > ushort.MaxValue || minor > ushort.MaxValue)
                {
                    throw new IOException("The device mux reported an invalid version.");
                }

                return ((ushort)major, (ushort)minor);
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("The device mux version handshake timed out.");
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[MaxBulkPayload];
        while (!_pumpCancellation.IsCancellationRequested)
        {
            try
            {
                var result = _usb.ReadPacketAsync(buffer, 0, buffer.Length, UsbTransferTimeoutMs,
                    _pumpCancellation.Token).GetAwaiter().GetResult();
                if (result.Count > 0)
                {
                    _incoming.Enqueue(buffer.AsSpan(0, result.Count).ToArray());
                    DispatchMessages();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                if (!_pumpCancellation.IsCancellationRequested)
                {
                    UsbTrace.LogError($"apple-usb-mux receive pump failed: {exception.Message}");
                    AbortAll(new IOException("The Apple USB multiplexer receive pump failed.", exception));
                    return;
                }
            }
        }
    }

    private void DispatchMessages()
    {
        bool handshaken;
        lock (_stateLock)
        {
            handshaken = _handshaken;
        }

        while (handshaken && _incoming.TryDequeueMessage(out var protocol, out var txSeq, out var payload))
        {
            lock (_sendLock)
            {
                _rxSeq = txSeq;
            }

            if (protocol == MuxProtoTcp)
            {
                DispatchTcp(payload);
            }
        }
    }

    private void DispatchTcp(byte[] payload)
    {
        if (payload.Length < 20)
        {
            return;
        }

        var sport = BinaryPrimitives.ReadUInt16BigEndian(payload);
        var dport = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2));
        var seq = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
        var ack = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8));
        var flags = payload[13];
        var window = (uint)BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(14)) << 8;
        var data = payload.Length > 20 ? payload[20..] : ReadOnlySpan<byte>.Empty;

        MuxTcpConnection? connection;
        lock (_stateLock)
        {
            _connections.TryGetValue(dport, out connection);
        }

        if (connection is null)
        {
            if ((flags & TcpFlagSyn) != 0)
            {
                SendTcp(0, sport, 0, seq + 1, TcpFlagRst, []);
            }

            return;
        }

        connection.OnPacket(flags, seq, ack, window, data);
    }

    private void SendMux(uint protocol, byte[] payload, CancellationToken cancellationToken)
    {
        ushort txSeq;
        ushort rxSeq;
        lock (_sendLock)
        {
            txSeq = _txSeq++;
            rxSeq = _rxSeq;
        }

        var message = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(message, protocol);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), (uint)message.Length);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), MuxMagic);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(12), txSeq);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(14), rxSeq);
        payload.CopyTo(message, 16);
        WriteBulk(message, cancellationToken);
    }

    private void SendTcp(ushort sport, ushort dport, uint seq, uint ack, byte flags, ReadOnlySpan<byte> data) =>
        SendTcp(sport, dport, seq, ack, flags, data, WindowBytes, CancellationToken.None);

    private void SendTcp(ushort sport, ushort dport, uint seq, uint ack, byte flags, ReadOnlySpan<byte> data,
        int window, CancellationToken cancellationToken)
    {
        var payload = new byte[20 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, sport);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), dport);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), ack);
        payload[12] = 0x50;
        payload[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(14), (ushort)(window >> 8));
        data.CopyTo(payload.AsSpan(20));

        ushort txSeq;
        ushort rxSeq;
        lock (_sendLock)
        {
            txSeq = _txSeq++;
            rxSeq = _rxSeq;
        }

        var message = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(message, MuxProtoTcp);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), (uint)message.Length);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), MuxMagic);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(12), txSeq);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(14), rxSeq);
        payload.CopyTo(message, 16);
        WriteBulk(message, cancellationToken);
    }

    private void WriteBulk(byte[] message, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < message.Length)
        {
            var chunk = Math.Min(message.Length - written, MaxBulkPayload);
            _usb.WriteAsync(message, written, chunk, UsbTransferTimeoutMs, cancellationToken)
                .GetAwaiter().GetResult();
            written += chunk;
        }

        if (message.Length % 512 == 0)
        {
            _usb.WriteZlpAsync(UsbTransferTimeoutMs, cancellationToken).GetAwaiter().GetResult();
        }
    }

    private void RemoveConnection(ushort sport)
    {
        lock (_stateLock)
        {
            _connections.Remove(sport);
        }
    }

    private void AbortAll(Exception reason)
    {
        List<MuxTcpConnection> connections;
        lock (_stateLock)
        {
            connections = [.. _connections.Values];
            _connections.Clear();
        }

        foreach (var connection in connections)
        {
            connection.Abort(reason);
        }
    }

    /// <summary>
    /// Byte stream over one mini-TCP connection.
    /// <para>承载一条迷你 TCP 连接的字节流。</para>
    /// </summary>
    private sealed class MuxTcpStream(MuxTcpConnection connection) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            connection.Read(buffer.AsMemory(offset, count), Timeout.Infinite);

        public override void Write(byte[] buffer, int offset, int count) =>
            connection.Write(buffer.AsSpan(offset, count), Timeout.Infinite);

        public override void Close()
        {
            connection.Close();
            base.Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                connection.Dispose();
            }

            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// One mini-TCP connection state machine.
    /// <para>一条迷你 TCP 连接的状态机。</para>
    /// </summary>
    internal sealed class MuxTcpConnection : IDisposable
    {
        private const int StateClosed = 0;
        private const int StateSynSent = 1;
        private const int StateEstablished = 2;

        private readonly AppleUsbMuxDirect _mux;
        private readonly Lock _lock = new();
        private readonly Queue<byte> _received = new();
        private readonly ManualResetEventSlim _dataAvailable = new(false);
        private readonly ManualResetEventSlim _stateChanged = new(false);
        private readonly ManualResetEventSlim _windowOpened = new(true);
        private uint _txSeq;
        private uint _ackedSeq;
        private uint _txAck;
        private int _txWindow = WindowBytes;
        private int _unacked;
        private int _state = StateClosed;
        private bool _peerClosed;
        private Exception? _failure;

        public MuxTcpConnection(AppleUsbMuxDirect mux, ushort destinationPort, int window)
        {
            _mux = mux;
            DestinationPort = destinationPort;
            _txSeq = (uint)Random.Shared.Next(1, int.MaxValue);
            _txWindow = window;
        }

        public ushort SourcePort { get; set; }

        public ushort DestinationPort { get; }

        public void SendFlags(byte flags, bool initial, ReadOnlySpan<byte> data, CancellationToken cancellationToken)
        {
            if (initial)
            {
                lock (_lock)
                {
                    _state = StateSynSent;
                    _stateChanged.Set();
                }
            }

            _mux.SendTcp(SourcePort, DestinationPort, _txSeq, _txAck, flags, data, _txWindow, cancellationToken);
        }

        public bool WaitForEstablished(TimeSpan timeout, CancellationToken cancellationToken) =>
            _stateChanged.Wait(timeout, cancellationToken) && Volatile.Read(ref _state) == StateEstablished;

        public void OnPacket(byte flags, uint seq, uint ack, uint window, ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                if ((flags & TcpFlagRst) != 0)
                {
                    _failure ??= new IOException("The device reset the multiplexed connection.");
                    _state = StateClosed;
                    _stateChanged.Set();
                    _dataAvailable.Set();
                    _windowOpened.Set();
                    return;
                }

                if ((flags & TcpFlagAck) != 0)
                {
                    var advance = unchecked((int)(ack - _ackedSeq));
                    if (advance > 0)
                    {
                        _unacked = Math.Max(0, _unacked - advance);
                        _ackedSeq = ack;
                    }

                    _txWindow = window > 0 ? (int)Math.Min(window, (uint)int.MaxValue) : _txWindow;
                    if (_unacked < _txWindow / 2)
                    {
                        _windowOpened.Set();
                    }
                }

                if ((flags & TcpFlagSyn) != 0 && _state == StateSynSent)
                {
                    _ackedSeq = seq;
                    _txAck = seq + 1;
                    _state = StateEstablished;
                    _stateChanged.Set();
                    _mux.SendTcp(SourcePort, DestinationPort, _txSeq, _txAck, TcpFlagAck, []);
                    return;
                }

                if (data.Length > 0)
                {
                    foreach (var octet in data)
                    {
                        _received.Enqueue(octet);
                    }

                    _ackedSeq = seq + (uint)data.Length;
                    _txAck = _ackedSeq;
                    _dataAvailable.Set();
                    _mux.SendTcp(SourcePort, DestinationPort, _txSeq, _txAck, TcpFlagAck, []);
                }

                if ((flags & TcpFlagFin) != 0)
                {
                    _peerClosed = true;
                    _dataAvailable.Set();
                }
            }
        }

        public int Read(Memory<byte> destination, int timeoutMs)
        {
            while (true)
            {
                lock (_lock)
                {
                    if (_failure is { } failure)
                    {
                        throw failure;
                    }

                    if (_received.Count > 0)
                    {
                        var copied = 0;
                        while (copied < destination.Length && _received.Count > 0)
                        {
                            destination.Span[copied++] = _received.Dequeue();
                        }

                        return copied;
                    }

                    if (_peerClosed || _state == StateClosed)
                    {
                        return 0;
                    }
                }

                if (!_dataAvailable.Wait(timeoutMs, CancellationToken.None))
                {
                    throw new TimeoutException("The multiplexed connection read timed out.");
                }

                lock (_lock)
                {
                    if (_received.Count == 0 && !_peerClosed && _state != StateClosed && _failure is null)
                    {
                        _dataAvailable.Reset();
                    }
                }
            }
        }

        public void Write(ReadOnlySpan<byte> source, int timeoutMs)
        {
            var offset = 0;
            while (offset < source.Length)
            {
                if (!_windowOpened.Wait(timeoutMs, CancellationToken.None))
                {
                    throw new TimeoutException("The multiplexed connection write timed out.");
                }

                var chunk = 0;
                lock (_lock)
                {
                    if (_failure is { } failure)
                    {
                        throw failure;
                    }

                    if (_state != StateEstablished)
                    {
                        throw new IOException("The multiplexed connection is not established.");
                    }

                    chunk = Math.Min(source.Length - offset, Math.Min(_txWindow - _unacked, MaxBulkPayload - 20));
                    if (chunk <= 0)
                    {
                        _windowOpened.Reset();
                        continue;
                    }

                    var data = source.Slice(offset, chunk);
                    var payload = data.ToArray();
                    _mux.SendTcp(SourcePort, DestinationPort, _txSeq, _txAck, TcpFlagAck | 0x08, payload,
                        _txWindow, CancellationToken.None);
                    _txSeq += (uint)chunk;
                    _unacked += chunk;
                    if (_unacked >= _txWindow)
                    {
                        _windowOpened.Reset();
                    }
                }

                offset += chunk;
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                if (_state == StateEstablished)
                {
                    _mux.SendTcp(SourcePort, DestinationPort, _txSeq, _txAck, TcpFlagAck | TcpFlagFin, []);
                    _state = StateClosed;
                    _stateChanged.Set();
                }
            }

            _mux.RemoveConnection(SourcePort);
        }

        public void Abort(Exception? reason = null)
        {
            lock (_lock)
            {
                _failure ??= reason;
                _state = StateClosed;
                _stateChanged.Set();
                _dataAvailable.Set();
                _windowOpened.Set();
            }
        }

        public void Dispose()
        {
            _dataAvailable.Dispose();
            _stateChanged.Dispose();
            _windowOpened.Dispose();
        }
    }

    /// <summary>
    /// Reassembles mux messages that arrive split across bulk transfers.
    /// <para>重组跨 bulk 传输拆分的复用消息。</para>
    /// </summary>
    internal sealed class PacketBuffer
    {
        private readonly Queue<byte> _bytes = new();

        public void Enqueue(byte[] chunk)
        {
            lock (_bytes)
            {
                foreach (var octet in chunk)
                {
                    _bytes.Enqueue(octet);
                }
            }
        }

        public bool TryDequeueMessage(out uint protocol, out ushort txSeq, out byte[] payload)
        {
            lock (_bytes)
            {
                protocol = 0;
                txSeq = 0;
                payload = [];
                if (_bytes.Count < 16)
                {
                    return false;
                }

                var snapshot = _bytes.ToArray();
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(snapshot.AsSpan(4));
                if (length is < 16 or > MuxMaxMessageBytes || _bytes.Count < length)
                {
                    if (length is < 16 or > MuxMaxMessageBytes)
                    {
                        throw new IOException($"The device mux message length {length} is out of range.");
                    }

                    return false;
                }

                protocol = BinaryPrimitives.ReadUInt32BigEndian(snapshot);
                var magic = BinaryPrimitives.ReadUInt32BigEndian(snapshot.AsSpan(8));
                if (magic != MuxMagic)
                {
                    throw new IOException("The device mux message carries an invalid magic.");
                }

                txSeq = BinaryPrimitives.ReadUInt16BigEndian(snapshot.AsSpan(12));
                payload = snapshot[16..length];
                for (var consumed = 0; consumed < length; consumed++)
                {
                    _bytes.Dequeue();
                }

                return true;
            }
        }
    }
}