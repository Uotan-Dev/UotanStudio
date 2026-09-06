using System.Buffers;

namespace UotanStudio.Firmware.Sparse;

public class FileDataProvider(string filePath, long offset, long length) : ISparseDataProvider
{
    private readonly string _filePath = filePath;
    private readonly long _offset = offset;

    public long Length { get; } = length;

    /// <summary>
    /// Synchronously writes the provider's data range to the specified stream.
    /// <para>同步将提供程序的数据范围写入指定流。</para>
    /// </summary>
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
    public int Read(long inOffset, byte[] buffer, int bufferOffset, int count) => Read(inOffset, buffer.AsSpan(bufferOffset, count));

    /// <summary>
    /// Reads data from the provider into a Span{Byte}.
    /// <para>从提供程序读取数据到 Span{Byte}。</para>
    /// </summary>
    public int Read(long inOffset, Span<byte> buffer)
    {
        if (inOffset >= Length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, Length - inOffset);
        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        fs.Seek(_offset + inOffset, SeekOrigin.Begin);
        return fs.Read(buffer[..toRead]);
    }

    /// <summary>
    /// Creates a sub-provider that represents a sub-range of this provider.
    /// <para>创建表示此提供程序子范围的子提供程序。</para>
    /// </summary>
    public ISparseDataProvider GetSubProvider(long subOffset, long subLength) => new FileDataProvider(_filePath, _offset + subOffset, subLength);

    /// <summary>
    /// Releases any resources held by the provider.
    /// <para>释放提供程序持有的所有资源。</para>
    /// </summary>
    public virtual void Dispose() { }
}
