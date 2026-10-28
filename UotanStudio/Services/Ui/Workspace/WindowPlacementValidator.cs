using Avalonia;

namespace UotanStudio.Services.Ui.Workspace;

/// <summary>
/// Decides whether a persisted main-window size and position leave a usable visible part of the window on a screen.
/// <para>判断持久化的主窗口尺寸与位置是否在屏幕上留下可用的窗口可视部分。</para>
/// </summary>
internal static class WindowPlacementValidator
{
    /// <summary>
    /// Minimum visible width and height in pixels a restored window must keep on one screen.
    /// <para>恢复的窗口必须在单个屏幕上保留的最小可视宽度与高度（像素）。</para>
    /// </summary>
    internal const int MinimumVisibleExtent = 48;

    /// <summary>
    /// Reports whether the window rectangle formed by the supplied geometry and scaling overlaps one working area
    /// by at least <see cref="MinimumVisibleExtent"/> pixels in both dimensions.
    /// <para>判定由给定几何与缩放构成的窗口矩形是否与某个工作区在宽、高两个方向上都至少重叠
    /// <see cref="MinimumVisibleExtent"/> 像素。</para>
    /// </summary>
    /// <param name="x">Window X position in pixels. <para>窗口 X 坐标（像素）。</para></param>
    /// <param name="y">Window Y position in pixels. <para>窗口 Y 坐标（像素）。</para></param>
    /// <param name="width">Window width in device-independent pixels. <para>窗口宽度（设备无关像素）。</para></param>
    /// <param name="height">Window height in device-independent pixels. <para>窗口高度（设备无关像素）。</para></param>
    /// <param name="workingAreas">Screen working areas in pixels; an empty list reports <see langword="true"/>
    /// because the geometry cannot be judged. <para>屏幕工作区（像素）；列表为空时无法判断，返回
    /// <see langword="true"/>。</para></param>
    /// <param name="scaling">Scaling factor converting the size to pixels; values outside 0.1–4 use 1.
    /// <para>把尺寸换算为像素的缩放因子；超出 0.1–4 时按 1 处理。</para></param>
    /// <returns><see langword="true"/> when a usable visible part remains. <para>仍保留可用可视部分时返回
    /// <see langword="true"/>。</para></returns>
    internal static bool HasVisibleArea(int x, int y, double width, double height,
        IReadOnlyList<PixelRect> workingAreas, double scaling)
    {
        if (workingAreas.Count == 0)
        {
            return true;
        }

        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return true;
        }

        var factor = scaling is >= 0.1 and <= 4 ? scaling : 1;
        var bounds = new PixelRect(x, y,
            Math.Max(1, (int)Math.Round(width * factor)),
            Math.Max(1, (int)Math.Round(height * factor)));
        foreach (var area in workingAreas)
        {
            var visible = area.Intersect(bounds);
            if (visible.Width >= MinimumVisibleExtent && visible.Height >= MinimumVisibleExtent)
            {
                return true;
            }
        }

        return false;
    }
}