using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.Services;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Nodes;

public partial class NodeEditorView
{
    private readonly LogTextPresenter<LogEntry> _executionLogPresenter;
    private NodeEditorViewModel? _executionLogModel;
    private bool _executionLogPaneExpanded;
    private bool _executionLogAutoScroll = true;
    private double _executionLogHeight = 190;

    private LogTextPresenter<LogEntry> CreateExecutionLogPresenter()
        => new(this, ExecutionLogTextEditor,
            static entry => $"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Source}: {entry.Message}",
            static entry => entry.Level,
            () => _executionLogAutoScroll,
            value => _executionLogAutoScroll = value,
            value => CopyExecutionLogSelectionMenuItem.IsEnabled = value);

    private void AttachExecutionLogPresentation()
    {
        _executionLogPresenter.Attach();
        BindExecutionLogModel();
    }

    private void DetachExecutionLogPresentation()
    {
        BindExecutionLogModel(null);
        _executionLogPresenter.Detach();
    }

    private void BindExecutionLogModel() => BindExecutionLogModel(DataContext as NodeEditorViewModel);

    private void BindExecutionLogModel(NodeEditorViewModel? model)
    {
        if (_executionLogModel is not null)
        {
            _executionLogModel.ExecutionLog.CollectionChanged -= OnExecutionLogCollectionChanged;
            _executionLogModel.PropertyChanged -= OnExecutionLogModelPropertyChanged;
        }

        _executionLogModel = model;
        if (_executionLogModel is not null)
        {
            _executionLogModel.ExecutionLog.CollectionChanged += OnExecutionLogCollectionChanged;
            _executionLogModel.PropertyChanged += OnExecutionLogModelPropertyChanged;
        }

        _executionLogPresenter.Rebuild(_executionLogModel?.ExecutionLog ?? []);
        UpdateExecutionLogPane();
    }

    private void OnExecutionLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => _executionLogPresenter.Update(e, _executionLogModel?.ExecutionLog ?? []);

    private void OnExecutionLogModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeEditorViewModel.IsExecutionLogExpanded))
        {
            UpdateExecutionLogPane();
        }
    }

    private void OnCenterPaneSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_executionLogPaneExpanded)
        {
            return;
        }

        var rowDefinitions = CenterPane.RowDefinitions;
        var minimumRowHeight = Math.Min(120, Math.Max(0, (CenterPane.Bounds.Height - 9) / 2));
        rowDefinitions[0].MinHeight = minimumRowHeight;
        rowDefinitions[2].MinHeight = minimumRowHeight;

        var maximumLogHeight = Math.Max(minimumRowHeight, CenterPane.Bounds.Height - 9 - minimumRowHeight);
        if (rowDefinitions[2].ActualHeight <= 0)
        {
            rowDefinitions[2].Height = new GridLength(Math.Clamp(_executionLogHeight, minimumRowHeight, maximumLogHeight));
        }
        else if (rowDefinitions[2].ActualHeight > maximumLogHeight)
        {
            rowDefinitions[2].Height = new GridLength(maximumLogHeight);
        }
        _executionLogHeight = Math.Min(_executionLogHeight, maximumLogHeight);
    }

    private void UpdateExecutionLogPane()
    {
        var expanded = _executionLogModel?.IsExecutionLogExpanded == true;
        if (expanded == _executionLogPaneExpanded)
        {
            return;
        }

        var rows = CenterPane.RowDefinitions;
        if (_executionLogPaneExpanded)
        {
            _executionLogHeight = Math.Max(120, rows[2].ActualHeight);
        }

        _executionLogPaneExpanded = expanded;
        if (expanded)
        {
            var minimumRowHeight = Math.Min(120, Math.Max(0, (CenterPane.Bounds.Height - 9) / 2));
            rows[0].MinHeight = minimumRowHeight;
            rows[1].Height = new GridLength(9);
            rows[2].MinHeight = minimumRowHeight;
            var maximumLogHeight = Math.Max(minimumRowHeight, CenterPane.Bounds.Height - 9 - minimumRowHeight);
            rows[0].Height = new GridLength(1, GridUnitType.Star);
            rows[2].Height = new GridLength(Math.Clamp(_executionLogHeight, minimumRowHeight, maximumLogHeight));
        }
        else
        {
            if (rows[2].ActualHeight > 0)
            {
                _executionLogHeight = Math.Max(120, rows[2].ActualHeight);
            }
            rows[0].MinHeight = 0;
            rows[1].Height = new GridLength(0);
            rows[2].MinHeight = 0;
            rows[2].Height = GridLength.Auto;
        }
    }

    private void OnClearExecutionLogClick(object? sender, RoutedEventArgs e)
    {
        _executionLogModel?.ExecutionLog.Clear();
        _executionLogPresenter.ScrollToEnd();
    }

    private void OnCopyExecutionLogSelectionClick(object? sender, RoutedEventArgs e)
        => ExecutionLogTextEditor.Copy();

    private void OnSelectAllExecutionLogClick(object? sender, RoutedEventArgs e)
        => ExecutionLogTextEditor.SelectAll();

    private void OnCollapseExecutionLogClick(object? sender, RoutedEventArgs e)
    {
        if (_executionLogModel is not null)
        {
            _executionLogModel.IsExecutionLogExpanded = false;
        }
    }

    private void OnExpandExecutionLogClick(object? sender, RoutedEventArgs e)
    {
        if (_executionLogModel is not null)
        {
            _executionLogModel.IsExecutionLogExpanded = true;
        }
    }
}