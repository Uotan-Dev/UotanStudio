using System.Collections.Specialized;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using UotanStudio.Services;

namespace UotanStudio.Views.Controls;

/// <summary>Maintains entry boundaries and level mappings without replacing unaffected log text.<para>维护条目边界与级别映射，不替换未受影响的日志文本。</para></summary>
/// <param name="document">Text document holding the rendered log content.<para>存放已渲染日志内容的文本文档。</para></param>
internal sealed class LogTextBuffer(TextDocument document)
{
    private readonly List<(int Length, int Lines)> _entries = [];
    internal List<LogLevel> Levels { get; } = [];

    internal void Clear()
    {
        _entries.Clear();
        Levels.Clear();
        document.Text = string.Empty;
    }

    internal void Insert<T>(int index, IEnumerable<T> entries, Func<T, string> text, Func<T, LogLevel> level)
    {
        var offset = index == _entries.Count ? document.TextLength : _entries.Take(index).Sum(entry => entry.Length);
        var lineIndex = index == _entries.Count ? Levels.Count : _entries.Take(index).Sum(entry => entry.Lines);
        var content = new StringBuilder();
        foreach (var entry in entries)
        {
            var line = text(entry) + "\n";
            var lineCount = line.Count(character => character == '\n');
            _entries.Insert(index++, (line.Length, lineCount));
            Levels.InsertRange(lineIndex, Enumerable.Repeat(level(entry), lineCount));
            lineIndex += lineCount;
            content.Append(line);
        }
        if (content.Length > 0) document.Insert(offset, content.ToString());
    }

    internal void Remove(int index, int count)
    {
        var offset = _entries.Take(index).Sum(entry => entry.Length);
        var lineIndex = _entries.Take(index).Sum(entry => entry.Lines);
        var removed = _entries.GetRange(index, count);
        _entries.RemoveRange(index, count);
        Levels.RemoveRange(lineIndex, removed.Sum(entry => entry.Lines));
        document.Remove(offset, removed.Sum(entry => entry.Length));
    }
}

/// <summary>Shares plain log rendering and scrolling while each holder retains its own model and lifetime.<para>共用纯文本日志渲染与滚动，各持有方保留自己的模型与生命周期。</para></summary>
internal sealed class LogTextPresenter<T>
{
    private readonly TextEditor _editor;
    private readonly Func<T, string> _text;
    private readonly Func<T, LogLevel> _level;
    private readonly Func<bool> _getFollow;
    private readonly Action<bool> _setFollow;
    private readonly Action<bool> _setCopyEnabled;
    private readonly LogTextBuffer _buffer;
    private ScrollViewer? _scroller;
    private bool _attached;
    private bool _updating;
    private bool _pending;
    private int _generation;

    internal LogTextPresenter(Control host, TextEditor editor, Func<T, string> text, Func<T, LogLevel> level,
        Func<bool> getFollow, Action<bool> setFollow, Action<bool> setCopyEnabled)
    {
        _editor = editor;
        _text = text;
        _level = level;
        _getFollow = getFollow;
        _setFollow = setFollow;
        _setCopyEnabled = setCopyEnabled;
        _buffer = new LogTextBuffer(editor.Document);
        editor.Document.UndoStack.SizeLimit = 0;
        editor.Options.AllowScrollBelowDocument = false;
        editor.TextArea.TextView.LineTransformers.Add(new LevelColorizer(host, _buffer.Levels));
    }

    internal void Attach()
    {
        if (_attached) return;
        _attached = true;
        _editor.Loaded += OnLoaded;
        _editor.TextArea.SelectionChanged += OnSelectionChanged;
        _editor.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        LanguageService.ThemeChanged += OnThemeChanged;
        if (_editor.IsLoaded) InitializeViewport();
    }

    internal void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _generation++;
        _pending = false;
        _editor.Loaded -= OnLoaded;
        _editor.TextArea.SelectionChanged -= OnSelectionChanged;
        _editor.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        LanguageService.ThemeChanged -= OnThemeChanged;
        _scroller = null;
    }

    internal void Rebuild(IEnumerable<T> entries)
    {
        Update(null, entries);
    }

    internal void Update(NotifyCollectionChangedEventArgs? change, IEnumerable<T> entries)
    {
        _updating = true;
        try
        {
            using (_editor.Document.RunUpdate())
            {
                switch (change?.Action)
                {
                    case NotifyCollectionChangedAction.Add when change.NewItems is not null:
                        _buffer.Insert(change.NewStartingIndex, change.NewItems.Cast<T>(), _text, _level);
                        break;
                    case NotifyCollectionChangedAction.Remove when change.OldItems is not null:
                        _buffer.Remove(change.OldStartingIndex, change.OldItems.Count);
                        break;
                    default:
                        _buffer.Clear();
                        _buffer.Insert(0, entries, _text, _level);
                        break;
                }
            }
        }
        finally { _updating = false; }
        FollowEnd();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => InitializeViewport();

    private void InitializeViewport()
    {
        LogEditorMetrics.ApplyLineHeight(_editor);
        _scroller = _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        _scroller?.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, true);
        FollowEnd();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        _setCopyEnabled(_editor.SelectionLength > 0);
        if (!_updating) UpdateFollow();
    }

    private void OnScrollChanged(object? sender, RoutedEventArgs e)
    {
        if (!_updating && !_pending) UpdateFollow();
    }

    private void UpdateFollow()
    {
        if (_scroller is not null)
            _setFollow(_editor.SelectionLength == 0
                && _scroller.Offset.Y + _scroller.Viewport.Height >= _scroller.Extent.Height - 4);
    }

    private void FollowEnd()
    {
        if (!_attached || _pending || !_getFollow() || _editor.SelectionLength != 0) return;
        _pending = true;
        var generation = _generation;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _generation) return;
            try
            {
                if (_attached && _getFollow() && _editor.SelectionLength == 0) ScrollToEnd();
            }
            finally { _pending = false; }
        }, DispatcherPriority.Loaded);
    }

    internal void ScrollToEnd() => _scroller?.ScrollToEnd();

    private void OnThemeChanged(object? sender, EventArgs e) => _editor.TextArea.TextView.Redraw();

    private sealed class LevelColorizer(Control host, IReadOnlyList<LogLevel> levels) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.LineNumber > levels.Count) return;
            var key = levels[line.LineNumber - 1] switch
            {
                LogLevel.Trace => "LogTraceBrush",
                LogLevel.Debug => "LogDebugBrush",
                LogLevel.Warn => "LogWarnBrush",
                LogLevel.Error => "LogErrorBrush",
                LogLevel.Fatal => "LogFatalBrush",
                _ => "LogInfoBrush"
            };
            if (host.TryFindResource(key, host.ActualThemeVariant, out var resource) && resource is IBrush brush)
                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    element.TextRunProperties.SetBaselineAlignment(BaselineAlignment.Center);
                });
        }
    }
}

/// <summary>Reserves identical line height for Latin and CJK fallback fonts in all log views.<para>为所有日志视图中的拉丁与 CJK 回退字体预留一致的行高。</para></summary>
internal static class LogEditorMetrics
{
    internal static void ApplyLineHeight(TextEditor editor)
    {
        using var metrics = new TextLayout("Ag中文", new Typeface(editor.FontFamily), editor.FontSize, Brushes.Black);
        var defaultHeight = editor.TextArea.TextView.DefaultLineHeight / editor.Options.LineHeightFactor;
        editor.Options.LineHeightFactor = Math.Ceiling(Math.Max(metrics.Height, defaultHeight)) / defaultHeight;
    }
}