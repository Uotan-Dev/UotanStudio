using System.Buffers;

namespace UotanStudio.Media.Decoding;

/// <summary>
/// A decoded video frame in NV12 pixel format, with the luma and chroma planes
/// stored contiguously in a single pooled buffer.
/// <para>NV12 像素格式的解码帧：亮度与色度平面连续存放于单个池化缓冲区中。</para>
/// </summary>
/// <remarks>
/// <para>
/// NV12 layout: <c>Y plane (width × height)</c> followed by
/// <c>interleaved UV plane (width × height / 2)</c>. Both planes share the same
/// stride; the UV plane covers the full stride width even for odd widths.
/// </para>
/// <para>
/// NV12 布局：<c>Y 平面（width × height）</c>后跟<c>交错 UV 平面（width × height / 2）</c>。
/// 两个平面共用同一 stride；即使宽度为奇数，UV 平面也按完整 stride 宽度存储。
/// </para>
/// <para>
/// Instances own the pooled buffer and must be disposed exactly once by the
/// consumer. Call <see cref="Dispose"/> promptly after converting or copying
/// the pixels, because a decode loop may allocate tens of buffers per second.
/// </para>
/// <para>
/// 实例持有池化缓冲区，必须由消费方恰好释放一次。由于解码循环每秒可能分配
/// 数十个缓冲区，转换或复制像素后应立即调用 <see cref="Dispose"/>。
/// </para>
/// </remarks>
public sealed class DecodedVideoFrame : IDisposable
{
    private byte[]? _buffer;

    /// <summary>Creates a frame that takes ownership of an existing pooled buffer.</summary>
    /// <summary>创建一个接管现有池化缓冲区所有权的帧。</summary>
    /// <param name="buffer">The pooled buffer holding the NV12 planes. 持有 NV12 平面的池化缓冲区。</param>
    /// <param name="length">The number of valid bytes in <paramref name="buffer"/>. <paramref name="buffer"/> 中的有效字节数。</param>
    /// <param name="width">Frame width in pixels. 帧宽度（像素）。</param>
    /// <param name="height">Frame height in pixels. 帧高度（像素）。</param>
    /// <param name="stride">
    /// Row pitch in bytes of the NV12 buffer (may exceed width for alignment).
    /// NV12 缓冲区的行距（字节），为对齐可能大于宽度。
    /// </param>
    /// <param name="timestampTicks">Presentation timestamp in ticks. 显示时间戳（tick）。</param>
    public DecodedVideoFrame(byte[] buffer, int length, int width, int height, int stride, long timestampTicks)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (length < 0 || length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        int required = VideoFrameLimits.Nv12Length(width, height, stride);
        if (length < required)
        {
            throw new ArgumentException("The buffer does not contain complete NV12 planes.", nameof(length));
        }

        _buffer = buffer;
        Length = length;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampTicks = timestampTicks;
    }

    /// <summary>Gets the number of valid bytes in <see cref="Buffer"/>.</summary>
    /// <summary>获取 <see cref="Buffer"/> 中的有效字节数。</summary>
    public int Length { get; }

    /// <summary>Gets the frame width in pixels.</summary>
    /// <summary>获取帧宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>Gets the frame height in pixels.</summary>
    /// <summary>获取帧高度（像素）。</summary>
    public int Height { get; }

    /// <summary>Gets the row pitch in bytes of the NV12 buffer.</summary>
    /// <summary>获取 NV12 缓冲区的行距（字节）。</summary>
    public int Stride { get; }

    /// <summary>Gets the presentation timestamp in ticks.</summary>
    /// <summary>获取显示时间戳（tick）。</summary>
    public long TimestampTicks { get; }

    /// <summary>Gets the buffer holding the NV12 planes (valid range <c>[0, Length)</c>).</summary>
    /// <summary>获取持有 NV12 平面的缓冲区（有效范围 <c>[0, Length)</c>）。</summary>
    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(DecodedVideoFrame));

    /// <summary>
    /// Returns the valid portion of the NV12 buffer. Throws if disposed.
    /// </summary>
    /// <summary>
    /// 返回 NV12 缓冲区的有效部分；已释放时抛出异常。
    /// </summary>
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    /// <inheritdoc />
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
