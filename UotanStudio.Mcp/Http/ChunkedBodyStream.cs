using System.Buffers;

namespace UotanStudio.Mcp.Http;

/// <summary>
/// Frames every write as an HTTP/1.1 chunk so responses can stream (SSE) without knowing the total
/// length in advance.
/// <para>把每次写入编码为 HTTP/1.1 分块，使响应无需预知总长度即可流式输出（SSE）。</para>
/// The wrapped stream stays owned by the connection; disposing this wrapper never disposes it.
/// <para>被包装的流由连接持有；释放本包装不会释放底层流。</para>
/// </summary>
internal sealed class ChunkedBodyStream : Stream
{
    private static readonly byte[] Terminator = "0\r\n\r\n"u8.ToArray();
    private static readonly byte[] Crlf = "\r\n"u8.ToArray();

    private readonly Stream _inner;
    private bool _completed;

    internal ChunkedBodyStream(Stream inner) => _inner = inner;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_completed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Writes the terminating zero-length chunk, completing the response body.
    /// <para>写入结束用的零长度分块，结束响应体。</para>
    /// </summary>
    internal async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        await _inner.WriteAsync(Terminator, cancellationToken).ConfigureAwait(false);
        await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (count == 0)
        {
            return;
        }

        Span<byte> header = stackalloc byte[18];
        var headerLength = WriteChunkHeader((uint)count, header);
        _inner.Write(header[..headerLength]);
        _inner.Write(buffer.AsSpan(offset, count));
        _inner.Write(Crlf);
        _inner.Flush();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (buffer.Length == 0)
        {
            return;
        }

        var header = ArrayPool<byte>.Shared.Rent(18);
        try
        {
            var headerLength = WriteChunkHeader((uint)buffer.Length, header);
            await _inner.WriteAsync(header.AsMemory(0, headerLength), cancellationToken).ConfigureAwait(false);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _inner.WriteAsync(Crlf, cancellationToken).ConfigureAwait(false);
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    /// <summary>Writes the hexadecimal chunk size followed by CRLF. <para>写入十六进制分块长度与 CRLF。</para></summary>
    private static int WriteChunkHeader(uint size, Span<byte> destination)
    {
        const string Digits = "0123456789ABCDEF";

        Span<char> digits = stackalloc char[8];
        var digitCount = 0;
        do
        {
            digits[digitCount++] = Digits[(int)(size & 0xF)];
            size >>= 4;
        }
        while (size != 0);

        var position = 0;
        for (var i = digitCount - 1; i >= 0; i--)
        {
            destination[position++] = (byte)digits[i];
        }

        destination[position++] = (byte)'\r';
        destination[position++] = (byte)'\n';
        return position;
    }
}