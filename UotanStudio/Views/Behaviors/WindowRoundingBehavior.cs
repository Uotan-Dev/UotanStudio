using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UotanStudio.Views.Behaviors;

/// <summary>
/// Rounds the corners of a window on Linux, where drawn decorations replace the window manager's frame.
/// <para>在 Linux 上为窗口加圆角：自绘装饰接管窗口边框后，原本由窗口管理器绘制的圆角会消失。</para>
/// </summary>
/// <remarks>
/// Rounding is only applied when the compositor actually granted a transparent surface
/// (<see cref="TopLevel.ActualTransparencyLevel"/> equals <see cref="WindowTransparencyLevel.Transparent"/>).
/// Without a transparent surface (for example under an uncomposited X11 window manager), clipping would
/// expose opaque black triangles in the four corners, so windows cleanly fall back to square corners.
/// When active, the behavior toggles <see cref="RoundedClassName"/> so the decoration border rounds its own
/// stroke along the curve, while clipping the window visual keeps client content and the title bar inside
/// the rounded silhouette.
/// <para>仅当合成器实际提供了透明表面（<see cref="TopLevel.ActualTransparencyLevel"/> 为
/// <see cref="WindowTransparencyLevel.Transparent"/>）时才启用圆角；无合成器 X11 等拿不到透明表面的环境会
/// 自动回退直角，避免四角出现黑块。启用时同步切换 <see cref="RoundedClassName"/> 样式类，使自绘装饰边框沿
/// 圆角弧线闭合，同时裁剪窗口可视对象以防客户区内容和标题栏溢出圆角外。</para>
/// </remarks>
public static class WindowRoundingBehavior
{
    /// <summary>The corner radius applied to rounded windows.<para>圆角窗口使用的半径。</para></summary>
    internal const double CornerRadius = 8;

    /// <summary>
    /// The style class toggled on a window while Linux corner rounding is active.
    /// <para>Linux 圆角生效时在窗口上切换的样式类名。</para>
    /// </summary>
    internal const string RoundedClassName = "linux-rounded";

    private static readonly ConditionalWeakTable<Window, Tracker> Trackers = new();

    /// <summary>
    /// Starts rounding the given window and keeps it in sync with resizes, state changes, and transparency level.
    /// <para>开始为指定窗口加圆角，并随窗口尺寸、状态与透明层级变化保持同步。</para>
    /// </summary>
    /// <param name="window">The window to round.<para>需要加圆角的窗口。</para></param>
    public static void Attach(Window window) =>
        Trackers.GetValue(window, static owner => new Tracker(owner));

    private sealed class Tracker
    {
        private readonly Window _window;
        private Size _lastClippedSize;

        internal Tracker(Window window)
        {
            _window = window;
            _window.SizeChanged += OnSizeChanged;
            _window.PropertyChanged += OnPropertyChanged;
            _window.Closed += OnClosed;
            Update();
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Update();

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Window.WindowStateProperty
                || e.Property == TopLevel.ActualTransparencyLevelProperty)
            {
                Update();
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _window.SizeChanged -= OnSizeChanged;
            _window.PropertyChanged -= OnPropertyChanged;
            _window.Closed -= OnClosed;
        }

        private void Update()
        {
            // 最大化、全屏或未取得透明表面（如无合成器 X11）时回退直角，避免切掉内容或露出黑角。
            // Fall back to square corners when maximised, fullscreen, or without a transparent surface
            // (e.g. uncomposited X11), avoiding clipped content or opaque black corners.
            if (_window.WindowState != WindowState.Normal
                || _window.ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
            {
                _window.Classes.Set(RoundedClassName, false);
                _window.Clip = null;
                _lastClippedSize = default;
                return;
            }

            var size = _window.ClientSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            _window.Classes.Set(RoundedClassName, true);
            if (_window.Clip is not null && _lastClippedSize == size)
            {
                return;
            }

            _lastClippedSize = size;
            _window.Clip = new RectangleGeometry(new Rect(size), CornerRadius, CornerRadius);
        }
    }
}