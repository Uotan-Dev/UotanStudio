using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Views.Workspace;

public partial class AdbLogView : UserControl
{
    private readonly List<(int Length, int LineCount)> _renderedEntries = [];
    private readonly List<(LogLevel? Level, AdbLogcatEntry? Entry)> _lines = [];
    private AdbLogViewModel? _model;
    private ScrollViewer? _scroller;
    private bool _followScheduled;
    private bool _updating;

    /// <summary>
    /// Initializes the streaming logcat view.
    /// <para>初始化实时 logcat 视图。</para>
    /// </summary>
    public AdbLogView()
    {
        InitializeComponent();
        LogEditor.Document.UndoStack.SizeLimit = 0;
        LogEditor.Options.AllowScrollBelowDocument = false;
        LogEditor.Options.EnableHyperlinks = false;
        LogEditor.Options.EnableEmailHyperlinks = false;
        LogEditor.TextArea.TextView.ElementGenerators.Add(new LogColumnGenerator(this));
        LogEditor.TextArea.TextView.LineTransformers.Add(new LogLevelColorizer(this));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => { if (IsLoaded) BindModel(); };
        LogEditor.TextArea.SelectionChanged += OnSelectionChanged;
        LogEditor.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var textView = LogEditor.TextArea.TextView;
        using var metrics = new TextLayout("Ag中文", new Typeface(LogEditor.FontFamily), LogEditor.FontSize, Brushes.Black);
        var defaultTextHeight = textView.DefaultLineHeight / LogEditor.Options.LineHeightFactor;
        LogEditor.Options.LineHeightFactor = Math.Ceiling(Math.Max(metrics.Height, defaultTextHeight)) / defaultTextHeight;
        LanguageService.ThemeChanged += OnThemeChanged;
        _scroller = LogEditor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        _scroller?.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, true);
        BindModel();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        LanguageService.ThemeChanged -= OnThemeChanged;
        if (_model is not null)
        {
            _model.Lines.CollectionChanged -= OnLinesChanged;
            _model.PropertyChanged -= OnModelPropertyChanged;
        }
        _model = null;
        _scroller = null;
    }

    private void BindModel()
    {
        if (_model is not null)
        {
            _model.Lines.CollectionChanged -= OnLinesChanged;
            _model.PropertyChanged -= OnModelPropertyChanged;
        }
        _model = DataContext as AdbLogViewModel;
        if (_model is not null)
        {
            _model.Lines.CollectionChanged += OnLinesChanged;
            _model.PropertyChanged += OnModelPropertyChanged;
        }
        _updating = true;
        try
        {
            using (LogEditor.Document.RunUpdate())
                RebuildDocument();
        }
        finally { _updating = false; }
        FollowEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _updating = true;
        try
        {
            Vector? preservedOffset = null;
            using (LogEditor.Document.RunUpdate())
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                        InsertEntries(e.NewStartingIndex, e.NewItems.Cast<AdbLogcatEntry>());
                        break;
                    case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                        preservedOffset = RemoveEntries(e.OldStartingIndex, e.OldItems.Count);
                        break;
                    default:
                        RebuildDocument();
                        break;
                }
            }
            if (preservedOffset is { } offset)
                _scroller?.SetCurrentValue(ScrollViewer.OffsetProperty, offset);
        }
        finally { _updating = false; }
        FollowEnd();
    }

    private void RebuildDocument()
    {
        _renderedEntries.Clear();
        _lines.Clear();
        LogEditor.Document.Text = string.Empty;
        if (_model is not null) InsertEntries(0, _model.Lines);
    }

    private void InsertEntries(int index, IEnumerable<AdbLogcatEntry> entries)
    {
        var offset = index == _renderedEntries.Count
            ? LogEditor.Document.TextLength : _renderedEntries.Take(index).Sum(entry => entry.Length);
        var lineIndex = index == _renderedEntries.Count
            ? _lines.Count : _renderedEntries.Take(index).Sum(entry => entry.LineCount);
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            var line = entry.Text.ReplaceLineEndings("\n") + "\n";
            var lineCount = line.Count(character => character == '\n');
            _renderedEntries.Insert(index++, (line.Length, lineCount));
            _lines.Insert(lineIndex, (entry.Level, entry.Priority == '\0' ? null : entry));
            _lines.InsertRange(lineIndex + 1, Enumerable.Repeat<(LogLevel?, AdbLogcatEntry?)>((entry.Level, null), lineCount - 1));
            lineIndex += lineCount;
            text.Append(line);
        }
        if (text.Length > 0) LogEditor.Document.Insert(offset, text.ToString());
    }

    private Vector? RemoveEntries(int index, int count)
    {
        var offset = _renderedEntries.Take(index).Sum(entry => entry.Length);
        var lineIndex = _renderedEntries.Take(index).Sum(entry => entry.LineCount);
        var removed = _renderedEntries.GetRange(index, count);
        var removedLineCount = removed.Sum(entry => entry.LineCount);
        Vector? preservedOffset = null;
        if (_scroller is not null && (_model?.AutoScroll != true || LogEditor.SelectionLength > 0))
        {
            // Preserve the visible text when cache eviction removes lines above it.
            var textView = LogEditor.TextArea.TextView;
            var removedTop = textView.GetVisualTopByDocumentLine(lineIndex + 1);
            var removedBottom = textView.GetVisualTopByDocumentLine(lineIndex + removedLineCount + 1);
            var previous = _scroller.Offset;
            var removedAboveViewport = Math.Clamp(previous.Y - removedTop, 0, removedBottom - removedTop);
            preservedOffset = new Vector(previous.X, previous.Y - removedAboveViewport);
        }
        _renderedEntries.RemoveRange(index, count);
        _lines.RemoveRange(lineIndex, removedLineCount);
        LogEditor.Document.Remove(offset, removed.Sum(entry => entry.Length));
        return preservedOffset;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => LogEditor.TextArea.TextView.Redraw();

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AdbLogViewModel.AutoScroll) or nameof(AdbLogViewModel.IsPaused))) return;
        if (_model is { AutoScroll: true, IsPaused: false }) FollowEnd();
        else KeepScrollOffset();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        CopySelectionMenuItem.IsEnabled = LogEditor.SelectionLength > 0;
        if (_updating) return;
        if (LogEditor.SelectionLength > 0) KeepScrollOffset();
        else FollowEnd();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_updating || _followScheduled || _scroller is null || _model is null) return;
        // Browsing older lines pauses following; only the button enables it again.
        if (LogEditor.SelectionLength == 0 && e.OffsetDelta.Y < 0 &&
            _scroller.Offset.Y + _scroller.Viewport.Height < _scroller.Extent.Height - 4)
            _model.AutoScroll = false;
        if (!_model.AutoScroll || LogEditor.SelectionLength > 0) KeepScrollOffset();
    }

    private void KeepScrollOffset() => _scroller?.SetCurrentValue(ScrollViewer.OffsetProperty, _scroller.Offset);

    private void FollowEnd()
    {
        if (_followScheduled || _model is not { AutoScroll: true, IsPaused: false } || LogEditor.SelectionLength != 0) return;
        _followScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _followScheduled = false;
            if (_model is { AutoScroll: true, IsPaused: false } && LogEditor.SelectionLength == 0 && _scroller is not null)
            {
                // A finite offset avoids retaining ScrollToEnd's infinite target after following is disabled.
                var offset = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
                _scroller.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(_scroller.Offset.X, offset));
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnCopySelectionClick(object? sender, RoutedEventArgs e) => LogEditor.Copy();
    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => LogEditor.SelectAll();

    private sealed class LogLevelColorizer(AdbLogView view) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.LineNumber > view._lines.Count) return;
            var resourceKey = view._lines[line.LineNumber - 1].Level switch
            {
                LogLevel.Trace => "LogTraceBrush",
                LogLevel.Debug => "LogDebugBrush",
                LogLevel.Info => "LogInfoBrush",
                LogLevel.Warn => "LogWarnBrush",
                LogLevel.Error => "LogErrorBrush",
                LogLevel.Fatal => "LogFatalBrush",
                _ => null
            };
            var brush = view.LogEditor.Foreground;
            if (resourceKey is not null &&
                view.TryFindResource(resourceKey, view.ActualThemeVariant, out var resource) && resource is IBrush levelBrush)
                brush = levelBrush;
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
                element.TextRunProperties.SetBaselineAlignment(BaselineAlignment.Center);
                if (element is LogColumnElement { IsPriority: true } && brush is ISolidColorBrush background)
                {
                    element.BackgroundBrush = brush;
                    element.TextRunProperties.SetForegroundBrush(ContrastingText(background.Color));
                }
            });
        }
    }
}
