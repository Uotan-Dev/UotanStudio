using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>Workspace catalog of installed workflow templates that can be applied to the node canvas.</summary>
public sealed partial class PluginToolViewModel : Tool, IDisposable
{
    private readonly VisualPluginManager _manager;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPluginCommand))]
    private VisualPluginRegistration? _selectedPlugin;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Installed, compatible workflow templates available for one-click application.</summary>
    public ObservableCollection<VisualPluginRegistration> Plugins { get; } = [];

    public PluginToolViewModel() : this(VisualPluginManager.Instance)
    {
    }

    internal PluginToolViewModel(VisualPluginManager manager)
    {
        _manager = manager;
        _manager.Changed += OnManagerChanged;
        RebuildPlugins();
    }

    [RelayCommand]
    private void ApplyPlugin(VisualPluginRegistration? registration)
    {
        if (registration is not { IsTemplate: true, IsAvailable: true, Package: { } package })
        {
            return;
        }

        if (ProjectOperationsService.Instance.NodeEditorVM is not { } editor)
        {
            Status = LanguageService.Instance.GetString("Plugin_EditorUnavailable");
            return;
        }

        try
        {
            editor.ImportTemplate(package);
            Status = LanguageService.Instance.GetString("Plugin_TemplateApplied");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            Status = string.Format(LanguageService.Instance.GetString("Plugin_OperationFailed"), ex.Message);
        }
    }

    private void OnManagerChanged(object? sender, EventArgs e) => RebuildPlugins();

    private void RebuildPlugins()
    {
        var selectedId = SelectedPlugin?.Id;
        Plugins.Clear();
        foreach (var registration in _manager.Registrations.Where(registration =>
                     registration.IsTemplate && registration.IsAvailable))
        {
            Plugins.Add(registration);
        }

        SelectedPlugin = Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? Plugins.FirstOrDefault();
    }

    public void Dispose()
    {
        _manager.Changed -= OnManagerChanged;
        GC.SuppressFinalize(this);
    }
}
