using System.Collections.ObjectModel;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// <para>属性面板中可视化插件工作区的视图模型，维护可编辑复合插件列表与当前选中项。</para>
/// View model for the visual-plugin workspace in the property pane, maintaining the editable composite plugin list
/// and the current selection.
/// </summary>
public sealed class VisualPluginWorkspaceViewModel : ViewModelBase, IDisposable
{
    /// <summary>Whether the property pane exposes the plugin workspace.</summary>
    public bool ShowWorkspace { get; }

    /// <summary>Installed composite nodes available for isolated graph editing.</summary>
    public ObservableCollection<VisualPluginRegistration> EditableVisualPlugins { get; } = [];

    /// <summary>The composite node selected in the property-pane plugin workspace.</summary>
    public VisualPluginRegistration? SelectedVisualPlugin
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>初始化工作区视图模型并订阅插件管理器变更。</para>
    /// Initializes the workspace view model and subscribes to plugin manager changes.
    /// </summary>
    /// <param name="showWorkspace">
    /// <para>是否在属性面板中暴露插件工作区。</para>
    /// Whether the property pane exposes the plugin workspace.
    /// </param>
    public VisualPluginWorkspaceViewModel(bool showWorkspace)
    {
        ShowWorkspace = showWorkspace;
        VisualPluginManager.Instance.Changed += OnPluginsChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Dispose() => VisualPluginManager.Instance.Changed -= OnPluginsChanged;

    private void OnPluginsChanged(object? sender, EventArgs e)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Refresh();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        var selectedId = SelectedVisualPlugin?.Id;
        EditableVisualPlugins.Clear();
        foreach (var registration in VisualPluginManager.Instance.Registrations.Where(registration =>
                     registration.Kind == VisualPluginKind.CompositeNode && registration.IsAvailable))
        {
            EditableVisualPlugins.Add(registration);
        }

        SelectedVisualPlugin = EditableVisualPlugins.FirstOrDefault(registration =>
            string.Equals(registration.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? EditableVisualPlugins.FirstOrDefault();
    }
}