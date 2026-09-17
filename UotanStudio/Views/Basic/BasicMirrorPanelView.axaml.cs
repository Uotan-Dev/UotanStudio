using Avalonia.Controls;

namespace UotanStudio.Views.Basic;

/// <summary>
/// <para>右侧固定面板的「投屏」标签视图：画面区域固定为 20:9 手机屏比例（竖屏 9:20）并随面板大小重新计算。</para>
/// The fixed right panel's "mirror" tab: the picture area keeps the 20:9 phone shape (portrait 9:20) and is
/// recomputed when the panel is resized.
/// </summary>
public partial class BasicMirrorPanelView : UserControl
{
    /// <summary>
    /// <para>手机屏比例（20:9）的横向单位；竖屏时宽 9、高 20。页面外壳也用这组常量推算右侧面板宽度。</para>
    /// Horizontal units of the 20:9 phone aspect; portrait means 9 wide by 20 tall. The page shell reuses these
    /// constants to derive the right panel's width.
    /// </summary>
    internal const double ScreenWidthUnits = 9;

    /// <summary><para>手机屏比例（20:9）的纵向单位。</para>Vertical units of the 20:9 phone aspect.</summary>
    internal const double ScreenHeightUnits = 20;

    /// <summary>Initializes the mirror panel. 初始化投屏面板。</summary>
    public BasicMirrorPanelView()
    {
        InitializeComponent();
        MirrorHost.SizeChanged += (_, _) => UpdateSurfaceSize();
        UpdateSurfaceSize();
    }

    /// <summary>
    /// <para>把画面区域调整为可用空间内最大的 9:20 矩形，并保持居中。</para>
    /// Fits the picture area to the largest 9:20 rectangle inside the available space and keeps it centred.
    /// </summary>
    private void UpdateSurfaceSize()
    {
        var available = MirrorHost.Bounds.Size;
        if (available.Width <= 0 || available.Height <= 0)
        {
            return;
        }

        var width = Math.Min(available.Width, available.Height * (ScreenWidthUnits / ScreenHeightUnits));
        MirrorSurface.Width = width;
        MirrorSurface.Height = width * (ScreenHeightUnits / ScreenWidthUnits);
    }
}