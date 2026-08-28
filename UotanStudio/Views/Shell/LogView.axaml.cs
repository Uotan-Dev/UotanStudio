using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>全局日志面板视图，显示统一日志服务的全部条目并提供清空、导出与点击复制操作。</para>
/// Global log panel view, displaying all entries from the unified log service and providing clear, export, and click-to-copy actions.
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
        // 路由事件订阅内部滚动器的滚动变化：无需先解析内部 ScrollViewer 实例，
        // 且能统一在用户滚轮滚动与新条目追加时同步自动滚动状态。
        // Subscribe to the inner scroller's scroll changes via the routed event: no need to
        // resolve the internal ScrollViewer instance first, and both wheel scrolling and new
        // entry appends funnel through one handler that syncs the auto-scroll state.
        LogList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
    }

    /// <summary>
    /// <para>解析 ListBox 内部滚动容器并订阅条目集合变更。</para>
    /// Resolves the ListBox internal scroll container and subscribes to entry collection changes.
    /// </summary>
    private void AttachScroller()
    {
        if (_logScroller is null)
        {
            _logScroller = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
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
    /// <para>导出按钮点击事件，弹出保存对话框并将当前日志集合写入文本文件。</para>
    /// Export button click handler, opening a save dialog and writing the current log collection to a text file.
    /// </summary>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        await ExportLogsAsync();
    }

    /// <summary>
    /// <para>导出日志到用户选择的文件。</para>
    /// Exports logs to a user-chosen file.
    /// </summary>
    private async Task ExportLogsAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        if (DataContext is not LogViewModel vm || vm.Entries.Count == 0)
        {
            return;
        }

        var suggestedName = $"UotanStudio_Log_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageService.Instance.GetString("Log_Export"),
            DefaultExtension = "log",
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Log files")
                {
                    Patterns = new[] { "*.log", "*.txt" },
                },
            },
        });

        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            foreach (var entry in vm.Entries)
            {
                await writer.WriteLineAsync(
                    $"{entry.Timestamp} [{entry.Level}] {entry.Source} - {entry.Message}");
            }
            await writer.FlushAsync();

            LogService.Instance.Write(LogLevel.Info, "LogView",
                LanguageService.Instance.GetString("Log_ExportDone", file.Name));
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "LogView",
                LanguageService.Instance.GetString("Log_ExportFailed", ex.Message));
        }
    }

    /// <summary>
    /// <para>日志条目点击事件，将该行日志文本复制到剪贴板。</para>
    /// Log entry click handler, copying the clicked row's text to the clipboard.
    /// </summary>
    private async void OnEntryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not StackPanel panel)
        {
            return;
        }

        if (panel.DataContext is not LogViewModel.LogEntryViewModel entry)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed == false)
        {
            return;
        }

        var text = $"{entry.Timestamp} [{entry.Level}] {entry.Source} - {entry.Message}";
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            LogService.Instance.Write(LogLevel.Debug, "LogView",
                LanguageService.Instance.GetString("Log_CopyDone"));
        }
        catch
        {
            // 剪贴板写入失败时不打扰用户
        }
    }

    /// <summary>
    /// <para>滚动变化时同步自动滚动状态：用户滚到列表底部时恢复自动滚动，
    /// 向上翻阅历史时暂停自动滚动（否则每次滚动都会被拉回底部，表现为滚轮无效）。</para>
    /// Syncs the auto-scroll state on scroll changes: restores auto-scroll when the user reaches
    /// the bottom, and pauses it while browsing history upwards (otherwise every wheel tick would
    /// snap back to the bottom, making the wheel appear dead).
    /// </summary>
    private void OnScrollChanged(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewModel vm || _logScroller is null)
        {
            return;
        }

        // 容差 4px：列表不满一屏或滚动器尚未布局时也视为在底部。
        // A 4px tolerance: an undersized list or an unlaid-out scroller still counts as bottom.
        var atBottom = _logScroller.Offset.Y + _logScroller.Viewport.Height >= _logScroller.Extent.Height - 4;
        if (vm.AutoScroll != atBottom)
        {
            vm.AutoScroll = atBottom;
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