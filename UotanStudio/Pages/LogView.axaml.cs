using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages;

/// <summary>
/// <para>全局日志面板视图，显示统一日志服务的全部条目并提供清空操作。</para>
/// Global log panel view, displaying all entries from the unified log service and providing a clear action.
/// </summary>
public partial class LogView : UserControl
{
    private ScrollViewer? _logScroller;

    /// <summary>
    /// <para>初始化全局日志面板视图的新实例。</para>
    /// Initializes a new instance of the LogView.
    /// </summary>
    public LogView()
    {
        InitializeComponent();
        LogList.Loaded += (_, _) => AttachScroller();
    }

    /// <summary>
    /// <para>解析 ListBox 内部滚动容器并订阅滚动事件。</para>
    /// Resolves the ListBox internal scroll container and subscribes to its scroll events.
    /// </summary>
    private void AttachScroller()
    {
        if (_logScroller is null)
        {
            _logScroller = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_logScroller is not null)
            {
                _logScroller.ScrollChanged += (_, _) => OnScrollChanged();
            }
        }

        if (DataContext is LogViewModel vm && !_subscribed)
        {
            vm.Entries.CollectionChanged += (_, _) => FollowAutoScroll();
            _subscribed = true;
        }

        FollowAutoScroll();
    }

    private bool _subscribed;

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
        ScrollToEnd();
    }

    /// <summary>
    /// <para>滚动变化时，若自动滚动开启则跟随至最新条目。</para>
    /// On scroll change, follows the latest entry when auto-scroll is enabled.
    /// </summary>
    private void OnScrollChanged()
    {
        if (DataContext is LogViewModel { AutoScroll: true })
        {
            ScrollToEnd();
        }
    }

    /// <summary>
    /// <para>新增条目时自动跟随滚动到底部（若开启自动滚动）。</para>
    /// Follows the latest entry on new items when auto-scroll is enabled.
    /// </summary>
    private void FollowAutoScroll()
    {
        if (DataContext is LogViewModel { AutoScroll: true })
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd() => _logScroller?.ScrollToEnd();
}