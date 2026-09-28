using System.Runtime.Versioning;
using UotanStudio.Media.Decoding;

namespace UotanStudio.Media.Acceleration.Linux;

/// <summary>
/// A decoded video frame that stays on the GPU: the VA surface is exported as
/// read-only separate DRM PRIME layers and presented without a CPU download.
/// Disposing the frame closes the exported descriptors and releases the surface
/// back to the decoder pool; presenters must retain it until the compositor is
/// done sampling.
/// <para>留在 GPU 上的解码帧：VA 表面以只读 SEPARATE_LAYERS DRM PRIME 导出，呈现时无需 CPU 下载。
/// 释放该帧会关闭导出的描述符并把表面归还解码器池；呈现器必须持有它直到合成器完成采样。</para>
/// </summary>
/// <remarks>
/// <para>
/// Delivered through <see cref="VaApiVideoDecoder.DmabufFrameCallback"/> instead of the generic
/// NV12 frame callback. Disposal is thread-safe and no-ops after the owning decoder reset or
/// closed; the color metadata reflects the stream unless overridden by options.
/// </para>
/// <para>
/// 经 <see cref="VaApiVideoDecoder.DmabufFrameCallback"/> 而非通用 NV12 帧回调交付。释放是线程安全的，
/// 在所属解码器重置或关闭后自动变为空操作；色彩元数据反映码流值，除非被选项覆盖。
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class VaDmaBufVideoFrame : IDisposable
{
    private readonly Action<int> _release;
    private readonly int _generation;
    private DmaBufSurface? _surface;
    private int _disposed;

    internal VaDmaBufVideoFrame(DmaBufSurface surface, int width, int height, VideoColorMatrix matrix, VideoColorRange range, long timestampTicks, Action<int> release, int generation)
    {
        _surface = surface;
        _release = release;
        _generation = generation;
        Width = width;
        Height = height;
        ColorMatrix = matrix;
        ColorRange = range;
        TimestampTicks = timestampTicks;
    }

    /// <summary>
    /// Gets the exported DMA-BUF descriptor and its owned file descriptors.
    /// <para>获取导出的 DMA-BUF 描述符及其持有的文件描述符。</para>
    /// </summary>
    /// <value>
    /// The surface snapshot; separate-layer exports expose two layers for NV12.
    /// <para>表面快照；NV12 的 SEPARATE_LAYERS 导出暴露两个层。</para>
    /// </value>
    /// <exception cref="ObjectDisposedException">The frame has been disposed.
    /// <para>该帧已被释放。</para></exception>
    public DmaBufSurface Surface => _surface ?? throw new ObjectDisposedException(nameof(VaDmaBufVideoFrame));

    /// <summary>
    /// Gets the visible frame width in pixels.
    /// <para>获取可见帧宽度（像素）。</para>
    /// </summary>
    /// <value>The decoded width. <para>解码宽度。</para></value>
    public int Width { get; }

    /// <summary>
    /// Gets the visible frame height in pixels.
    /// <para>获取可见帧高度（像素）。</para>
    /// </summary>
    /// <value>The decoded height. <para>解码高度。</para></value>
    public int Height { get; }

    /// <summary>
    /// Gets the presentation timestamp in TimeSpan ticks.
    /// <para>获取以 TimeSpan tick 计的显示时间戳。</para>
    /// </summary>
    /// <value>The timestamp carried from the decoded access unit.
    /// <para>从解码访问单元一路传递的时间戳。</para></value>
    public long TimestampTicks { get; }

    /// <summary>
    /// Gets the effective YUV matrix reported by the decoder or forced by options.
    /// <para>获取由解码器报告或由选项强制使用的 YUV 矩阵。</para>
    /// </summary>
    /// <value>The color matrix the presenter should assume.
    /// <para>呈现器应当假定的色彩矩阵。</para></value>
    public VideoColorMatrix ColorMatrix { get; }

    /// <summary>
    /// Gets the effective YUV range reported by the decoder or forced by options.
    /// <para>获取由解码器报告或由选项强制使用的 YUV 范围。</para>
    /// </summary>
    /// <value>The color range the presenter should assume.
    /// <para>呈现器应当假定的色彩范围。</para></value>
    public VideoColorRange ColorRange { get; }

    /// <summary>
    /// Closes the exported descriptors and releases the VA surface back to the decoder pool;
    /// safe to call more than once and from any thread.
    /// <para>关闭导出的描述符并把 VA 表面归还解码器池；可重复调用，且可在任意线程调用。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _surface, null)?.Dispose();
        _release(_generation);
    }
}