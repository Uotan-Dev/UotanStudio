using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Tests.Comm.TestDoubles;

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

    /// <summary>Gets all bytes written through this transport, in order. 所有写入字节。</summary>
    public List<byte[]> Writes { get; } = [];

    /// <summary>Gets whether the transport reports connected. 是否报告已连接。</summary>
    public bool IsConnected => _connected;

    /// <summary>
    /// Optional hook invoked on every write; returned byte arrays are enqueued as device
    /// responses. <para>每次写入时调用的可选钩子；返回的字节数组会被入队为设备响应。</para>
    /// </summary>
    public Func<byte[], IEnumerable<byte[]>>? OnWrite { get; set; }

    /// <summary>When set, writes return 0 (simulating a dead transport). 置位时写入返回 0。</summary>
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

    /// <summary>Enqueues a device response. 入队一条设备响应。</summary>
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

    /// <summary>Enqueues a UTF-8 text device response. 入队一条 UTF-8 文本设备响应。</summary>
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

    /// <summary>Gets the services the client opened (e.g. "sync:", "reboot:sideload"). 客户端打开的服务列表。</summary>
    public IReadOnlyList<string> OpenedServices => _openedServices;

    /// <summary>Gets the raw payloads of every WRTE the client sent (sync packets included). 客户端发送的 WRTE 负载。</summary>
    public List<byte[]> WrtePayloads { get; } = [];

    public uint PeerMaxPayload { get; init; } = AdbConstants.MaxPayload;

    public bool AutoAcknowledgeWrites { get; init; } = true;

    public Action<byte[]>? OnWrte { get; init; }

    private int _closeCount;

    public int CloseCount => Volatile.Read(ref _closeCount);

    /// <summary>Initializes a new ADB device simulator. 初始化 ADB 设备模拟器。</summary>
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
                yield return AdbMessaging.Serialize(new AdbMessage(
                    AdbCommand.Cnxn, AdbConstants.ProtocolVersion, PeerMaxPayload,
                    Encoding.UTF8.GetBytes("device::fake\0")));
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
}