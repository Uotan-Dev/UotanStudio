using System.Diagnostics;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Tools.MtkProbe;

internal sealed class TracingTransport(ICommTransport inner, Action<string> log)
    : ICommTransport, ICommTransportZeroLengthWrite
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public bool IsConnected => inner.IsConnected;

    public void Connect() => inner.Connect();

    public void Disconnect() => inner.Disconnect();

    public void Reset() => inner.Reset();

    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        var read = inner.Read(buffer, offset, length, timeoutMs);
        if (read > 0)
        {
            log($"  RX[{read}] {Convert.ToHexString(buffer.AsSpan(offset, read))}");
        }
        else
        {
            log($"  RX[{read}] timeout after {timeoutMs}ms (t={_clock.ElapsedMilliseconds}ms)");
        }

        return read;
    }

    public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var read = await inner.ReadAsync(buffer, offset, length, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (read > 0)
            {
                log($"  RX[{read}] {Convert.ToHexString(buffer.AsSpan(offset, read))}");
            }
            else
            {
                log($"  RX[{read}] empty reply (t={_clock.ElapsedMilliseconds}ms)");
            }

            return read;
        }
        catch (TimeoutException)
        {
            log($"  RX[timeout] no bytes within {timeoutMs}ms (t={_clock.ElapsedMilliseconds}ms)");
            throw;
        }
    }

    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        var written = inner.Write(data, offset, length, timeoutMs);
        var shown = (int)Math.Min(written, 32);
        var suffix = written > 32 ? $"…(+{written - 32})" : string.Empty;
        log($"  TX[{written}] {(shown > 0 ? Convert.ToHexString(data.AsSpan(offset, shown)) : string.Empty)}{suffix}");
        return written;
    }

    public async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var written = await inner.WriteAsync(data, offset, length, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        var shown = (int)Math.Min(written, 32);
        var suffix = written > 32 ? $"…(+{written - 32})" : string.Empty;
        log($"  TX[{written}] {(shown > 0 ? Convert.ToHexString(data.AsSpan(offset, shown)) : string.Empty)}{suffix}");
        return written;
    }

    public Stream GetStream() => inner.GetStream();

    public Task WriteZeroLengthPacketAsync(int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (inner is ICommTransportZeroLengthWrite zlp)
        {
            return zlp.WriteZeroLengthPacketAsync(timeoutMs, cancellationToken);
        }

        log("  ZLP skipped: the inner transport has no zero-length capability.");
        return Task.CompletedTask;
    }

    public void Dispose() => inner.Dispose();
}