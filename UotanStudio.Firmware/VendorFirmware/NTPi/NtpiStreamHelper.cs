namespace UotanStudio.Firmware.VendorFirmware.NTPi;

internal static class NtpiStreamHelper
{
    /// <summary>
    /// Reads exactly the specified number of bytes from the stream into the buffer.
    /// <para>从流中精确读取指定字节数到缓冲区的指定位置。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable stream positioned at the first byte to consume. <para>定位在待读取首个字节处的可读流。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination array receiving the bytes. <para>接收字节的目标数组。</para>
    /// </param>
    /// <param name="offset">
    /// Zero-based index in <paramref name="buffer"/> where reading begins. <para><paramref name="buffer"/> 中开始写入的零基索引。</para>
    /// </param>
    /// <param name="count">
    /// Exact number of bytes to read; zero succeeds without touching the stream. <para>需要读取的确切字节数；为 0 时不读取流即成功返回。</para>
    /// </param>
    public static void ReadExact(Stream stream, byte[] buffer, int offset, int count) => stream.ReadExactly(buffer, offset, count);

    /// <summary>
    /// Asynchronously reads exactly the specified number of bytes from the stream into the buffer.
    /// <para>异步从流中精确读取指定字节数到缓冲区的指定位置。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable stream positioned at the first byte to consume. <para>定位在待读取首个字节处的可读流。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination array receiving the bytes. <para>接收字节的目标数组。</para>
    /// </param>
    /// <param name="offset">
    /// Zero-based index in <paramref name="buffer"/> where reading begins. <para><paramref name="buffer"/> 中开始写入的零基索引。</para>
    /// </param>
    /// <param name="count">
    /// Exact number of bytes to read; zero completes without touching the stream. <para>需要读取的确切字节数；为 0 时不读取流即完成。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed by the underlying read; the default (none) token cannot cancel. <para>底层读取观察的取消令牌；默认（无）令牌不可取消。</para>
    /// </param>
    public static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) => await stream.ReadExactlyAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads the entire stream into a pooled byte array, returning (data, valid length).
    /// <para>将整个流读取到池化字节数组中,返回 (数据, 有效长度)。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable stream consumed from its current position to the end; its position advances and it is left open.
    /// <para>从其当前位置读到末尾的可读流；位置随之前进，流保持打开。</para>
    /// </param>
    /// <returns>
    /// The backing array of an expandable buffer together with the number of leading bytes that hold data; bytes beyond
    /// that length are unused capacity.
    /// <para>可扩展缓冲区的底层数组以及其中有效数据占用的前导字节数；超出该长度的字节属于未使用的容量。</para>
    /// </returns>
    public static (byte[] Data, int Length) ReadStreamToPool(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.GetBuffer();
        return (data, (int)ms.Length);
    }

    /// <summary>
    /// Asynchronously reads the entire stream into a pooled byte array, returning (data, valid length).
    /// <para>异步将整个流读取到池化字节数组中,返回 (数据, 有效长度)。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable stream consumed from its current position to the end; its position advances and it is left open.
    /// <para>从其当前位置读到末尾的可读流；位置随之前进，流保持打开。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed while copying; the default (none) token cannot cancel. <para>复制期间观察的取消令牌；默认（无）令牌不可取消。</para>
    /// </param>
    /// <returns>
    /// The backing array of an expandable buffer together with the number of leading bytes that hold data; bytes beyond
    /// that length are unused capacity.
    /// <para>可扩展缓冲区的底层数组以及其中有效数据占用的前导字节数；超出该长度的字节属于未使用的容量。</para>
    /// </returns>
    public static async Task<(byte[] Data, int Length)> ReadStreamToPoolAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var data = ms.GetBuffer();
        return (data, (int)ms.Length);
    }

    /// <summary>
    /// Returns the pooled buffer (compatibility placeholder; the shared ArrayPool is not used here).
    /// <para>归还池化缓冲区(此处为兼容占位,实际未使用 ArrayPool 共享池)。</para>
    /// </summary>
    public static void ReturnPooledBuffer() { }
}