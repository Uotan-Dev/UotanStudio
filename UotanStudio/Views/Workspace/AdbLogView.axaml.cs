using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Views.Workspace;

public partial class AdbLogView : UserControl
{
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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => { if (IsLoaded) BindModel(); };
        LogEditor.TextArea.SelectionChanged += (_, _) =>
            CopySelectionMenuItem.IsEnabled = LogEditor.SelectionLength > 0;
        LogEditor.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _scroller = LogEditor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        _scroller?.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, true);
        BindModel();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_model is not null) _model.Lines.CollectionChanged -= OnLinesChanged;
        _model = null;
        _scroller = null;
    }

    private void BindModel()
    {
        if (_model is not null) _model.Lines.CollectionChanged -= OnLinesChanged;
        _model = DataContext as AdbLogViewModel;
        if (_model is not null) _model.Lines.CollectionChanged += OnLinesChanged;
        _updating = true;
        try
        {
            LogEditor.Text = _model is null || _model.Lines.Count == 0
                ? string.Empty : string.Join('\n', _model.Lines) + "\n";
        }
        finally { _updating = false; }
        FollowEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _updating = true;
        try
        {
            var document = LogEditor.Document;
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                    document.Insert(document.TextLength, string.Join('\n', e.NewItems.Cast<string>()) + "\n");
                    break;
                case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                    document.Remove(0, e.OldItems.Cast<string>().Sum(line => line.Length + 1));
                    break;
                default:
                    document.Text = string.Empty;
                    break;
            }
        }
        finally { _updating = false; }
        FollowEnd();
    }

    private void OnScrollChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating || _followScheduled || _scroller is null || _model is null) return;
        _model.AutoScroll = _scroller.Offset.Y + _scroller.Viewport.Height >= _scroller.Extent.Height - 4;
    }

    private void FollowEnd()
    {
        if (_followScheduled || _model?.AutoScroll != true || LogEditor.SelectionLength != 0) return;
        _followScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _followScheduled = false;
            if (_model?.AutoScroll == true && LogEditor.SelectionLength == 0) _scroller?.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }

    private void OnClearClick(object? sender, RoutedEventArgs e) => _model?.Clear();
    private void OnCopySelectionClick(object? sender, RoutedEventArgs e) => LogEditor.Copy();
    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => LogEditor.SelectAll();
}
