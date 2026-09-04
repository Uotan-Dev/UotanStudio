using System.Text;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Tests.Comm.TestDoubles;

/// <summary>
/// A scripted <see cref="ICommTransport"/> for protocol tests where empty reads must return
/// 0 immediately instead of blocking for the full timeout (keeps probe/timeout paths fast).
/// Writes are recorded in order and an optional <see cref="OnWrite"/> hook enqueues device
/// responses, exactly like <see cref="FakeCommTransport"/>.
/// <para>用于协议测试的脚本化 <see cref="ICommTransport"/>：空队列读取立即返回 0 而非阻塞
/// 至超时（使探测/超时路径保持快速）。写入按顺序记录，可选的 <see cref="OnWrite"/> 钩子负责
/// 入队设备响应，行为与 <see cref="FakeCommTransport"/> 一致。</para>
/// </summary>
public sealed class ScriptedCommTransport : ICommTransport
{
    private readonly object _lock = new();
    private readonly Queue<byte[]> _reads = new();
    // 上次部分消费后遗留的块剩余部分：作为字节流延续保持在"队首"，下一次读取优先消费，
    // 不能 Enqueue 到队尾（否则会与后续独立入队的包错位）。
    // Remainder of a partially-consumed chunk: it continues the byte stream and must be consumed
    // next, NOT re-queued at the tail (that would interleave it with later independent packets).
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
    public void Connect() => _connected = true;

    /// <inheritdoc />
    public void Disconnect() => _connected = false;

    /// <inheritdoc />
    public void Reset() { }

    /// <inheritdoc />
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
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
    public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => Task.FromResult(Write(data, offset, length, timeoutMs));

    /// <inheritdoc />
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        lock (_lock)
        {
            byte[]? chunk;
            if (_headRemainder is not null)
            {
                chunk = _headRemainder;
                _headRemainder = null;
            }
            else
            {
                if (_reads.Count == 0)
                {
                    return 0;
                }

                chunk = _reads.Dequeue();
            }

            var n = Math.Min(length, chunk.Length);
            Array.Copy(chunk, 0, buffer, offset, n);
            if (n < chunk.Length)
            {
                _headRemainder = chunk[n..];
            }

            return n;
        }
    }

    /// <inheritdoc />
    public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => Task.FromResult(Read(buffer, offset, length, timeoutMs));

    /// <inheritdoc />
    public Stream GetStream() => throw new NotSupportedException();

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _reads.Clear();
            _headRemainder = null;
        }
    }
}