using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>
/// A decoded BGRA frame that stays on the GPU: the video processor wrote it into a shared D3D11 texture
/// guarded by a keyed mutex, and presentation imports the texture without a CPU download. The producer
/// must not touch the texture again until <see cref="MarkPresented"/> reports that the compositor finished
/// the update which acquired key 1 and released key 0.
/// <para>留在 GPU 上的 BGRA 解码帧：视频处理器把它写入由键互锁守护的共享 D3D11 纹理，呈现时直接导入纹理，
/// 无需 CPU 下载。在 <see cref="MarkPresented"/> 报告合成器完成"获取键 1、释放键 0"的更新之前，
/// 生产方不得再触碰该纹理。</para>
/// </summary>
/// <remarks>
/// <para>
/// Delivered through <see cref="MediaFoundationGpuVideoDecoder.GpuExportFrameCallback"/> instead of the
/// generic NV12 frame callback. Unlike <see cref="Acceleration.Linux.VaDmaBufVideoFrame"/> there is no
/// dispose step: the presenter calls <see cref="MarkPresented"/> in a finally clause whether the update
/// succeeded or not. A failed update cannot wedge the producer — if the compositor died holding the mutex,
/// the next acquire reports WAIT_ABANDONED, which the decoder normalizes back to key 0.
/// </para>
/// <para>
/// 经 <see cref="MediaFoundationGpuVideoDecoder.GpuExportFrameCallback"/> 而非通用 NV12 帧回调交付。
/// 与 <see cref="Acceleration.Linux.VaDmaBufVideoFrame"/> 不同，本类型没有释放步骤：无论更新成败，
/// 呈现器都在 finally 中调用 <see cref="MarkPresented"/>。失败的更新不会卡死生产方——若合成器持有互锁时
/// 设备死亡，下一次获取会返回 WAIT_ABANDONED，解码器会将其归一化回键 0。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class D3D11SharedVideoFrame
{
    private readonly ExportSlotRing<MediaFoundationExportSlot> _ring;
    private readonly MediaFoundationExportSlot _slot;

    internal D3D11SharedVideoFrame(ExportSlotRing<MediaFoundationExportSlot> ring, MediaFoundationExportSlot slot,
        nint sharedHandle, int width, int height, long timestampTicks, VideoColorMatrix colorMatrix, VideoColorRange colorRange)
    {
        _ring = ring;
        _slot = slot;
        SharedHandle = sharedHandle;
        Width = width;
        Height = height;
        TimestampTicks = timestampTicks;
        ColorMatrix = colorMatrix;
        ColorRange = colorRange;
    }

    /// <summary>
    /// Gets the process-global handle of the shared BGRA texture for
    /// <c>KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle</c> imports.
    /// <para>获取共享 BGRA 纹理的进程全局句柄，用于
    /// <c>KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle</c> 导入。</para>
    /// </summary>
    /// <value>The handle stays valid until the owning decoder is reset or disposed.
    /// <para>句柄在所属解码器重置或释放前保持有效。</para></value>
    public nint SharedHandle { get; }

    /// <summary>Gets the visible frame width in pixels. <para>获取可见帧宽度（像素）。</para></summary>
    public int Width { get; }

    /// <summary>Gets the visible frame height in pixels. <para>获取可见帧高度（像素）。</para></summary>
    public int Height { get; }

    /// <summary>Gets the presentation timestamp in TimeSpan ticks. <para>获取以 TimeSpan tick 计的显示时间戳。</para></summary>
    public long TimestampTicks { get; }

    /// <summary>Gets the effective YUV matrix the conversion used. <para>获取转换所用的有效 YUV 矩阵。</para></summary>
    public VideoColorMatrix ColorMatrix { get; }

    /// <summary>Gets the effective YUV range the conversion used. <para>获取转换所用的有效 YUV 范围。</para></summary>
    public VideoColorRange ColorRange { get; }

    /// <summary>
    /// Reports that this frame left the presentation path (update completed, failed, or skipped); the
    /// texture becomes rentable again and the call is idempotent and safe from any thread. Presenters must
    /// call it exactly once per frame, in a finally clause.
    /// <para>报告本帧已离开呈现路径（更新完成、失败或被跳过）；纹理重新可租用。调用幂等且可在任意线程执行。
    /// 呈现器必须对每帧在 finally 中恰好调用一次。</para>
    /// </summary>
    public void MarkPresented() => _ring.MarkPresented(_slot);
}