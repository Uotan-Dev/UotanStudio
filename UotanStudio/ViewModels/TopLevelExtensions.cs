using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace UotanStudio.ViewModels;

/// <summary>
/// Provides access helpers to the current TopLevel (main window or top-level view).
/// <para>提供对当前 TopLevel（主窗口或顶层视图）的访问辅助。</para>
/// </summary>
public static class TopLevelExtensions
{
    /// <summary>
    /// Gets the current application TopLevel control, or null when unavailable.
    /// <para>获取应用程序当前的 TopLevel 控件，若不可用则返回 null。</para>
    /// </summary>
    public static TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window)
        {
            return TopLevel.GetTopLevel(window);
        }

        return null;
    }
}