using System.Collections.Specialized;
using System.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Core;
using UotanStudio.Services.Ui.Workspace;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// Tracks unsaved documents across dock collections.
/// <para>跟踪停靠集合中的未保存文档。</para>
/// </summary>
internal sealed class WorkspaceModificationTracker : IDisposable
{
    private readonly Func<IDockable?> _getLayout;
    private readonly HashSet<INotifyPropertyChanged> _objects = [];
    private readonly HashSet<INotifyCollectionChanged> _collections = [];

    internal bool HasUnsavedChanges { get; private set; }
    internal event EventHandler? Changed;

    internal WorkspaceModificationTracker(Func<IDockable?> getLayout)
    {
        _getLayout = getLayout;
        Refresh();
    }

    internal void Refresh()
    {
        ClearSubscriptions();
        foreach (var item in WorkspaceSessionCapture.Enumerate(_getLayout()))
        {
            Observe(item);
            if (item is IDock dock) ObserveCollection(dock.VisibleDockables);
            if (item is IRootDock { Windows: { } windows })
            {
                ObserveCollection(windows);
                foreach (var window in windows) Observe(window);
            }
        }
        UpdateState();
    }

    private void Observe(object item)
    {
        if (item is INotifyPropertyChanged observable && _objects.Add(observable))
            observable.PropertyChanged += OnPropertyChanged;
    }

    private void ObserveCollection(object? collection)
    {
        if (collection is INotifyCollectionChanged observable && _collections.Add(observable))
            observable.CollectionChanged += OnCollectionChanged;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is "VisibleDockables" or "Windows" or "Layout")
            Refresh();
        else if (e.PropertyName == "IsDirty") UpdateState();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void UpdateState()
    {
        var modified = _objects.Any(DocumentSaveService.IsDirty);
        if (HasUnsavedChanges == modified) return;
        HasUnsavedChanges = modified;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ClearSubscriptions()
    {
        foreach (var item in _objects) item.PropertyChanged -= OnPropertyChanged;
        foreach (var collection in _collections) collection.CollectionChanged -= OnCollectionChanged;
        _objects.Clear();
        _collections.Clear();
    }

    public void Dispose() => ClearSubscriptions();
}