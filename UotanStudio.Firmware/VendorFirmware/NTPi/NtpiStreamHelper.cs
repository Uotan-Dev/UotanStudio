namespace UotanStudio.Firmware.VendorFirmware.NTPi;

internal static class NtpiStreamHelper
{
    /// <summary>
    /// Reads exactly the specified number of bytes from the stream into the buffer.
    /// <para>从流中精确读取指定字节数到缓冲区的指定位置。</para>
    /// </summary>
    public static void ReadExact(Stream stream, byte[] buffer, int offset, int count) => stream.ReadExactly(buffer, offset, count);

    /// <summary>
    /// Asynchronously reads exactly the specified number of bytes from the stream into the buffer.
    /// <para>异步从流中精确读取指定字节数到缓冲区的指定位置。</para>
    /// </summary>
    public static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) => await stream.ReadExactlyAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads the entire stream into a pooled byte array, returning (data, valid length).
    /// <para>将整个流读取到池化字节数组中,返回 (数据, 有效长度)。</para>
    /// </summary>
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