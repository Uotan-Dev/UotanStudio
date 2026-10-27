using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.VideoToolbox;

/// <summary>
/// A decoded BGRA frame that stays on the GPU: the pixel buffer is IOSurface-backed and presentation
/// imports the surface without a CPU download. The decoder retains the pixel buffer at delivery;
/// VideoToolbox's decoder pool cannot recycle it until <see cref="MarkPresented"/> reports that the
/// compositor finished the update.
/// <para>留在 GPU 上的 BGRA 解码帧：像素缓冲由 IOSurface 支撑，呈现时直接导入表面而无需 CPU 下载。
/// 解码器在交付时保留像素缓冲；在 <see cref="MarkPresented"/> 报告合成器完成更新之前，
/// VideoToolbox 的解码缓冲池无法回收它。</para>
/// </summary>
/// <remarks>
/// Delivered through <see cref="VideoToolboxVideoDecoder.IosurfaceFrameCallback"/> instead of the generic
/// NV12 frame callback. There is no dispose step: the presenter calls <see cref="MarkPresented"/> in a
/// finally clause whether the update succeeded or not, which releases the retained pixel buffer.
/// <para>经 <see cref="VideoToolboxVideoDecoder.IosurfaceFrameCallback"/> 而非通用 NV12 帧回调交付。
/// 本类型没有释放步骤：无论更新成败，呈现器都在 finally 中调用 <see cref="MarkPresented"/>，
/// 由其释放被保留的像素缓冲。</para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class IosurfaceVideoFrame
{
    private readonly Action _release;
    private int _released;

    internal IosurfaceVideoFrame(nint pixelBuffer, nint iosurface, int width, int height, long timestampTicks, Action release)
    {
        PixelBuffer = pixelBuffer;
        Iosurface = iosurface;
        Width = width;
        Height = height;
        TimestampTicks = timestampTicks;
        _release = release;
    }

    /// <summary>
    /// Gets the retained IOSurface-backed CVPixelBuffer handle.
    /// <para>获取被保留的 IOSurface 支撑 CVPixelBuffer 句柄。</para>
    /// </summary>
    /// <value>Valid until <see cref="MarkPresented"/>. <para>在 <see cref="MarkPresented"/> 之前有效。</para></value>
    public nint PixelBuffer { get; }

    /// <summary>
    /// Gets the IOSurface handle for <c>KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef</c> imports.
    /// <para>获取 IOSurface 句柄，用于 <c>KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef</c> 导入。</para>
    /// </summary>
    /// <value>Valid until <see cref="MarkPresented"/>. <para>在 <see cref="MarkPresented"/> 之前有效。</para></value>
    public nint Iosurface { get; }

    /// <summary>Gets the visible frame width in pixels. <para>获取可见帧宽度（像素）。</para></summary>
    public int Width { get; }

    /// <summary>Gets the visible frame height in pixels. <para>获取可见帧高度（像素）。</para></summary>
    public int Height { get; }

    /// <summary>Gets the presentation timestamp in TimeSpan ticks. <para>获取以 TimeSpan tick 计的显示时间戳。</para></summary>
    public long TimestampTicks { get; }

    /// <summary>
    /// Reports that this frame left the presentation path (update completed, failed, or skipped).
    /// <para>报告本帧已离开呈现路径（更新完成、失败或被跳过）。</para>
    /// </summary>
    public void MarkPresented()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _release();
    }
}