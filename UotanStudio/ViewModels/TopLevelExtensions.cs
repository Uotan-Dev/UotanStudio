using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>提供对当前 TopLevel（主窗口或顶层视图）的访问辅助。</para>
/// Provides access helpers to the current TopLevel (main window or top-level view).
/// </summary>
public static class TopLevelExtensions
{
    /// <summary>
    /// <para>获取应用程序当前的 TopLevel 控件，若不可用则返回 null。</para>
    /// Gets the current application TopLevel control, or null when unavailable.
    /// </summary>
    public static TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window)
        {
            return TopLevel.GetTopLevel(window);
        }

        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView
            && singleView.MainView is { } mainView)
        {
            return TopLevel.GetTopLevel(mainView);
        }

        return null;
    }
}
