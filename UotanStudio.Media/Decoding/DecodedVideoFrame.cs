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
    /// <summary>
    /// Reported/effective YUV matrix, or Auto when unreported.
    /// <para>已报告或实际采用的色彩矩阵；未报告时为 Auto。</para>
    /// </summary>
    /// <value>
    /// The color matrix the consumer should assume when converting this frame.
    /// <para>转换该帧时消费方应当假定的色彩矩阵。</para>
    /// </value>
    public VideoColorMatrix ColorMatrix { get; init; }
    /// <summary>
    /// Reported/effective YUV range, or Auto when unreported.
    /// <para>已报告或实际采用的色彩范围；未报告时为 Auto。</para>
    /// </summary>
    /// <value>
    /// The luma/chroma range the consumer should assume when converting this frame.
    /// <para>转换该帧时消费方应当假定的亮度/色度范围。</para>
    /// </value>
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

    /// <summary>
    /// Creates a frame that takes ownership of an existing pooled buffer.
    /// <para>创建一个接管现有池化缓冲区所有权的帧。</para>
    /// </summary>
    /// <param name="buffer">The pooled buffer holding the NV12 planes. <para>持有 NV12 平面的池化缓冲区。</para></param>
    /// <param name="length">The number of valid bytes in <paramref name="buffer"/>. <para><paramref name="buffer"/> 中的有效字节数。</para></param>
    /// <param name="width">Frame width in pixels. <para>帧宽度（像素）。</para></param>
    /// <param name="height">Frame height in pixels. <para>帧高度（像素）。</para></param>
    /// <param name="stride">
    /// Row pitch in bytes of the NV12 buffer (may exceed width for alignment).
    /// <para>NV12 缓冲区的行距（字节），为对齐可能大于宽度。</para>
    /// </param>
    /// <param name="timestampTicks">Presentation timestamp in ticks. <para>显示时间戳（tick）。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>. <para><paramref name="buffer"/> 为 <see langword="null"/> 时抛出。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative, exceeds <paramref name="buffer"/>, or the geometry is out of range. <para><paramref name="length"/> 为负、超出缓冲区，或几何参数超出范围时抛出。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="length"/> is smaller than the NV12 plane total. <para><paramref name="length"/> 小于 NV12 平面所需总字节数时抛出。</para></exception>
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

    /// <summary>
    /// Gets the number of valid bytes in <see cref="Buffer"/>.
    /// <para>获取 <see cref="Buffer"/> 中的有效字节数。</para>
    /// </summary>
    /// <value>
    /// Byte count of the usable frame data; the public constructor validates it against the NV12 plane total.
    /// <para>可用帧数据的字节数；公共构造函数会按 NV12 平面所需总字节数校验该值。</para>
    /// </value>
    public int Length { get; }

    /// <summary>
    /// Gets the frame width in pixels.
    /// <para>获取帧宽度（像素）。</para>
    /// </summary>
    /// <value>
    /// The width supplied at construction, used to locate pixels inside <see cref="Stride"/> rows.
    /// <para>构造时给定的宽度，用于在 <see cref="Stride"/> 行内定位像素。</para>
    /// </value>
    public int Width { get; }

    /// <summary>
    /// Gets the frame height in pixels.
    /// <para>获取帧高度（像素）。</para>
    /// </summary>
    /// <value>
    /// The height supplied at construction, covering the luma rows and half-height chroma rows.
    /// <para>构造时给定的高度，涵盖亮度行与半高的色度行。</para>
    /// </value>
    public int Height { get; }

    /// <summary>
    /// Gets the row pitch in bytes of the NV12 buffer.
    /// <para>获取 NV12 缓冲区的行距（字节）。</para>
    /// </summary>
    /// <value>
    /// Bytes per row; it may exceed <see cref="Width"/> for alignment and applies to both planes.
    /// <para>每行字节数；为对齐可能大于 <see cref="Width"/>，且同时适用于两个平面。</para>
    /// </value>
    public int Stride { get; }

    /// <summary>
    /// Gets the presentation timestamp in ticks.
    /// <para>获取显示时间戳（tick）。</para>
    /// </summary>
    /// <value>
    /// The timestamp carried through from the decoded access unit.
    /// <para>从解码访问单元一路传递而来的时间戳。</para>
    /// </value>
    public long TimestampTicks { get; }

    /// <summary>
    /// Gets the buffer holding the NV12 planes (valid range <c>[0, Length)</c>).
    /// <para>获取持有 NV12 平面的缓冲区（有效范围 <c>[0, Length)</c>）。</para>
    /// </summary>
    /// <value>
    /// The underlying array; read only <see cref="Length"/> bytes and never retain it after <see cref="Dispose"/>.
    /// <para>底层数组；只应读取 <see cref="Length"/> 个字节，且在 <see cref="Dispose"/> 之后不得继续持有。</para>
    /// </value>
    /// <exception cref="ObjectDisposedException">The frame has been disposed. <para>帧已释放时抛出。</para></exception>
    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(DecodedVideoFrame));

    /// <summary>
    /// Returns the valid portion of the NV12 buffer. Throws if disposed.
    /// <para>返回 NV12 缓冲区的有效部分；已释放时抛出异常。</para>
    /// </summary>
    /// <value>
    /// The span <c>[0, Length)</c> of <see cref="Buffer"/>, valid until the frame is disposed.
    /// <para><see cref="Buffer"/> 的 <c>[0, Length)</c> 区间；在帧释放前有效。</para>
    /// </value>
    /// <exception cref="ObjectDisposedException">The frame has been disposed. <para>帧已释放时抛出。</para></exception>
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    /// <summary>
    /// Releases the frame, returning its buffer to <see cref="ArrayPool{T}"/> unless the
    /// storage is decoder-owned; calling it more than once has no additional effect.
    /// <para>释放帧；除非存储归解码器所有，否则将缓冲区归还给 <see cref="ArrayPool{T}"/>。重复调用无额外效果。</para>
    /// </summary>
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null && ReturnBufferToPool)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}