using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb;

/// <summary>
/// Exposes an opened USB device session as a <see cref="Stream"/>.
/// A thin-layer convenience wrapper: reads and writes map directly to session bulk I/O
/// with the configured timeouts. The stream does NOT own the session — callers remain
/// responsible for the session lifetime.
/// <para>将会话作为 <see cref="Stream"/> 暴露的薄层便利包装：读写直接映射到会话的批量 I/O，
/// 并使用配置的超时时间。流不拥有会话——调用方仍负责会话的生命周期。</para>
/// </summary>
internal sealed class UsbStream : Stream
{

    /// <summary>
    /// Initializes a new stream over the specified session.
    /// <para>在指定会话上初始化新流。</para>
    /// </summary>
    /// <param name="session">The opened USB device session. <para>已打开的 USB 设备会话。</para></param>
    public UsbStream(IUsbDeviceSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ReadTimeout = session.DefaultTimeoutMs;
        WriteTimeout = session.DefaultTimeoutMs;
    }

    /// <summary>
    /// Gets the wrapped session.
    /// <para>获取被包装的会话。</para>
    /// </summary>
    public IUsbDeviceSession Session { get; }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanTimeout => true;

    private const int InfiniteTimeout = -1;

    private int _readTimeout = InfiniteTimeout;
    private int _writeTimeout = InfiniteTimeout;

    /// <inheritdoc />
    public override int ReadTimeout
    {
        get => _readTimeout;
        set
        {
            if (value is < 0 and not InfiniteTimeout)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _readTimeout = value;
        }
    }

    /// <inheritdoc />
    public override int WriteTimeout
    {
        get => _writeTimeout;
        set
        {
            if (value is < 0 and not InfiniteTimeout)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _writeTimeout = value;
        }
    }

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("USB streams are not seekable.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("USB streams are not seekable.");
        set => throw new NotSupportedException("USB streams are not seekable.");
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (count < 0 || count > buffer.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0)
        {
            return 0;
        }

        return Session.ReadInto(buffer, offset, count, ReadTimeout);
    }

    /// <inheritdoc />
    /// <exception cref="IOException">The endpoint accepted fewer than <paramref name="count"/> bytes, which a stream
    /// write cannot report as a partial success.
    /// <para>端点接受的字节少于 <paramref name="count"/>，而流写入无法把它表达为部分成功。</para></exception>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (count < 0 || count > buffer.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0)
        {
            return;
        }

        var written = Session.Write(buffer, offset, count, WriteTimeout);
        if (written != count)
        {
            throw new IOException(
                $"The USB bulk write accepted {written} of {count} bytes; the transfer timed out or the endpoint reported an error.");
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("USB streams are not seekable.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("USB streams are not seekable.");
}