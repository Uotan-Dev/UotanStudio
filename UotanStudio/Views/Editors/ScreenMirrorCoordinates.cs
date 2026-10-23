using Avalonia;
using UotanStudio.Mirroring;

namespace UotanStudio.Views.Editors;

/// <summary>Maps image-local logical coordinates through centered Uniform stretching to video pixels.<para>将图像内逻辑坐标经居中 Uniform 拉伸映射为视频像素坐标。</para></summary>
internal static class ScreenMirrorCoordinates
{
    /// <summary>
    /// Chooses the position size carried by injected touch and scroll events, preferring the video
    /// size announced in codec metadata when it matches the decoded frame's aspect.
    /// <para>为注入的触摸与滚动事件选择携带的位置尺寸；编码元数据公告的视频尺寸与解码帧宽高比一致时优先采用。</para>
    /// </summary>
    /// <param name="announced">Codec metadata carrying the announced video size, or null.<para>携带公告视频尺寸的编码元数据，可为 null。</para></param>
    /// <param name="frame">Size reported by the decoder for the current frame.<para>解码器报告的当前帧尺寸。</para></param>
    /// <returns>The pixel size to report as the event position.<para>作为事件位置上报的像素尺寸。</para></returns>
    internal static PixelSize ResolveInjectionSize(ScrcpyVideoMetadata? announced, PixelSize frame)
    {
        if (announced is not { Width: > 0, Height: > 0 })
        {
            return frame;
        }

        if (frame is not { Width: > 0, Height: > 0 })
        {
            return new PixelSize(announced.Width, announced.Height);
        }

        return (frame.Width > frame.Height) == (announced.Width > announced.Height)
            ? new PixelSize(announced.Width, announced.Height)
            : new PixelSize(announced.Height, announced.Width);
    }

    internal static bool TryMap(Point local, Size bounds, int width, int height, bool clamp, out int x, out int y)
    {
        x = y = 0;
        if (width <= 0 || height <= 0 || bounds.Width <= 0 || bounds.Height <= 0
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || !double.IsFinite(local.X) || !double.IsFinite(local.Y)) return false;
        var scale = Math.Min(bounds.Width / width, bounds.Height / height);
        var px = (local.X - (bounds.Width - width * scale) / 2) / scale;
        var py = (local.Y - (bounds.Height - height * scale) / 2) / scale;

        if (!clamp && (px < 0 || py < 0 || px >= width || py >= height)) return false;
        x = (int)Math.Clamp(px, 0, width - 1);
        y = (int)Math.Clamp(py, 0, height - 1);
        return true;
    }
}