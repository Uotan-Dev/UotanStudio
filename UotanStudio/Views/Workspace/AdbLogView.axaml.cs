using System.Collections.Specialized;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using UotanStudio.Services;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// <para>ADB 日志面板视图，实时展示设备与烧录通道的日志条目。</para>
/// ADB log panel view, displaying Device and Flash channel log entries in real time.
/// </summary>
public partial class AdbLogView : UserControl
{
    private readonly List<(int Length, int LineCount)> _renderedEntries = [];
    private readonly List<LogLevel> _lineLevels = [];
    private ScrollViewer? _logScroller;
    private bool _closed;

    public AdbLogView()
    {
        InitializeComponent();
        LogEditor.Document.UndoStack.SizeLimit = 0;
        LogEditor.Options.AllowScrollBelowDocument = false;
        LogEditor.TextArea.TextView.LineTransformers.Add(new LogLevelColorizer(this));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _closed = false;
        LogEditor.Loaded += OnLogEditorLoaded;
        LogEditor.TextArea.SelectionChanged += OnSelectionChanged;
        LogEditor.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        LanguageService.ThemeChanged += OnThemeChanged;

        if (DataContext is AdbLogViewModel vm)
        {
            vm.Entries.CollectionChanged += OnEntriesCollectionChanged;
            RebuildDocument();
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _closed = true;
        LogEditor.Loaded -= OnLogEditorLoaded;
        LogEditor.TextArea.SelectionChanged -= OnSelectionChanged;
        LogEditor.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        LanguageService.ThemeChanged -= OnThemeChanged;

        if (DataContext is AdbLogViewModel vm)
        {
            vm.Entries.CollectionChanged -= OnEntriesCollectionChanged;
        }
        _logScroller = null;
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            using (LogEditor.Document.RunUpdate())
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        if (e.NewItems is not null)
                            InsertEntries(e.NewStartingIndex, e.NewItems.Cast<AdbLogViewModel.LogEntryViewModel>());
                        break;
                    default:
                        RebuildDocument();
                        break;
                }
            }
            FollowAutoScroll();
        });
    }

    private void RebuildDocument()
    {
        _renderedEntries.Clear();
        _lineLevels.Clear();
        LogEditor.Document.Text = string.Empty;
        if (DataContext is not AdbLogViewModel vm) return;
        InsertEntries(0, vm.Entries);
    }

    private void InsertEntries(int index, IEnumerable<AdbLogViewModel.LogEntryViewModel> entries)
    {
        var offset = index == _renderedEntries.Count
            ? LogEditor.Document.TextLength
            : _renderedEntries.Take(index).Sum(e => e.Length);
        var lineIndex = index == _renderedEntries.Count
            ? _lineLevels.Count
            : _renderedEntries.Take(index).Sum(e => e.LineCount);
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            var line = $"{entry.Timestamp} [{entry.Level,-5}] {entry.Source} - {entry.Message}".ReplaceLineEndings("\n") + "\n";
            var lineCount = line.Count(c => c == '\n');
            _renderedEntries.Insert(index++, (line.Length, lineCount));
            _lineLevels.InsertRange(lineIndex, Enumerable.Repeat(entry.Level, lineCount));
            lineIndex += lineCount;
            text.Append(line);
        }
        if (text.Length > 0)
            LogEditor.Document.Insert(offset, text.ToString());
    }

    private void OnLogEditorLoaded(object? sender, RoutedEventArgs e)
    {
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

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AdbLogViewModel vm)
            vm.Clear();
        ScrollToEnd();
    }

    private void OnCopySelectionClick(object? sender, RoutedEventArgs e) => LogEditor.Copy();
    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => LogEditor.SelectAll();

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (CopySelectionMenuItem != null) CopySelectionMenuItem.IsEnabled = LogEditor.SelectionLength > 0;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => LogEditor.TextArea.TextView.Redraw();

    private void OnScrollChanged(object? sender, RoutedEventArgs e) => UpdateAutoScroll();

    private void UpdateAutoScroll()
    {
        if (_logScroller is not null && DataContext is AdbLogViewModel vm)
            vm.AutoScroll = LogEditor.SelectionLength == 0
                && _logScroller.Offset.Y + _logScroller.Viewport.Height >= _logScroller.Extent.Height - 4;
    }

    private void FollowAutoScroll()
    {
        if (!_closed && DataContext is AdbLogViewModel vm && vm.AutoScroll && LogEditor.SelectionLength == 0)
        {
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Loaded);
        }
    }

    private void ScrollToEnd() => _logScroller?.ScrollToEnd();

    private sealed class LogLevelColorizer(AdbLogView view) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.LineNumber > view._lineLevels.Count)
                return;

            var resourceKey = view._lineLevels[line.LineNumber - 1] switch
            {
                LogLevel.Trace => "LogTraceBrush",
                LogLevel.Debug => "LogDebugBrush",
                LogLevel.Info => "LogInfoBrush",
                LogLevel.Warn => "LogWarnBrush",
                LogLevel.Error => "LogErrorBrush",
                LogLevel.Fatal => "LogFatalBrush",
                _ => "LogInfoBrush",
            };
            if (view.TryFindResource(resourceKey, view.ActualThemeVariant, out var resource) && resource is IBrush brush)
                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    element.TextRunProperties.SetBaselineAlignment(BaselineAlignment.Center);
                });
        }
    }
}
