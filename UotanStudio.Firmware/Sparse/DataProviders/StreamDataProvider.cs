using System.Buffers;

namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Provides access to a region of a <see cref="Stream"/> as an <see cref="ISparseDataProvider"/>.
/// Supports synchronous and asynchronous reads and can optionally leave the underlying
/// stream open when disposed.
/// <para>提供对 Stream 区域的访问，实现 ISparseDataProvider 接口。
/// 支持同步和异步读取，可选择在释放时保持底层流打开。</para>
/// </summary>
/// <remarks>
/// Initializes a new <see cref="StreamDataProvider"/> for a section of a stream.
/// <para>为流的某个区段初始化新的 StreamDataProvider。</para>
/// </remarks>
/// <param name="stream">The source stream to read from. <para>要读取的源流。</para></param>
/// <param name="offset">Byte offset within the stream where the provider view starts. <para>流中提供程序视图起始的字节偏移。</para></param>
/// <param name="length">Number of bytes exposed by this provider. <para>此提供程序暴露的字节数。</para></param>
/// <param name="leaveOpen">If true, do not close the source stream when disposing. <para>如果为 true，释放时不关闭源流。</para></param>
public class StreamDataProvider(Stream stream, long offset, long length, bool leaveOpen = true) : ISparseDataProvider
{
    private readonly Stream _stream = stream;
    private readonly long _offset = offset;
    private readonly bool _leaveOpen = leaveOpen;

    /// <summary>
    /// Gets the total length, in bytes, of the data exposed by this provider.
    /// <para>获取此提供程序暴露的数据总字节长度。</para>
    /// </summary>
    public long Length { get; } = length;

    /// <summary>
    /// Synchronously writes the provider's data to the specified output stream.
    /// <para>同步将提供程序的数据写入指定输出流。</para>
    /// </summary>
    /// <param name="outStream">Destination stream to receive the bytes. <para>接收字节的目标流。</para></param>
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
    /// <param name="outStream">Destination stream to receive the bytes. <para>接收字节的目标流。</para></param>
    /// <param name="cancellationToken">Cancellation token for the asynchronous operation. <para>异步操作的取消令牌。</para></param>
    /// <returns>A task that completes when the write finishes. <para>写入完成时结束的任务。</para></returns>
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
    /// <param name="inOffset">Byte offset relative to the provider's start. <para>相对于提供程序起始的字节偏移。</para></param>
    /// <param name="buffer">Destination buffer to receive bytes. <para>接收字节的目标缓冲区。</para></param>
    /// <param name="bufferOffset">Offset in the destination buffer to begin writing. <para>目标缓冲区中的起始写入偏移。</para></param>
    /// <param name="count">Maximum number of bytes to read. <para>最大读取字节数。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取的字节数。</para></returns>
    public int Read(long inOffset, byte[] buffer, int bufferOffset, int count) => Read(inOffset, buffer.AsSpan(bufferOffset, count));

    /// <summary>
    /// Reads bytes from this provider into a <see cref="Span{Byte}"/>.
    /// <para>从此提供程序读取字节到 Span{Byte}。</para>
    /// </summary>
    /// <param name="inOffset">Byte offset relative to the provider's start. <para>相对于提供程序起始的字节偏移。</para></param>
    /// <param name="buffer">Span to receive the data. <para>接收数据的 Span。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取的字节数。</para></returns>
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
    /// <param name="subOffset">Byte offset relative to this provider's start. <para>相对于此提供程序起始的字节偏移。</para></param>
    /// <param name="subLength">Length in bytes of the sub-range. <para>子范围的字节长度。</para></param>
    /// <returns>An <see cref="ISparseDataProvider"/> for the requested sub-range. <para>请求子范围的 ISparseDataProvider。</para></returns>
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