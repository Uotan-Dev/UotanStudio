using Avalonia;

namespace UotanStudio.Views.Editors;

/// <summary>Maps image-local logical coordinates through centered Uniform stretching to video pixels.</summary>
internal static class ScreenMirrorCoordinates
{
    internal static bool TryMap(Point local, Size bounds, int width, int height, bool clamp, out int x, out int y)
    {
        x = y = 0;
        if (width <= 0 || height <= 0 || bounds.Width <= 0 || bounds.Height <= 0
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || !double.IsFinite(local.X) || !double.IsFinite(local.Y)) return false;
        double scale = Math.Min(bounds.Width / width, bounds.Height / height);
        double px = (local.X - (bounds.Width - width * scale) / 2) / scale;
        double py = (local.Y - (bounds.Height - height * scale) / 2) / scale;
        // Reject before conversion: truncating a negative fraction would otherwise hit pixel zero.
        if (!clamp && (px < 0 || py < 0 || px >= width || py >= height)) return false;
        x = (int)Math.Clamp(px, 0, width - 1);
        y = (int)Math.Clamp(py, 0, height - 1);
        return true;
    }
}
