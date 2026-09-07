using System.Buffers;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;

public class StreamDataProvider(Stream stream, long offset, long length, bool leaveOpen = true) : ISparseDataProvider
{
    private readonly Stream _stream = stream;
    private readonly long _offset = offset;
    private readonly bool _leaveOpen = leaveOpen;

    public long Length { get; } = length;

    /// <summary>
    /// Synchronously writes the provider's data to the specified output stream.
    /// <para>同步将提供程序的数据写入指定输出流。</para>
    /// </summary>
    public void WriteTo(Stream outStream)
    {
        if (_stream.CanSeek)
        {
            _stream.Seek(_offset, SeekOrigin.Begin);
        }
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = Length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = _stream.Read(buffer, 0, toRead);
                if (read == 0)
                {
                    break;
                }

                outStream.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Asynchronously writes the provider's data to the specified output stream.
    /// <para>异步将提供程序的数据写入指定输出流。</para>
    /// </summary>
    public async Task WriteToAsync(Stream outStream, CancellationToken cancellationToken = default)
    {
        if (_stream.CanSeek)
        {
            _stream.Seek(_offset, SeekOrigin.Begin);
        }
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = Length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await _stream.ReadAsync(buffer, 0, toRead, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await outStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads bytes from this provider into a byte array buffer.
    /// <para>从此提供程序读取字节到字节数组缓冲区。</para>
    /// </summary>
    public int Read(long inOffset, byte[] buffer, int bufferOffset, int count) => Read(inOffset, buffer.AsSpan(bufferOffset, count));

    /// <summary>
    /// Reads bytes from this provider into a Span{Byte}.
    /// <para>从此提供程序读取字节到 Span{Byte}。</para>
    /// </summary>
    public int Read(long inOffset, Span<byte> buffer)
    {
        if (inOffset >= Length)
        {
            return 0;
        }

        var toRead = Math.Min(buffer.Length, (int)(Length - inOffset));
        if (_stream.CanSeek)
        {
            _stream.Seek(_offset + inOffset, SeekOrigin.Begin);
        }
        return _stream.Read(buffer[..toRead]);
    }

    /// <summary>
    /// Creates a sub-provider that represents a sub-range of this provider.
    /// <para>创建表示此提供程序子范围的子提供程序。</para>
    /// </summary>
    public ISparseDataProvider GetSubProvider(long subOffset, long subLength) => new StreamDataProvider(_stream, _offset + subOffset, subLength, true);

    /// <summary>
    /// Disposes the provider and optionally closes the underlying stream.
    /// <para>释放提供程序，并根据配置选择是否关闭底层流。</para>
    /// </summary>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
