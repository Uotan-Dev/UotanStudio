using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services.VisualPlugins;

namespace UotanStudio.ViewModels.Shell;

/// <summary>Search and selection state for the independent visual-plugin manager.</summary>
public partial class VisualPluginManagerViewModel : ViewModelBase, IDisposable
{
    private readonly VisualPluginManager _manager;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private VisualPluginRegistration? _selectedPlugin;

    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>Packages visible under the current search filter.</summary>
    public ObservableCollection<VisualPluginRegistration> Plugins { get; } = [];

    /// <summary>Whether a package is selected for detail and package actions.</summary>
    public bool HasSelection => SelectedPlugin is not null;

    public VisualPluginManagerViewModel() : this(VisualPluginManager.Instance)
    {
    }

    internal VisualPluginManagerViewModel(VisualPluginManager manager)
    {
        _manager = manager;
        _manager.Changed += OnManagerChanged;
        RebuildVisiblePlugins();
    }

    partial void OnSearchChanged(string value) => RebuildVisiblePlugins();

    /// <summary>Re-scans the managed package directory.</summary>
    public void Refresh() => _manager.Refresh();

    private void OnManagerChanged(object? sender, EventArgs e) => RebuildVisiblePlugins();

    private void RebuildVisiblePlugins()
    {
        var selectedId = SelectedPlugin?.Id;
        var query = Search.Trim();
        Plugins.Clear();
        foreach (var registration in _manager.Registrations.Where(registration =>
                     query.Length == 0
                     || registration.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                     || registration.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || registration.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            Plugins.Add(registration);
        }

        SelectedPlugin = Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? Plugins.FirstOrDefault();
    }

    public void Dispose() => _manager.Changed -= OnManagerChanged;
}