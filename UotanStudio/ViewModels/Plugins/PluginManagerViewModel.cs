using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Logging;
using UotanStudio.Abstractions.Nodes;
using UotanStudio.Abstractions.Plugin;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Plugins
{
    /// <summary>
    /// <para>插件管理器视图模型，展示已加载插件及其提供的节点定义，支持启用/禁用与刷新。</para>
    /// Plugin manager view model, showing loaded plugins and their node definitions, with enable/disable and refresh support.
    /// </summary>
    public partial class PluginManagerViewModel : ViewModelBase
    {
        private readonly LogService _log = LogService.Instance;
        private PluginInfo? _selectedPlugin;

        /// <summary>
        /// <para>已注册的插件元数据列表。</para>
        /// List of registered plugin metadata.
        /// </summary>
        public ObservableCollection<PluginInfo> Plugins { get; } = new();

        /// <summary>
        /// <para>当前选中插件提供的节点定义列表。</para>
        /// List of node definitions provided by the currently selected plugin.
        /// </summary>
        public ObservableCollection<INodeDefinition> SelectedPluginNodes { get; } = new();

        /// <summary>
        /// <para>当前选中的插件。</para>
        /// Currently selected plugin.
        /// </summary>
        public PluginInfo? SelectedPlugin
        {
            get => _selectedPlugin;
            set
            {
                if (SetProperty(ref _selectedPlugin, value))
                {
                    RefreshSelectedPluginNodes();
                }
            }
        }

        /// <summary>
        /// <para>已加载插件数量。</para>
        /// Number of loaded plugins.
        /// </summary>
        public int LoadedCount { get; private set; }

        /// <summary>
        /// <para>总节点定义数量。</para>
        /// Total number of node definitions.
        /// </summary>
        public int TotalNodes { get; private set; }

        /// <summary>
        /// <para>构造插件管理器视图模型，自动刷新插件列表。</para>
        /// Constructs the plugin manager view model, automatically refreshing the plugin list.
        /// </summary>
        public PluginManagerViewModel()
        {
            _ = RefreshAsync();
        }

        /// <summary>
        /// <para>异步刷新插件与节点定义列表。</para>
        /// Asynchronously refreshes the plugin and node-definition lists.
        /// </summary>
        [RelayCommand]
        public async Task RefreshAsync()
        {
            var registry = PluginRegistry.Instance;
            var infos = registry.PluginInfos;
            Plugins.Clear();
            foreach (var info in infos) Plugins.Add(info);

            var defs = registry.NodeDefinitions;
            TotalNodes = defs.Count;
            LoadedCount = infos.Count;
            OnPropertyChanged(nameof(LoadedCount));
            OnPropertyChanged(nameof(TotalNodes));

            await Task.CompletedTask.ConfigureAwait(false);
            _log.Write(LogLevel.Info, "PluginMgr", $"Refreshed: {LoadedCount} plugins, {TotalNodes} node definitions");
        }

        /// <summary>
        /// <para>禁用当前选中插件（下次初始化生效）。</para>
        /// Disables the currently selected plugin (takes effect on next initialization).
        /// </summary>
        [RelayCommand]
        private void DisableSelected()
        {
            if (SelectedPlugin == null) return;
            PluginRegistry.Instance.DisablePlugin(SelectedPlugin.Id);
            _log.Write(LogLevel.Warn, "PluginMgr", $"Disabled plugin: {SelectedPlugin.Id} (effective on next init)");
        }

        /// <summary>
        /// <para>启用当前选中插件。</para>
        /// Enables the currently selected plugin.
        /// </summary>
        [RelayCommand]
        private void EnableSelected()
        {
            if (SelectedPlugin == null) return;
            PluginRegistry.Instance.EnablePlugin(SelectedPlugin.Id);
            _log.Write(LogLevel.Info, "PluginMgr", $"Enabled plugin: {SelectedPlugin.Id} (effective on next init)");
        }

        private void RefreshSelectedPluginNodes()
        {
            SelectedPluginNodes.Clear();
            if (SelectedPlugin == null) return;

            foreach (var def in PluginRegistry.Instance.NodeDefinitions)
            {
                if (def.PluginId == SelectedPlugin.Id)
                {
                    SelectedPluginNodes.Add(def);
                }
            }
        }
    }
}
