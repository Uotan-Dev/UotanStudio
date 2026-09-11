using System.Buffers;

namespace UotanStudio.Media.Decoding;

/// <summary>
/// A decoded video frame in NV12 pixel format, with the luma and chroma planes
/// stored contiguously in one buffer, pooled or borrowed from the decoder.
/// <para>NV12 像素格式的解码帧：亮度与色度平面连续存放于单个池化或解码器借用缓冲区中。</para>
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
/// Public constructors take ownership of pooled buffers. Decoder callbacks may
/// borrow session storage; the decoder disposes each frame when the callback
/// returns. Copy or convert pixels within that callback. Dispose is idempotent.
/// </para>
/// <para>
/// 公共构造函数接管池化缓冲区。解码回调可借用会话存储；解码器在回调返回后
/// 释放帧，消费方须在回调内复制或转换像素。Dispose 可重复调用。
/// </para>
/// </remarks>
public class DecodedVideoFrame : IDisposable
{
    /// <summary>Reported/effective YUV matrix, or Auto when unreported. 已报告或实际采用的色彩矩阵。</summary>
    public VideoColorMatrix ColorMatrix { get; init; }
    /// <summary>Reported/effective YUV range, or Auto when unreported. 已报告或实际采用的色彩范围。</summary>
    public VideoColorRange ColorRange { get; init; }
    private byte[]? _buffer;

    /// <summary>False only for decoder-owned storage borrowed until the callback returns.</summary>
    internal bool ReturnBufferToPool { get; init; } = true;

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
    /// <param name="skipNv12Validation">
    /// Skips the NV12 plane-length validation for derived frame layouts (e.g. BGRA).
    /// 对派生帧布局（如 BGRA）跳过 NV12 平面长度校验。
    /// </param>
    protected DecodedVideoFrame(byte[] buffer, int length, int width, int height, int stride, long timestampTicks, bool skipNv12Validation)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (length < 0 || length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (!skipNv12Validation)
        {
            int required = VideoFrameLimits.Nv12Length(width, height, stride);
            if (length < required)
            {
                throw new ArgumentException("The buffer does not contain complete NV12 planes.", nameof(length));
            }
        }

        _buffer = buffer;
        Length = length;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampTicks = timestampTicks;
    }

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
        if (buffer is not null && ReturnBufferToPool)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
