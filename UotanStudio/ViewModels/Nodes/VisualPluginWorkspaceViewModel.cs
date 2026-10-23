using System.Collections.ObjectModel;
using UotanStudio.Projects;
using UotanStudio.Services;
using UotanStudio.Services.Plugins;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// View model for the visual-plugin workspace in the property pane, maintaining the editable composite plugin list
/// and the current selection.
/// <para>属性面板中可视化插件工作区的视图模型，维护可编辑复合插件列表与当前选中项。</para>
/// </summary>
public sealed class VisualPluginWorkspaceViewModel : ViewModelBase, IDisposable
{
    /// <summary>Whether the property pane exposes the plugin workspace.</summary>
    public bool ShowWorkspace { get; }

    /// <summary>
    /// Gets whether the current publish form blocks any plugin kind (only code-dependent kinds are
    /// affected on an AOT or trimmed publish); the pane shows an explanation instead of staying silently empty.
    /// <para>获取当前发布形式是否禁用了任一插件种类（AOT 或裁剪发布下仅代码类插件受影响）；面板据此显示说明而非静默留空。</para>
    /// </summary>
    public bool CodeRuntimeDisabled =>
        PluginKindPolicy.BlockedKinds(RuntimeCapabilityProbe.EffectiveCapability).Count > 0;

    /// <summary>
    /// Gets the localized explanation shown while code-dependent plugins are disabled; an empty
    /// string when they are enabled.
    /// <para>获取代码运行时被禁用时的本地化说明文本；未禁用时为空字符串。</para>
    /// </summary>
    public string CodeRuntimeDisabledText => CodeRuntimeDisabled
        ? LanguageService.Instance.GetString("Plugin_CodeRuntimeDisabled")
        : string.Empty;

    /// <summary>Installed composite nodes available for isolated graph editing.</summary>
    public ObservableCollection<VisualPluginRegistration> EditableVisualPlugins { get; } = [];

    /// <summary>The composite node selected in the property-pane plugin workspace.</summary>
    public VisualPluginRegistration? SelectedVisualPlugin
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Initializes the workspace view model and subscribes to plugin manager changes.
    /// <para>初始化工作区视图模型并订阅插件管理器变更。</para>
    /// </summary>
    /// <param name="showWorkspace">
    /// Whether the property pane exposes the plugin workspace.
    /// <para>是否在属性面板中暴露插件工作区。</para>
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