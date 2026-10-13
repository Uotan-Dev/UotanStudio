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
    private byte[]? _headRemainder;
    private bool _connected = true;

    /// <summary>
    /// Gets all bytes written through this transport, in order.
    /// <para>所有写入字节。</para>
    /// </summary>
    public List<byte[]> Writes { get; } = [];

    /// <summary>Gets the number of transport reads, including empty reads.</summary>
    public int ReadCount { get; private set; }

    /// <summary>Gets the number of disposal calls, independently of the simulated connection state.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>Gets the number of <see cref="Reset"/> calls.</summary>
    public int ResetCount { get; private set; }

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

    /// <summary>Optional hook for simulating local disconnect failures before changing connection state.</summary>
    public Action? OnDisconnect { get; set; }

    /// <summary>
    /// Optional hook consulted on every write with the same offset/length span the write would send;
    /// returning <see langword="true"/> makes the write fail with an <see cref="IOException"/>,
    /// simulating a transport that stops accepting bytes.
    /// <para>每次写入时都会以与该次写入相同的 offset/length 区间咨询的可选钩子；返回
    /// <see langword="true"/> 会让该次写入以 <see cref="IOException"/> 失败，用于模拟不再接受字节的传输。</para>
    /// </summary>
    public Func<byte[], int, int, bool>? RejectWrite { get; set; }

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
    public void Connect() => _connected = true;

    /// <inheritdoc />
    public void Disconnect()
    {
        OnDisconnect?.Invoke();
        _connected = false;
    }

    /// <inheritdoc />
    public void Reset() => ResetCount++;

    /// <inheritdoc />
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        if (RejectWrite?.Invoke(data, offset, length) == true)
        {
            throw new IOException("Transport refused the write.");
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
    public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => Task.FromResult(Write(data, offset, length, timeoutMs));

    /// <inheritdoc />
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        lock (_lock)
        {
            ReadCount++;
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
            DisposeCount++;
            _reads.Clear();
            _headRemainder = null;
        }
    }
}