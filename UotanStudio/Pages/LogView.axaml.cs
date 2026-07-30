using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages;

/// <summary>
/// <para>全局日志面板视图，显示统一日志服务的全部条目并提供清空操作。</para>
/// Global log panel view, displaying all entries from the unified log service and providing a clear action.
/// </summary>
public partial class LogView : UserControl
{
    /// <summary>
    /// <para>初始化全局日志面板视图的新实例。</para>
    /// Initializes a new instance of the LogView.
    /// </summary>
    public LogView()
    {
        InitializeComponent();
        LogScroll.ScrollChanged += OnScrollChanged;
    }

    /// <summary>
    /// <para>清空按钮点击事件，清空当前日志集合并滚动到底部。</para>
    /// Clear button click handler, clearing the current log collection and scrolling to bottom.
    /// </summary>
    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LogViewModel vm)
        {
            vm.Clear();
        }
        LogScroll.ScrollToEnd();
    }

    /// <summary>
    /// <para>滚动变化时，若自动滚动开启则跟随至最新条目。</para>
    /// On scroll change, follows the latest entry when auto-scroll is enabled.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is LogViewModel { AutoScroll: true })
        {
            LogScroll.ScrollToEnd();
        }
    }
}
