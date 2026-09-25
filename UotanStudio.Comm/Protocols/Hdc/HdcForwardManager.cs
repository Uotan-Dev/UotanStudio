using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>Owns native daemon TCP mappings for one authenticated connection.<para>为一条已认证连接持有原生守护进程的 TCP 映射。</para></summary>
/// <param name="connection">The authenticated native HDC connection that carries every mapping's control channel.<para>承载每个映射控制通道的已认证原生 HDC 连接。</para></param>
public sealed class HdcForwardManager(HdcConnection connection) : IDisposable, UotanStudio.Comm.Contracts.IHdcForwardManager
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(bool Reverse, string Listen), Mapping> _mappings = [];
    private bool _disposed;

    /// <summary>Creates a loopback TCP mapping, awaiting the upstream endpoint check.<para>创建回环 TCP 映射，并等待上游端点校验。</para></summary>
    /// <param name="localPort">Host loopback port to listen on (1..65535).<para>主机回环监听端口（1..65535）。</para></param>
    /// <param name="remotePort">Remote device TCP port to map to (1..65535).<para>要映射到的远端设备 TCP 端口（1..65535）。</para></param>
    /// <param name="reverse"><see langword="true"/> to set up a reverse (device-initiated) mapping; <see langword="false"/> for host-initiated forwarding.<para><see langword="true"/> 建立反向（设备发起）映射；<see langword="false"/> 为主机发起转发。</para></param>
    /// <param name="cancellationToken">Cancels the synchronous endpoint handshake before the mapping is registered.<para>在映射登记前取消同步的端点握手。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localPort"/> is outside 1..65535.<para><paramref name="localPort"/> 不在 1..65535 范围内。</para></exception>
    /// <exception cref="System.IO.IOException">The listen key already has a different open mapping.<para>该监听键已存在另一个不同的打开映射。</para></exception>
    /// <exception cref="System.ObjectDisposedException">This manager has been disposed.<para>此管理器已被释放。</para></exception>
    public void Add(int localPort, int remotePort, bool reverse = false, CancellationToken cancellationToken = default)
        => Add(localPort, $"tcp:{remotePort}", reverse, cancellationToken);

    /// <summary>Maps a host TCP port to an upstream TCP, Unix socket, device, JDWP or Ark endpoint.<para>将主机 TCP 端口映射到上游 TCP、Unix 套接字、设备、JDWP 或 Ark 端点。</para></summary>
    /// <param name="localPort">Host loopback port to listen on (1..65535).<para>主机回环监听端口（1..65535）。</para></param>
    /// <param name="remoteEndpoint">Remote endpoint in <c>schema:content</c> form validated against the supported schemas.<para>采用 <c>schema:content</c> 形式、并按受支持模式校验的远端端点。</para></param>
    /// <param name="reverse"><see langword="true"/> to set up a reverse (device-initiated) mapping; <see langword="false"/> for host-initiated forwarding.<para><see langword="true"/> 建立反向（设备发起）映射；<see langword="false"/> 为主机发起转发。</para></param>
    /// <param name="cancellationToken">Cancels the synchronous endpoint handshake before the mapping is registered.<para>在映射登记前取消同步的端点握手。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localPort"/> is outside 1..65535.<para><paramref name="localPort"/> 不在 1..65535 范围内。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="remoteEndpoint"/> is missing or not a valid supported endpoint.<para><paramref name="remoteEndpoint"/> 缺失或不是有效的受支持端点。</para></exception>
    /// <exception cref="System.IO.IOException">The listen key already has a different open mapping.<para>该监听键已存在另一个不同的打开映射。</para></exception>
    /// <exception cref="System.ObjectDisposedException">This manager has been disposed.<para>此管理器已被释放。</para></exception>
    public void Add(int localPort, string remoteEndpoint, bool reverse = false, CancellationToken cancellationToken = default)
    {
        if (localPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
        ValidateRemoteEndpoint(remoteEndpoint, reverse);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (reverse, reverse ? remoteEndpoint : $"tcp:{localPort}");
            if (_mappings.TryGetValue(key, out var existing))
            {
                if (existing.LocalPort == localPort && existing.RemoteEndpoint == remoteEndpoint && !existing.IsClosed) return;
                if (!existing.IsClosed) throw new IOException("HDC listen port already has a different mapping.");
                existing.Dispose(); _mappings.Remove(key);
            }
            var mapping = new Mapping(connection, localPort, remoteEndpoint, reverse);
            try { mapping.Start(cancellationToken); _mappings.Add(key, mapping); }
            catch { mapping.Dispose(); throw; }
        }
    }

    /// <summary>Lists mappings owned by this connection, excluding closed tasks.<para>列出此连接持有的映射，排除已关闭的任务。</para></summary>
    /// <returns>Newline-joined <c>fport</c>/<c>rport tcp:PORT endpoint</c> lines, empty when no mapping is open.<para>以换行拼接的 <c>fport</c>/<c>rport tcp:PORT endpoint</c> 行；无打开映射时为空串。</para></returns>
    public string List()
    {
        lock (_gate) return string.Join('\n', _mappings.Values.Where(m => !m.IsClosed)
            .Select(m => $"{(m.Reverse ? "rport" : "fport")} tcp:{m.LocalPort} {m.RemoteEndpoint}"));
    }

    /// <summary>Removes a mapping and all its sockets by the listening port.<para>按监听端口移除映射及其全部套接字。</para></summary>
    /// <param name="listenPort">Listening port identifying the mapping to remove.<para>标识待移除映射的监听端口。</para></param>
    /// <param name="reverse"><see langword="true"/> for a reverse mapping, <see langword="false"/> for forward.<para><see langword="true"/> 为反向映射，<see langword="false"/> 为正向。</para></param>
    /// <returns><see langword="true"/> when a mapping with that listen key existed; otherwise <see langword="false"/>.<para>存在该监听键的映射时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</para></returns>
    public bool Remove(int listenPort, bool reverse = false) => Remove($"tcp:{listenPort}", reverse);

    /// <summary>Removes a mapping by its exact listening endpoint.<para>按精确监听端点移除映射。</para></summary>
    /// <param name="listenEndpoint">Exact listening endpoint key, e.g. <c>tcp:5555</c>.<para>精确监听端点键，例如 <c>tcp:5555</c>。</para></param>
    /// <param name="reverse"><see langword="true"/> for a reverse mapping, <see langword="false"/> for forward.<para><see langword="true"/> 为反向映射，<see langword="false"/> 为正向。</para></param>
    /// <returns><see langword="true"/> when a mapping with that listen key existed; otherwise <see langword="false"/>.<para>存在该监听键的映射时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</para></returns>
    public bool Remove(string listenEndpoint, bool reverse = false)
    {
        lock (_gate)
        {
            if (!_mappings.Remove((reverse, listenEndpoint), out var mapping)) return false;
            mapping.Dispose(); return true;
        }
    }

    /// <summary>Disposes every owned mapping and its sockets, then blocks further operations.<para>释放所有持有的映射及其套接字，并阻止后续操作。</para></summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var mapping in _mappings.Values) mapping.Dispose();
            _mappings.Clear();
        }
    }

    internal static void ValidateRemoteEndpoint(string endpoint, bool reverse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (endpoint.Length > 1024 || endpoint.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\'' or '"'))
            throw new ArgumentException("Invalid HDC endpoint.", nameof(endpoint));
        var split = endpoint.IndexOf(':');
        if (split <= 0 || split == endpoint.Length - 1) throw new ArgumentException("Expected schema:content.", nameof(endpoint));
        var schema = endpoint[..split];
        var content = endpoint[(split + 1)..];
        switch (schema)
        {
            case "tcp":
                if (!int.TryParse(content, out var port) || port is < 1 or > 65535) throw new ArgumentException("Invalid TCP port.", nameof(endpoint));
                break;
            case "jdwp":
                if (reverse || !int.TryParse(content, out var pid) || pid <= 0) throw new ArgumentException("JDWP requires a remote process ID and a forward mapping.", nameof(endpoint));
                break;
            case "ark":
                var parts = content.Split('@');
                if (reverse || parts.Length != 3 || !int.TryParse(parts[0], out var process) || process <= 0 ||
                    !int.TryParse(parts[1], out var thread) || thread <= 0 || parts[2] is not ("Debugger" or "ArkCompiler"))
                    throw new ArgumentException("Invalid Ark debug endpoint.", nameof(endpoint));
                break;
            case "localabstract":
            case "localfilesystem":
            case "localreserved":
            case "dev":
                break;
            default: throw new NotSupportedException("Unsupported HDC endpoint schema: " + schema);
        }
    }

    private sealed class Mapping(HdcConnection connection, int localPort, string remoteEndpoint, bool reverse) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<uint, Context> _contexts = new();
        private HdcStream? _channel;
        private TcpListener? _listener;
        private int _nextId = 1;
        private int _disposed;
        internal int LocalPort => localPort;
        internal string RemoteEndpoint => remoteEndpoint;
        internal bool Reverse => reverse;
        internal bool IsClosed => _stop.IsCancellationRequested || _channel?.IsClosed == true;

        internal void Start(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (reverse)
            {
                _channel = connection.OpenChannel(HdcCommand.ForwardInit,
                    Encoding.UTF8.GetBytes($"{remoteEndpoint} tcp:{localPort}"));
                var check = HdcSyncClient.Expect(_channel, HdcCommand.ForwardCheck, ct);
                var id = ValidateEndpoint(check.Payload ?? []);
                Send(HdcCommand.ForwardCheckResult, id, [1]);
                HdcSyncClient.Expect(_channel, HdcCommand.Echo, ct);
            }
            else
            {
                _listener = new TcpListener(IPAddress.Loopback, localPort);
                _listener.Start(32);
                _channel = connection.OpenChannel(HdcCommand.ForwardCheck, Endpoint(1, remoteEndpoint));
                var result = HdcSyncClient.Expect(_channel, HdcCommand.ForwardCheckResult, ct).Payload ?? [];
                if (result.Length != 5 || BinaryPrimitives.ReadUInt32BigEndian(result) != 1)
                    throw new InvalidDataException("Invalid HDC forwarding check response.");
                _ = AcceptLoop();
            }
            _ = Task.Run(Dispatch);
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var socket = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    var id = checked((uint)Interlocked.Increment(ref _nextId));
                    if (!AddContext(id, socket)) continue;
                    _channel!.SendCommand(HdcCommand.ForwardActiveSlave, Endpoint(id, remoteEndpoint));
                    _ = ExpireUnactivated(id);
                }
            }
            catch (Exception) { Dispose(); }
        }

        private async Task ExpireUnactivated(uint id)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token).ConfigureAwait(false);
                if (_contexts.TryGetValue(id, out var context) && Volatile.Read(ref context.Started) == 0) Free(id, true);
            }
            catch (OperationCanceledException) { }
        }

        private void Dispatch()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var message = _channel!.ReadMessage(250);
                    if (message is null) { if (_channel.IsClosed) break; continue; }
                    if (message.Command is HdcCommand.Echo or HdcCommand.EchoRaw or HdcCommand.ForwardSuccess) continue;
                    var payload = message.Payload ?? [];
                    if (payload.Length < 4) throw new InvalidDataException("Truncated HDC forwarding context.");
                    var id = BinaryPrimitives.ReadUInt32BigEndian(payload);
                    switch (message.Command)
                    {
                        case HdcCommand.ForwardActiveSlave when reverse:
                            ValidateEndpoint(payload);
                            if (_contexts.Count >= 64) { Send(HdcCommand.ForwardFreeContext, id, []); break; }
                            var socket = new TcpClient(AddressFamily.InterNetwork);
                            if (AddContext(id, socket)) _ = ConnectReverse(id, socket);
                            break;
                        case HdcCommand.ForwardActiveMaster when !reverse:
                            if (payload.Length != 4) throw new InvalidDataException("Invalid activation payload.");
                            StartPumps(id);
                            break;
                        case HdcCommand.ForwardData:
                            if (_contexts.TryGetValue(id, out var context) && !context.Incoming.Writer.TryWrite(payload[4..])) Free(id, true);
                            break;
                        case HdcCommand.ForwardFreeContext:
                            Free(id, false);
                            break;
                        default: throw new InvalidDataException($"Unexpected forwarding command: {message.Command}.");
                    }
                }
            }
            catch (Exception) { }
            finally { Dispose(); }
        }

        private bool AddContext(uint id, TcpClient socket)
        {
            socket.NoDelay = true;
            if (_stop.IsCancellationRequested || _contexts.Count >= 64 || !_contexts.TryAdd(id, new Context(socket)))
            { socket.Dispose(); return false; }
            if (_stop.IsCancellationRequested) { Free(id, false); return false; }
            return true;
        }

        private async Task ConnectReverse(uint id, TcpClient socket)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(IPAddress.Loopback, localPort, timeout.Token).ConfigureAwait(false);
                Send(HdcCommand.ForwardActiveMaster, id, []);
                StartPumps(id);
            }
            catch (Exception) { Free(id, true); }
        }

        private void StartPumps(uint id)
        {
            if (!_contexts.TryGetValue(id, out var context) || Interlocked.Exchange(ref context.Started, 1) != 0) return;
            _ = PumpRead(id, context);
            _ = PumpWrite(id, context);
        }

        private async Task PumpRead(uint id, Context context)
        {
            try
            {
                var buffer = new byte[32 * 1024];
                var stream = context.Socket.GetStream();
                while (await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false) is var count && count > 0)
                    Send(HdcCommand.ForwardData, id, buffer.AsSpan(0, count));
            }
            catch (Exception) { }
            finally { Free(id, true); }
        }

        private async Task PumpWrite(uint id, Context context)
        {
            try
            {
                var stream = context.Socket.GetStream();
                await foreach (var data in context.Incoming.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                    await stream.WriteAsync(data, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception) { }
            finally { Free(id, true); }
        }

        private void Free(uint id, bool notify)
        {
            if (!_contexts.TryRemove(id, out var context)) return;
            context.Incoming.Writer.TryComplete(); context.Socket.Dispose();
            if (notify && !IsClosed) try { Send(HdcCommand.ForwardFreeContext, id, []); } catch (IOException) { }
        }

        private uint ValidateEndpoint(byte[] payload)
        {
            if (payload.Length < 13 || payload[^1] != 0 || payload.AsSpan(4, 8).ContainsAnyExcept((byte)0)
                || Encoding.UTF8.GetString(payload.AsSpan(12, payload.Length - 13)) != $"tcp:{localPort}")
                throw new InvalidDataException("HDC reverse requested a different host endpoint.");
            return BinaryPrimitives.ReadUInt32BigEndian(payload);
        }

        private void Send(HdcCommand command, uint id, ReadOnlySpan<byte> content)
        {
            var payload = new byte[4 + content.Length];
            BinaryPrimitives.WriteUInt32BigEndian(payload, id); content.CopyTo(payload.AsSpan(4));
            _channel!.SendCommand(command, payload);
        }

        private static byte[] Endpoint(uint id, string endpoint)
        {
            var result = new byte[13 + Encoding.UTF8.GetByteCount(endpoint)];
            BinaryPrimitives.WriteUInt32BigEndian(result, id);
            Encoding.UTF8.GetBytes(endpoint, result.AsSpan(12)); return result;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel(); _listener?.Stop();
            foreach (var id in _contexts.Keys) Free(id, false);
            _channel?.Dispose();
        }

        private sealed class Context(TcpClient socket)
        {
            internal readonly TcpClient Socket = socket;
            internal readonly Channel<byte[]> Incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            internal int Started;
        }
    }
}