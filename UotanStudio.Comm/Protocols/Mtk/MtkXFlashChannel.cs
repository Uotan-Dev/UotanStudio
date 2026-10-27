using System.Buffers;
using System.Buffers.Binary;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Bounded XFlash framing for an exclusively owned transport. The enclosing DA session
/// must serialize complete command exchanges, not just individual frames. Stream payloads are
/// chunked at a fixed width and staged through a pooled buffer that is returned once every
/// awaited write has completed, so no frame byte outlives the call.
/// <para>独占传输上的有界 XFlash 帧编解码：外层 DA 会话必须串行化完整的命令交互，而不只是单帧。流载荷按固定
/// 宽度分块，并经由池化缓冲区暂存，在所有等待中的写入完成后归还，因此没有帧字节存活到调用之后。</para>
/// </summary>
internal sealed class MtkXFlashChannel
{
    private const uint Magic = 0xFEEEEEEF;
    private const int StreamChunkLength = 512;
    private readonly ICommTransport _transport;
    private readonly int _timeoutMs;
    private readonly int _maximumPayload;

    internal MtkXFlashChannel(ICommTransport transport, int timeoutMs = 5000, int maximumPayload = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayload);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPayload, 16 * 1024 * 1024);
        _transport = transport;
        _timeoutMs = timeoutMs;
        _maximumPayload = maximumPayload;
    }

    internal bool IsFaulted { get; private set; }

    internal async Task SendStreamAsync(uint dataType, Stream source, int length,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ValidateSource(source, length);
        RequireReady(cancellationToken);
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), dataType);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)length);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamChunkLength);
        try
        {
            await WriteAsync(header, Environment.TickCount64, cancellationToken).ConfigureAwait(false);
            var sent = 0;
            while (sent < length)
            {
                RequireReady(cancellationToken);
                var count = Math.Min(StreamChunkLength, length - sent);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                await WriteAsync(buffer, count, Environment.TickCount64, cancellationToken).ConfigureAwait(false);
                sent += count;
                progress?.Report(sent);
            }
        }
        catch
        {
            IsFaulted = true;
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static void ValidateSource(Stream source, int length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 64 * 1024 * 1024);
        if (!source.CanRead || (source.CanSeek && source.Length - source.Position < length))
        {
            throw new ArgumentException("The XFlash source is unreadable or shorter than the declared payload.", nameof(source));
        }
    }

    internal async Task SendAsync(uint dataType, byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, _maximumPayload);
        RequireReady(cancellationToken);
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), dataType);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payload.Length);
        var started = Environment.TickCount64;
        try
        {
            await WriteAsync(header, started, cancellationToken).ConfigureAwait(false);
            if (payload.Length != 0)
            {
                await WriteAsync(payload, started, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            IsFaulted = true;
            throw;
        }
    }

    internal Task<(uint DataType, byte[] Payload)> ReceiveAsync(CancellationToken cancellationToken)
        => ReceiveAsync(_maximumPayload, cancellationToken);

    internal async Task<(uint DataType, byte[] Payload)> ReceiveAsync(int maximumPayload, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayload);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPayload, _maximumPayload);
        RequireReady(cancellationToken);
        var started = Environment.TickCount64;
        try
        {
            var header = new byte[12];
            await ReadExactlyAsync(header, started, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            {
                throw new MtkProtocolException("Invalid XFlash frame magic.");
            }
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            if (length > maximumPayload)
            {
                throw new MtkProtocolException("XFlash payload exceeds the configured receive limit.");
            }
            var payload = new byte[(int)length];
            await ReadExactlyAsync(payload, started, cancellationToken).ConfigureAwait(false);
            return (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), payload);
        }
        catch
        {
            IsFaulted = true;
            throw;
        }
    }

    private async Task ReadExactlyAsync(byte[] buffer, long started, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            RequireReady(cancellationToken);
            var count = await _transport.ReadAsync(buffer, offset, buffer.Length - offset,
                RemainingTime(started), cancellationToken).ConfigureAwait(false);
            RequireReady(cancellationToken);
            if (count <= 0 || count > buffer.Length - offset)
            {
                throw new MtkProtocolException("Truncated XFlash frame.");
            }
            offset += count;
        }
    }

    private Task WriteAsync(byte[] buffer, long started, CancellationToken cancellationToken)
        => WriteAsync(buffer, buffer.Length, started, cancellationToken);

    private async Task WriteAsync(byte[] buffer, int length, long started, CancellationToken cancellationToken)
    {
        RequireReady(cancellationToken);
        var count = await _transport.WriteAsync(buffer, 0, length,
            RemainingTime(started), cancellationToken).ConfigureAwait(false);
        RequireReady(cancellationToken);
        if (count != length)
        {
            throw new MtkProtocolException("Incomplete XFlash write; reconnect before continuing.");
        }
    }

    private int RemainingTime(long started)
    {
        var remaining = _timeoutMs - (Environment.TickCount64 - started);
        return remaining > 0 ? (int)remaining : throw new TimeoutException("XFlash frame deadline expired.");
    }

    private void RequireReady(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsFaulted || !_transport.IsConnected)
        {
            throw new InvalidOperationException("XFlash framing requires a synchronized connected transport.");
        }
    }
}