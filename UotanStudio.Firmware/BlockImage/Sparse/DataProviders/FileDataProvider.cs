using System.Buffers;

namespace UotanStudio.Firmware.BlockImage.Sparse.DataProviders;

/// <summary>
/// Data provider over a byte range of a file on disk. Each full-region copy opens the path freshly and
/// leaves no handle behind, while random-access reads reuse one cached handle until the provider is
/// disposed; the file must not be replaced or truncated while it is being read.
/// <para>基于磁盘文件某个字节范围的提供程序。每次整区复制都会重新打开路径且不保留任何句柄，而随机访问读取则复用
/// 一个缓存句柄直到提供程序被释放；读取期间不得替换或截断该文件。</para>
/// </summary>
/// <param name="filePath">
/// Path of the file supplying the bytes; it is reopened on each read pass.
/// <para>提供字节的路径；每轮读取都会重新打开。</para>
/// </param>
/// <param name="offset">
/// Absolute byte offset in the file where the window starts.
/// <para>窗口起始处文件内的绝对字节偏移。</para>
/// </param>
/// <param name="length">
/// Byte length of the window.
/// <para>窗口的字节长度。</para>
/// </param>
internal class FileDataProvider(string filePath, long offset, long length) : ISparseDataProvider
{
    private readonly string _filePath = filePath;
    private readonly long _offset = offset;

    /// <summary>
    /// Byte length of the file window this provider exposes.
    /// <para>此提供程序暴露的文件窗口字节长度。</para>
    /// </summary>
    /// <value>
    /// The length given at construction; it is not compared with the file size, so an oversized
    /// window simply ends early when the underlying file runs out.
    /// <para>构造时传入的长度；不会与文件大小比较，窗口过大时只会因底层文件提前结束而少读。</para>
    /// </value>
    public long Length { get; } = length;

    /// <summary>
    /// Synchronously writes the provider's data range to the specified stream.
    /// <para>同步将提供程序的数据范围写入指定流。</para>
    /// </summary>
    /// <param name="stream">
    /// Writable destination stream; bytes are appended from its current position in blocks of up to
    /// 1 MiB and the stream is left open.
    /// <para>可写目标流；以不超过 1 MiB 的块从其当前位置追加写入，且不会关闭该流。</para>
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// The source file does not exist.
    /// <para>源文件不存在。</para>
    /// </exception>
    public void WriteTo(Stream stream)
    {
        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = Length;
            fs.Seek(_offset, SeekOrigin.Begin);

            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = fs.Read(buffer, 0, toRead);
                if (read == 0)
                {
                    break;
                }

                stream.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Asynchronously writes the provider's data range to the specified stream.
    /// <para>异步将提供程序的数据范围写入指定流。</para>
    /// </summary>
    /// <param name="stream">
    /// Writable destination stream receiving up to <see cref="Length"/> bytes in blocks of up to 1 MiB.
    /// <para>可写目标流，接收最多 <see cref="Length"/> 字节，每块不超过 1 MiB。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Passed to every source read and destination write; cancelling mid-copy leaves the partial bytes
    /// already handed to <paramref name="stream"/> in place.
    /// <para>传递给每次源读取与目标写入；复制中途取消会保留已交给 <paramref name="stream"/> 的部分字节。</para>
    /// </param>
    /// <returns>
    /// Task that completes once the whole window has been copied.
    /// <para>在整个窗口复制完成后结束的 Task。</para>
    /// </returns>
    /// <exception cref="FileNotFoundException">
    /// The source file does not exist.
    /// <para>源文件不存在。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled during the copy.
    /// <para><paramref name="cancellationToken"/> 在复制过程中被取消。</para>
    /// </exception>
    public async Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = Length;
            fs.Seek(_offset, SeekOrigin.Begin);

            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await fs.ReadAsync(buffer, 0, toRead, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await stream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads data from the provider into a byte array buffer.
    /// <para>从提供程序读取数据到字节数组缓冲区。</para>
    /// </summary>
    /// <param name="inOffset">
    /// Offset inside the file window; <c>0</c> is the window's first byte.
    /// <para>文件窗口内的偏移；<c>0</c> 表示窗口第一个字节。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination array receiving the bytes.
    /// <para>接收字节的目标数组。</para>
    /// </param>
    /// <param name="bufferOffset">
    /// Index in <paramref name="buffer"/> where writing starts.
    /// <para>开始写入的 <paramref name="buffer"/> 索引。</para>
    /// </param>
    /// <param name="count">
    /// Maximum number of bytes requested.
    /// <para>请求的最大字节数。</para>
    /// </param>
    /// <returns>
    /// Bytes read, truncated at the window end.
    /// <para>实际读取的字节数，到达窗口末尾时会被截断。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bufferOffset"/> or <paramref name="count"/> falls outside <paramref name="buffer"/>.
    /// <para><paramref name="bufferOffset"/> 或 <paramref name="count"/> 超出 <paramref name="buffer"/> 范围。</para>
    /// </exception>
    public int Read(long inOffset, byte[] buffer, int bufferOffset, int count) => Read(inOffset, buffer.AsSpan(bufferOffset, count));

    /// <summary>
    /// Lazily opened long-lived stream used by <see cref="Read(long, Span{Byte})"/>; avoids
    /// opening and closing a FileStream on every read call.
    /// <para>由 <see cref="Read(long, Span{Byte})"/> 懒打开的长生命周期流，避免每次读取
    /// 都新建并关闭 FileStream。</para>
    /// </summary>
    private FileStream? _readStream;

    /// <summary>
    /// Reads data from the provider into a Span{Byte}.
    /// <para>从提供程序读取数据到 Span{Byte}。</para>
    /// </summary>
    /// <param name="inOffset">
    /// Offset inside the file window; a value at or beyond <see cref="Length"/> reads nothing.
    /// <para>文件窗口内的偏移；等于或超过 <see cref="Length"/> 时不读取任何数据。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination span whose length is the maximum wanted.
    /// <para>长度即所需最大字节数的目标 Span。</para>
    /// </param>
    /// <returns>
    /// Bytes copied by the underlying file stream, or <c>0</c> at or past the window end; a short
    /// count also means the file ended before the declared window.
    /// <para>底层文件流复制的字节数，位于或越过窗口末尾时为 <c>0</c>；偏少的读取也可能表示文件在声明窗口之前结束。</para>
    /// </returns>
    /// <remarks>
    /// Reads share one seekable, random-access stream, so concurrent calls from several threads are
    /// not supported; the handle stays open until <see cref="Dispose"/> and a later read reopens it.
    /// <para>读取共用同一个可寻址的随机访问流，因此不支持多线程并发调用；句柄在 <see cref="Dispose"/>
    /// 之前保持打开，之后的读取会重新打开它。</para>
    /// </remarks>
    public int Read(long inOffset, Span<byte> buffer)
    {
        if (inOffset >= Length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, Length - inOffset);
        if (_readStream is null)
        {
            _readStream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        }
        _readStream.Seek(_offset + inOffset, SeekOrigin.Begin);
        return _readStream.Read(buffer[..toRead]);
    }

    /// <summary>
    /// Creates a sub-provider that represents a sub-range of this provider.
    /// <para>创建表示此提供程序子范围的子提供程序。</para>
    /// </summary>
    /// <param name="subOffset">
    /// Offset inside this window where the child window starts.
    /// <para>子窗口在本窗口内的起始偏移。</para>
    /// </param>
    /// <param name="subLength">
    /// Byte length of the child window.
    /// <para>子窗口的字节长度。</para>
    /// </param>
    /// <returns>
    /// Independent provider for the same file path; it owns its own read handle and disposing it does
    /// not affect this instance.
    /// <para>指向同一文件的独立提供程序；它拥有自己的读取句柄，释放它不会影响当前实例。</para>
    /// </returns>
    public ISparseDataProvider GetSubProvider(long subOffset, long subLength) => new FileDataProvider(_filePath, _offset + subOffset, subLength);

    /// <summary>
    /// Releases any resources held by the provider.
    /// <para>释放提供程序持有的所有资源。</para>
    /// </summary>
    /// <remarks>
    /// Only the cached read handle is closed; the destination streams this provider ever wrote to are
    /// left untouched, and a subsequent read simply opens a new handle.
    /// <para>仅关闭缓存的读取句柄；此前写入过的目标流不受影响，后续读取会直接打开新的句柄。</para>
    /// </remarks>
    public virtual void Dispose()
    {
        _readStream?.Dispose();
        _readStream = null;
    }
}