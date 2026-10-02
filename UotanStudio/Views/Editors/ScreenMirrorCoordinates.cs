using Avalonia;
using UotanStudio.Services.Mirroring;

namespace UotanStudio.Views.Editors;

/// <summary>Maps image-local logical coordinates through centered Uniform stretching to video pixels.</summary>
internal static class ScreenMirrorCoordinates
{
    /// <summary>
    /// The server drops touch/scroll events whose position size differs from the video size it announced in the
    /// codec metadata; the decoded frame size is not authoritative because decoders may report aligned sizes.
    /// </summary>
    internal static PixelSize ResolveInjectionSize(ScrcpyVideoMetadata? announced, PixelSize frame)
    {
        if (announced is not { Width: > 0, Height: > 0 } || frame is not { Width: > 0, Height: > 0 })
        {
            return frame;
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
        double scale = Math.Min(bounds.Width / width, bounds.Height / height);
        double px = (local.X - (bounds.Width - width * scale) / 2) / scale;
        double py = (local.Y - (bounds.Height - height * scale) / 2) / scale;

        if (!clamp && (px < 0 || py < 0 || px >= width || py >= height)) return false;
        x = (int)Math.Clamp(px, 0, width - 1);
        y = (int)Math.Clamp(py, 0, height - 1);
        return true;
    }
}