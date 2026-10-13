using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// Displays workspace log entries with shared incremental rendering and scrolling.
/// <para>以共享的增量渲染与滚动方式显示工作区日志条目。</para>
/// </summary>
public partial class WorkspaceLogView : UserControl
{
    private readonly LogTextPresenter<WorkspaceLogViewModel.LogEntryViewModel> _presenter;
    private WorkspaceLogViewModel? _model;

    public WorkspaceLogView()
    {
        InitializeComponent();
        _presenter = new(this, LogEditor, static entry => entry.Text, static entry => entry.Level,
            () => _model?.AutoScroll == true,
            value => { if (_model is { } model) model.AutoScroll = value; },
            value => CopySelectionMenuItem.IsEnabled = value);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => { if (IsLoaded) BindModel(); };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _presenter.Attach();
        BindModel();
    }

    private void BindModel()
    {
        if (_model is not null) _model.Entries.CollectionChanged -= OnEntriesCollectionChanged;
        _model = DataContext as WorkspaceLogViewModel;
        if (_model is not null) _model.Entries.CollectionChanged += OnEntriesCollectionChanged;
        _presenter.Rebuild(_model?.Entries.AsEnumerable() ?? []);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _presenter.Detach();
        if (_model is not null) _model.Entries.CollectionChanged -= OnEntriesCollectionChanged;
        _model = null;
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => _presenter.Update(e, _model?.Entries.AsEnumerable() ?? []);

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        _model?.Clear();
        _presenter.ScrollToEnd();
    }

    private void OnCopySelectionClick(object? sender, RoutedEventArgs e) => LogEditor.Copy();
    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => LogEditor.SelectAll();
}