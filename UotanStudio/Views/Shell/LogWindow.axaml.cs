using System.Collections.Specialized;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>全局日志独立窗口，按等级着色显示日志并提供筛选、清空、导出与文本选择复制。</para>
/// Standalone global log window with level-colored entries, filtering, clear, export, and selectable text.
/// </summary>
public partial class LogWindow : Window
{
    private static readonly LogViewModel SharedViewModel = new();
    // 记录每条日志的字符数和物理行数，淘汰旧日志时保留其余文本的选区与等级映射。
    // Track entry lengths and physical lines to trim old logs without rebuilding the selection or level mapping.
    private readonly List<(int Length, int LineCount)> _renderedEntries = [];
    private readonly List<LogLevel> _lineLevels = [];
    private ScrollViewer? _logScroller;
    private bool _updatingDocument;
    private bool _scrollPending;
    private bool _closed;

    /// <summary>
    /// <para>初始化日志窗口，并绑定到跨窗口关闭与重开保留日志的共享视图模型。</para>
    /// Initializes the log window with the shared view model that retains logs across window closure and reopening.
    /// </summary>
    public LogWindow()
    {
        InitializeComponent();
        DataContext = SharedViewModel;
        LogEditor.Document.UndoStack.SizeLimit = 0;
        LogEditor.Options.AllowScrollBelowDocument = false;
        LogEditor.Options.EnableHyperlinks = false;
        LogEditor.Options.EnableEmailHyperlinks = false;
        LogEditor.TextArea.TextView.LineTransformers.Add(new LogLevelColorizer(this));
        RebuildDocument();
        SharedViewModel.Entries.CollectionChanged += OnEntriesCollectionChanged;
        LogEditor.Loaded += OnLogEditorLoaded;
        LogEditor.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        LogEditor.TextArea.SelectionChanged += OnSelectionChanged;
        LanguageService.ThemeChanged += OnThemeChanged;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        // 仅解绑窗口事件；共享视图模型继续接收日志并保留筛选状态。
        // Detach window events only; the shared view model keeps receiving logs and retaining filters.
        SharedViewModel.Entries.CollectionChanged -= OnEntriesCollectionChanged;
        LogEditor.Loaded -= OnLogEditorLoaded;
        LogEditor.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        LogEditor.TextArea.SelectionChanged -= OnSelectionChanged;
        LanguageService.ThemeChanged -= OnThemeChanged;
        _logScroller = null;
        base.OnClosed(e);
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _updatingDocument = true;
        try
        {
            using (LogEditor.Document.RunUpdate())
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        InsertEntries(e.NewStartingIndex, e.NewItems!.Cast<LogViewModel.LogEntryViewModel>());
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        RemoveEntries(e.OldStartingIndex, e.OldItems!.Count);
                        break;
                    default:
                        RebuildDocument();
                        break;
                }
            }
        }
        finally
        {
            _updatingDocument = false;
        }
        FollowAutoScroll();
    }

    private void RebuildDocument()
    {
        _renderedEntries.Clear();
        _lineLevels.Clear();
        LogEditor.Document.Text = string.Empty;
        InsertEntries(0, SharedViewModel.Entries);
    }

    private void InsertEntries(int index, IEnumerable<LogViewModel.LogEntryViewModel> entries)
    {
        var offset = index == _renderedEntries.Count
            ? LogEditor.Document.TextLength
            : _renderedEntries.Take(index).Sum(entry => entry.Length);
        var lineIndex = index == _renderedEntries.Count
            ? _lineLevels.Count
            : _renderedEntries.Take(index).Sum(entry => entry.LineCount);
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            var line = $"{entry.Timestamp} [{entry.Level,-5}] {entry.Source} - {entry.Message}".ReplaceLineEndings("\n") + "\n";
            var lineCount = line.Count(character => character == '\n');
            _renderedEntries.Insert(index++, (line.Length, lineCount));
            _lineLevels.InsertRange(lineIndex, Enumerable.Repeat(entry.Level, lineCount));
            lineIndex += lineCount;
            text.Append(line);
        }
        if (text.Length > 0)
            LogEditor.Document.Insert(offset, text.ToString());
    }

    private void RemoveEntries(int index, int count)
    {
        var offset = _renderedEntries.Take(index).Sum(entry => entry.Length);
        var lineIndex = _renderedEntries.Take(index).Sum(entry => entry.LineCount);
        var removed = _renderedEntries.GetRange(index, count);
        _renderedEntries.RemoveRange(index, count);
        _lineLevels.RemoveRange(lineIndex, removed.Sum(entry => entry.LineCount));
        LogEditor.Document.Remove(offset, removed.Sum(entry => entry.Length));
    }

    /// <summary>
    /// <para>解析编辑器内部滚动容器并跟随最新条目。</para>
    /// Resolves the editor's scroll container and follows the latest entry.
    /// </summary>
    private void OnLogEditorLoaded(object? sender, RoutedEventArgs e)
    {
        // 为中英文回退字体预留相同高度，避免纯英文行与中文行的间距不同。
        // Reserve the same height for Latin and CJK fallback fonts on every line.
        var textView = LogEditor.TextArea.TextView;
        using var metrics = new TextLayout("Ag中文", new Typeface(LogEditor.FontFamily), LogEditor.FontSize, Brushes.Black);
        var defaultTextHeight = textView.DefaultLineHeight / LogEditor.Options.LineHeightFactor;
        LogEditor.Options.LineHeightFactor = Math.Ceiling(Math.Max(metrics.Height, defaultTextHeight)) / defaultTextHeight;

        if (_logScroller is null)
        {
            _logScroller = LogEditor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            _logScroller?.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, true);
        }

        FollowAutoScroll();
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

            LogService.Instance.Write(LogLevel.Info, "LogWindow",
                LanguageService.Instance.GetString("Log_ExportDone", file.Name));
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "LogWindow",
                LanguageService.Instance.GetString("Log_ExportFailed", ex.Message));
        }
    }

    private void OnCopySelectionClick(object? sender, RoutedEventArgs e) => LogEditor.Copy();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => LogEditor.SelectAll();

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        CopySelectionMenuItem.IsEnabled = LogEditor.SelectionLength > 0;
        if (!_updatingDocument)
            UpdateAutoScroll();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => LogEditor.TextArea.TextView.Redraw();

    /// <summary>
    /// <para>滚动变化时同步自动滚动状态：用户滚到列表底部时恢复自动滚动，
    /// 向上翻阅历史时暂停自动滚动（否则每次滚动都会被拉回底部，表现为滚轮无效）。</para>
    /// Syncs the auto-scroll state on scroll changes: restores auto-scroll when the user reaches
    /// the bottom, and pauses it while browsing history upwards (otherwise every wheel tick would
    /// snap back to the bottom, making the wheel appear dead).
    /// </summary>
    private void OnScrollChanged(object? sender, RoutedEventArgs e)
    {
        if (!_updatingDocument && !_scrollPending)
            UpdateAutoScroll();
    }

    private void UpdateAutoScroll()
    {
        if (_logScroller is not null)
            SharedViewModel.AutoScroll = LogEditor.SelectionLength == 0
                && _logScroller.Offset.Y + _logScroller.Viewport.Height >= _logScroller.Extent.Height - 4;
    }

    /// <summary>
    /// <para>新增条目时自动跟随滚动到底部（若开启自动滚动）。</para>
    /// Follows the latest entry on new items when auto-scroll is enabled.
    /// </summary>
    private void FollowAutoScroll()
    {
        if (!_closed && !_scrollPending && SharedViewModel.AutoScroll && LogEditor.SelectionLength == 0)
        {
            // 等待文本布局更新后再滚动，避免新增行改变高度时误判为离开底部。
            // Scroll after text layout updates so newly added lines do not disable following.
            _scrollPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_closed && SharedViewModel.AutoScroll && LogEditor.SelectionLength == 0)
                    ScrollToEnd();
                _scrollPending = false;
            }, DispatcherPriority.Loaded);
        }
    }

    private void ScrollToEnd() => _logScroller?.ScrollToEnd();

    private sealed class LogLevelColorizer(LogWindow window) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.LineNumber > window._lineLevels.Count)
                return;

            var resourceKey = window._lineLevels[line.LineNumber - 1] switch
            {
                LogLevel.Trace => "LogTraceBrush",
                LogLevel.Debug => "LogDebugBrush",
                LogLevel.Info => "LogInfoBrush",
                LogLevel.Warn => "LogWarnBrush",
                LogLevel.Error => "LogErrorBrush",
                LogLevel.Fatal => "LogFatalBrush",
                _ => "LogInfoBrush",
            };
            if (window.TryFindResource(resourceKey, window.ActualThemeVariant, out var resource) && resource is IBrush brush)
                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    // Keep glyph placement independent of other fallback fonts present on the line.
                    element.TextRunProperties.SetBaselineAlignment(BaselineAlignment.Center);
                });
        }
    }
}