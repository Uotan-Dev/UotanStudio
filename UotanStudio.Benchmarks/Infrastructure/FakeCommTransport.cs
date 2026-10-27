using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Benchmarks.Infrastructure;

/// <summary>
/// A scripted <see cref="ICommTransport"/> for protocol tests: every write is recorded and
/// an optional <see cref="OnWrite"/> hook enqueues device responses; reads are served from
/// a FIFO queue (blocking with the given timeout when empty).
/// <para>用于协议测试的脚本化 <see cref="ICommTransport"/>：每次写入都会被记录，可选的
/// <see cref="OnWrite"/> 钩子负责入队设备响应；读取从 FIFO 队列取数（队列为空时按超时阻塞）。</para>
/// </summary>
public class FakeCommTransport : ICommTransport
{
    private readonly object _lock = new();
    private readonly Queue<byte[]> _reads = new();
    private byte[]? _headRemainder;
    private bool _connected = true;

    /// <summary>
    /// Gets all bytes written through this transport, in order.
    /// <para>所有写入字节。</para>
    /// </summary>
    public List<byte[]> Writes { get; } = [];

    /// <summary>
    /// Gets whether the transport reports connected.
    /// <para>是否报告已连接。</para>
    /// </summary>
    public bool IsConnected => _connected;

    /// <summary>
    /// Optional hook invoked on every write; returned byte arrays are enqueued as device
    /// responses. <para>每次写入时调用的可选钩子；返回的字节数组会被入队为设备响应。</para>
    /// </summary>
    public Func<byte[], IEnumerable<byte[]>>? OnWrite { get; set; }

    /// <summary>
    /// When set, writes return 0 (simulating a dead transport).
    /// <para>置位时写入返回 0。</para>
    /// </summary>
    public bool FailWrites { get; set; }

    /// <inheritdoc />
    public void Connect() => _connected = true;

    /// <inheritdoc />
    public void Disconnect()
    {
        lock (_lock)
        {
            _connected = false;
            Monitor.PulseAll(_lock);
        }
    }

    /// <inheritdoc />
    public void Reset() { }

    /// <summary>
    /// Enqueues a device response.
    /// <para>入队一条设备响应。</para>
    /// </summary>
    public void Enqueue(byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            _reads.Enqueue(data);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Enqueues a UTF-8 text device response.
    /// <para>入队一条 UTF-8 文本设备响应。</para>
    /// </summary>
    public void Enqueue(string text) => Enqueue(Encoding.UTF8.GetBytes(text));

    /// <inheritdoc />
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        if (FailWrites)
        {
            return 0;
        }

        var copy = new byte[length];
        Array.Copy(data, offset, copy, 0, length);
        Writes.Add(copy);

        var responses = OnWrite?.Invoke(copy);
        if (responses is not null)
        {
            foreach (var response in responses)
            {
                Enqueue(response);
            }
        }

        return length;
    }

    /// <inheritdoc />
    public async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => await Task.Run(() => Write(data, offset, length, timeoutMs), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        var chunk = TryTake(timeoutMs);
        if (chunk is null)
        {
            return 0;
        }

        var n = Math.Min(length, chunk.Length);
        Array.Copy(chunk, 0, buffer, offset, n);
        if (n < chunk.Length)
        {
            lock (_lock)
            {
                _headRemainder = chunk[n..];
                Monitor.PulseAll(_lock);
            }
        }

        return n;
    }

    /// <inheritdoc />
    public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        var (success, chunk) = await TryTakeAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        if (!success || chunk is null)
        {
            return 0;
        }

        var n = Math.Min(length, chunk.Length);
        Array.Copy(chunk, 0, buffer, offset, n);
        if (n < chunk.Length)
        {
            lock (_lock)
            {
                _headRemainder = chunk[n..];
                Monitor.PulseAll(_lock);
            }
        }

        return n;
    }

    /// <inheritdoc />
    public Stream GetStream() => throw new NotSupportedException();

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _connected = false;
            _reads.Clear();
            _headRemainder = null;
            Monitor.PulseAll(_lock);
        }
    }

    private byte[]? TryTake(int timeoutMs)
    {
        lock (_lock)
        {
            if (_headRemainder is not null)
            {
                var head = _headRemainder;
                _headRemainder = null;
                return head;
            }

            if (_reads.Count > 0)
            {
                return _reads.Dequeue();
            }

            if (!_connected)
            {
                return null;
            }

            if (timeoutMs <= 0)
            {
                return null;
            }

            var deadline = Environment.TickCount64 + timeoutMs;
            while (_reads.Count == 0 && _connected)
            {
                var remaining = (int)Math.Min(deadline - Environment.TickCount64, int.MaxValue);
                if (remaining <= 0)
                {
                    return null;
                }

                Monitor.Wait(_lock, remaining);
            }

            return _reads.Count > 0 ? _reads.Dequeue() : null;
        }
    }

    private async Task<(bool Success, byte[]? Chunk)> TryTakeAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_headRemainder is not null)
                {
                    var head = _headRemainder;
                    _headRemainder = null;
                    return (true, head);
                }

                if (_reads.Count > 0)
                {
                    return (true, _reads.Dequeue());
                }

                if (!_connected)
                {
                    return (false, null);
                }
            }

            if (timeoutMs <= 0)
            {
                return (false, null);
            }

            try
            {
                await Task.Delay(Math.Min(50, timeoutMs), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            timeoutMs -= 50;
        }
    }
}

/// <summary>
/// A minimal ADB device simulator over <see cref="ICommTransport"/>: completes the CNXN
/// handshake (no AUTH), acknowledges OPEN/WRTE and closes reboot streams so tests can assert
/// the exact ADB command sequence (reboot:sideload, sync push to /sideload/package.zip).
/// <para>基于 <see cref="ICommTransport"/> 的最小 ADB 设备模拟器：完成 CNXN 握手（无
/// AUTH），确认 OPEN/WRTE 并关闭 reboot 流，便于测试断言 ADB 命令序列。</para>
/// </summary>
public sealed class FakeAdbDeviceTransport : FakeCommTransport
{
    private readonly List<byte> _recv = new();
    private readonly List<string> _openedServices = [];

    /// <summary>
    /// Gets the services the client opened (e.g. "sync:", "reboot:sideload").
    /// <para>客户端打开的服务列表。</para>
    /// </summary>
    public IReadOnlyList<string> OpenedServices => _openedServices;

    /// <summary>
    /// Gets the raw payloads of every WRTE the client sent (sync packets included).
    /// <para>客户端发送的 WRTE 负载。</para>
    /// </summary>
    public List<byte[]> WrtePayloads { get; } = [];

    public uint PeerMaxPayload { get; init; } = AdbConstants.MaxPayload;

    /// <summary>
    /// Gets or sets the bytes a sync RECV request answers with: the simulator splits them into DATA
    /// packets of <see cref="SyncPullChunkSize"/> bytes and closes the transfer with DONE. Left null,
    /// a RECV request stays unanswered, which is what the hanging-write tests rely on.
    /// <para>获取或设置 sync RECV 请求回应的字节：模拟器按 <see cref="SyncPullChunkSize"/> 切分为
    /// DATA 包，并以 DONE 结束传输。保持为 null 时 RECV 请求不会得到回应，挂死写入的测试正依赖该行为。</para>
    /// </summary>
    public byte[]? SyncPullData { get; init; }

    /// <summary>
    /// Gets or sets the largest DATA payload the simulator emits in one packet, so a pull can be made
    /// to span several chunks. Values below 1 are raised to 1.
    /// <para>获取或设置模拟器单个 DATA 包的最大负载字节数，用于让拉取跨多个分块。小于 1 的取值按 1 处理。</para>
    /// </summary>
    public int SyncPullChunkSize { get; init; } = 64 * 1024;

    public bool AutoAcknowledgeWrites { get; init; } = true;

    public Action<byte[]>? OnWrte { get; init; }

    /// <summary>
    /// Gets or sets how many AUTH token challenges the simulated adbd issues before it accepts the
    /// client, reproducing the AUTH challenge flow the wire protocol defines, where each token is answered with the next host identity
    /// and an exhausted chain ends with the public-key announcement that triggers the device prompt.
    /// Zero keeps the no-auth handshake the other tests rely on.
    /// <para>获取或设置模拟 adbd 在接纳客户端前下发多少个 AUTH 令牌挑战，复现线协议定义的 AUTH 挑战流程：每个令牌由下一个
    /// 主机身份应答，身份链耗尽后以公钥通告结束并触发设备提示。为 0 时保持其他测试依赖的免认证握手。</para>
    /// </summary>
    public int AuthTokenChallenges { get; init; }

    /// <summary>
    /// Gets the AUTH tokens the simulator issued, in order.
    /// <para>模拟器依次下发的 AUTH 令牌。</para>
    /// </summary>
    public List<byte[]> AuthTokens { get; } = [];

    /// <summary>
    /// Gets the AUTH signature payloads the client returned, in order.
    /// <para>客户端依次返回的 AUTH 签名负载。</para>
    /// </summary>
    public List<byte[]> AuthSignatures { get; } = [];

    /// <summary>
    /// Gets the AUTH RSAPUBLICKEY payload the client announced, or <see langword="null"/> when the
    /// client never reached the announcement.
    /// <para>客户端通告的 AUTH RSAPUBLICKEY 负载；客户端未走到通告时为 <see langword="null"/>。</para>
    /// </summary>
    public byte[]? AuthPublicKeyPayload { get; private set; }

    private int _authTokensIssued;

    private int _closeCount;

    public int CloseCount => Volatile.Read(ref _closeCount);

    /// <summary>
    /// Initializes a new ADB device simulator.
    /// <para>初始化 ADB 设备模拟器。</para>
    /// </summary>
    public FakeAdbDeviceTransport()
    {
        OnWrite = ProcessClientMessage;
    }

    private IEnumerable<byte[]> ProcessClientMessage(byte[] data)
    {
        lock (_recv)
        {
            _recv.AddRange(data);
            var responses = new List<byte[]>();
            var offset = 0;
            while (offset + AdbConstants.HeaderSize <= _recv.Count)
            {
                var headerBytes = _recv.Skip(offset).Take(AdbConstants.HeaderSize).ToArray();
                var header = AdbMessaging.ParseHeader(headerBytes);
                var payloadLength = (int)header.PayloadLength;
                if (offset + AdbConstants.HeaderSize + payloadLength > _recv.Count)
                {
                    break;
                }

                var payload = _recv.Skip(offset + AdbConstants.HeaderSize).Take(payloadLength).ToArray();
                offset += AdbConstants.HeaderSize + payloadLength;
                responses.AddRange(HandleMessage(header.Command, header.Arg0, header.Arg1, payload));
            }

            _recv.RemoveRange(0, offset);
            return responses;
        }
    }

    private IEnumerable<byte[]> HandleMessage(AdbCommand command, uint arg0, uint arg1, byte[] payload)
    {
        switch (command)
        {
            case AdbCommand.Cnxn:
                if (_authTokensIssued == 0 && AuthTokenChallenges > 0)
                {
                    yield return IssueAuthToken();
                    break;
                }

                yield return AdbMessaging.Serialize(new AdbMessage(
                    AdbCommand.Cnxn, AdbConstants.ProtocolVersion, PeerMaxPayload,
                    Encoding.UTF8.GetBytes("device::fake\0")));
                break;

            case AdbCommand.Auth:
                switch ((AdbAuthType)arg0)
                {
                    case AdbAuthType.Signature:
                        AuthSignatures.Add(payload);
                        if (AuthSignatures.Count >= AuthTokenChallenges)
                        {
                            yield return AnswerConnect();
                            break;
                        }

                        yield return IssueAuthToken();
                        break;

                    case AdbAuthType.RsaPublicKey:
                        AuthPublicKeyPayload = payload;
                        yield return AnswerConnect();
                        break;
                }

                break;

            case AdbCommand.Open:
                {
                    var service = Encoding.UTF8.GetString(payload).TrimEnd('\0');
                    _openedServices.Add(service);
                    yield return AdbMessaging.Serialize(new AdbMessage(AdbCommand.Okay, 1, arg0));
                    if (service.StartsWith("reboot:", StringComparison.Ordinal))
                    {
                        yield return AdbMessaging.Serialize(new AdbMessage(AdbCommand.Clse, 1, arg0));
                    }

                    break;
                }

            case AdbCommand.Wrte:
                {
                    WrtePayloads.Add(payload);
                    OnWrte?.Invoke(payload);
                    if (AutoAcknowledgeWrites)
                        yield return AdbMessaging.Serialize(new AdbMessage(AdbCommand.Okay, 1, arg0));
                    if (payload.Length >= 4 && Encoding.ASCII.GetString(payload, 0, 4) == "DONE")
                    {
                        var syncOkay = new byte[8];
                        Encoding.ASCII.GetBytes("OKAY").CopyTo(syncOkay, 0);
                        yield return AdbMessaging.Serialize(new AdbMessage(AdbCommand.Wrte, 1, arg0, syncOkay));
                    }

                    if (SyncPullData is { Length: > 0 } pull &&
                        payload.Length >= 8 && Encoding.ASCII.GetString(payload, 0, 4) == SyncIds.Recv)
                    {
                        foreach (var packet in BuildSyncPullPackets(pull))
                        {
                            yield return AdbMessaging.Serialize(new AdbMessage(AdbCommand.Wrte, 1, arg0, packet));
                        }
                    }

                    break;
                }

            case AdbCommand.Clse:
                Interlocked.Increment(ref _closeCount);
                yield return AdbMessaging.Serialize(new AdbMessage(AdbCommand.Clse, arg1, arg0));
                break;

            default:
                break;
        }
    }

    private byte[] IssueAuthToken()
    {
        var token = new byte[AdbAuthentication.TokenSize];
        RandomNumberGenerator.Fill(token);
        AuthTokens.Add(token);
        _authTokensIssued++;
        return AdbMessaging.Serialize(new AdbMessage(AdbCommand.Auth, (uint)AdbAuthType.Token, 0, token));
    }

    private byte[] AnswerConnect() => AdbMessaging.Serialize(new AdbMessage(
        AdbCommand.Cnxn, AdbConstants.ProtocolVersion, PeerMaxPayload,
        Encoding.UTF8.GetBytes("device::fake\0")));

    private IEnumerable<byte[]> BuildSyncPullPackets(byte[] data)
    {
        var chunkSize = Math.Max(1, SyncPullChunkSize);
        var packets = new List<byte[]>();
        for (var start = 0; start < data.Length; start += chunkSize)
        {
            var length = Math.Min(chunkSize, data.Length - start);
            var packet = new byte[8 + length];
            Encoding.ASCII.GetBytes(SyncIds.Data).CopyTo(packet, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)length);
            data.AsSpan(start, length).CopyTo(packet.AsSpan(8));
            packets.Add(packet);
        }

        var done = new byte[8];
        Encoding.ASCII.GetBytes(SyncIds.Done).CopyTo(done, 0);
        packets.Add(done);
        return packets;
    }
}