namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Write-only stream that refuses to exceed a byte cap, checked together with a <see cref="CancellationToken"/> on
/// every write; exceeding the cap throws <see cref="InvalidDataException"/> without touching the inner stream,
/// and the wrapper never disposes the inner stream.
/// <para>只写流，拒绝超过字节上限，并在每次写入时一并检查 <see cref="CancellationToken"/>；超限时抛出
/// <see cref="InvalidDataException"/> 且不写入内层流，包装器从不释放内层流。</para>
/// </summary>
/// <param name="output">Inner stream to write to. <para>写入目标内层流。</para></param>
/// <param name="maxBytes">Hard cap on total bytes written. <para>写入总字节数的硬性上限。</para></param>
/// <param name="cancellationToken">Token checked before each write. <para>每次写入前检查的取消令牌。</para></param>
public sealed class CappedWriteStream(Stream output, long maxBytes, CancellationToken cancellationToken = default) : Stream
{
    private readonly CancellationToken _cancellationToken = cancellationToken;

    /// <summary>
    /// Gets the number of bytes written so far.
    /// <para>获取已写入的字节数。</para>
    /// </summary>
    /// <value>Accumulated write count in bytes. <para>累计写入字节数。</para></value>
    public long Written { get; private set; }

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => Written;

    /// <inheritdoc/>
    public override long Position { get => Written; set => throw new NotSupportedException(); }

    /// <inheritdoc/>
    public override void Flush() => output.Flush();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (Written + buffer.Length > maxBytes)
        {
            throw new InvalidDataException($"Stream payload exceeds the write limit of {maxBytes} bytes.");
        }

        output.Write(buffer);
        Written += buffer.Length;
    }
}