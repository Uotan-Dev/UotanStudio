using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Nodes;

namespace UotanStudio.Abstractions.Plugin
{
    /// <summary>
    /// <para>插件注册表。统一管理所有插件的发现、加载、卸载，以及节点定义的聚合。</para>
    /// Plugin registry. Manages discovery, loading, unloading of all plugins, and aggregates node definitions.
    /// </summary>
    /// <remarks>
    /// <para>AOT 策略：内置插件通过 <see cref="RegisterBuiltIn"/> 在编译期注册工厂委托；不使用 <c>Assembly.LoadFrom</c> 反射。</para>
    /// AOT strategy: built-in plugins register their factory delegates at compile time via <see cref="RegisterBuiltIn"/>; no <c>Assembly.LoadFrom</c> reflection is used.
    /// </remarks>
    public sealed class PluginRegistry
    {
        private readonly List<PluginFactoryDelegate> _builtInFactories = new();
        private readonly List<IPlugin> _loadedPlugins = new();
        private readonly List<INodeDefinition> _allNodeDefinitions = new();
        private readonly Dictionary<string, PluginInfo> _pluginInfos = new();
        private readonly HashSet<string> _disabledPluginIds = new();
        private bool _initialized;

        /// <summary>
        /// <para>单例实例。</para>
        /// Singleton instance.
        /// </summary>
        public static PluginRegistry Instance { get; } = new();

        private PluginRegistry() { }

        /// <summary>
        /// <para>已加载的插件元数据快照（只读）。</para>
        /// Loaded plugin metadata snapshots (read-only).
        /// </summary>
        public IReadOnlyList<PluginInfo> PluginInfos
        {
            get
            {
                lock (_pluginInfos)
                {
                    return new List<PluginInfo>(_pluginInfos.Values);
                }
            }
        }

        /// <summary>
        /// <para>所有已注册节点定义（只读）。</para>
        /// All registered node definitions (read-only).
        /// </summary>
        public IReadOnlyList<INodeDefinition> NodeDefinitions
        {
            get
            {
                lock (_allNodeDefinitions)
                {
                    return new List<INodeDefinition>(_allNodeDefinitions);
                }
            }
        }

        /// <summary>
        /// <para>注册一个内置插件工厂委托（编译期已知，AOT 安全）。</para>
        /// Registers a built-in plugin factory delegate (known at compile time, AOT-safe).
        /// </summary>
        /// <param name="factory">
        /// <para>返回 <see cref="IPlugin"/> 实例的工厂委托。</para>
        /// Factory delegate returning an <see cref="IPlugin"/> instance.
        /// </param>
        public void RegisterBuiltIn(PluginFactoryDelegate factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            if (_initialized)
            {
                throw new InvalidOperationException("Cannot register built-in plugins after initialization.");
            }
            _builtInFactories.Add(factory);
        }

        /// <summary>
        /// <para>禁用某个插件（在下次 <see cref="InitializeAsync"/> 时生效）。</para>
        /// Disables a plugin (takes effect on the next <see cref="InitializeAsync"/>).
        /// </summary>
        public void DisablePlugin(string pluginId)
        {
            lock (_disabledPluginIds)
            {
                _disabledPluginIds.Add(pluginId);
            }
        }

        /// <summary>
        /// <para>启用某个插件（在下次 <see cref="InitializeAsync"/> 时生效）。</para>
        /// Enables a plugin (takes effect on the next <see cref="InitializeAsync"/>).
        /// </summary>
        public void EnablePlugin(string pluginId)
        {
            lock (_disabledPluginIds)
            {
                _disabledPluginIds.Remove(pluginId);
            }
        }

        /// <summary>
        /// <para>判断某插件是否被禁用。</para>
        /// Whether a plugin is disabled.
        /// </summary>
        public bool IsDisabled(string pluginId)
        {
            lock (_disabledPluginIds)
            {
                return _disabledPluginIds.Contains(pluginId);
            }
        }

        /// <summary>
        /// <para>异步初始化所有已注册插件，聚合节点定义。</para>
        /// Asynchronously initializes all registered plugins and aggregates node definitions.
        /// </summary>
        /// <param name="contextFactory">
        /// <para>为每个插件构造 <see cref="IPluginContext"/> 的工厂委托。</para>
        /// Factory delegate that constructs an <see cref="IPluginContext"/> for each plugin.
        /// </param>
        /// <param name="cancellationToken">
        /// <para>取消令牌。</para>
        /// Cancellation token.
        /// </param>
        public async ValueTask InitializeAsync(
            Func<IPlugin, IPluginContext> contextFactory,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(contextFactory);
            _initialized = true;

            foreach (var factory in _builtInFactories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IPlugin? plugin;
                try
                {
                    plugin = factory();
                }
                catch (Exception ex)
                {
                    var failInfo = new PluginInfo
                    {
                        Id = $"unknown-{Guid.NewGuid():N}",
                        DisplayName = "Unknown",
                        Version = "0.0.0",
                        Author = "",
                        Description = "",
                        IsBuiltIn = true,
                        IsLoaded = false,
                        LoadError = ex.Message
                    };
                    lock (_pluginInfos) { _pluginInfos[failInfo.Id] = failInfo; }
                    continue;
                }

                var info = new PluginInfo
                {
                    Id = plugin.Id,
                    DisplayName = plugin.DisplayName,
                    Version = plugin.Version,
                    Author = plugin.Author,
                    Description = plugin.Description,
                    IsBuiltIn = true,
                    IsLoaded = false
                };

                if (IsDisabled(plugin.Id))
                {
                    info.IsLoaded = false;
                    info.LoadError = "Disabled by user";
                    lock (_pluginInfos) { _pluginInfos[info.Id] = info; }
                    continue;
                }

                try
                {
                    var ctx = contextFactory(plugin);
                    await plugin.InitializeAsync(ctx, cancellationToken).ConfigureAwait(false);

                    lock (_loadedPlugins) { _loadedPlugins.Add(plugin); }
                    lock (_allNodeDefinitions)
                    {
                        foreach (var def in ctx.NodeDefinitions)
                        {
                            _allNodeDefinitions.Add(def);
                        }
                    }

                    info.IsLoaded = true;
                    lock (_pluginInfos) { _pluginInfos[info.Id] = info; }
                }
                catch (Exception ex)
                {
                    info.IsLoaded = false;
                    info.LoadError = ex.Message;
                    lock (_pluginInfos) { _pluginInfos[info.Id] = info; }
                }
            }
        }

        /// <summary>
        /// <para>异步关闭所有已加载插件，释放资源。</para>
        /// Asynchronously shuts down all loaded plugins, releasing resources.
        /// </summary>
        public async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
        {
            List<IPlugin> snapshot;
            lock (_loadedPlugins)
            {
                snapshot = new List<IPlugin>(_loadedPlugins);
                _loadedPlugins.Clear();
            }

            foreach (var plugin in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await plugin.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略关闭异常，确保后续插件仍能关闭
                }
            }

            lock (_allNodeDefinitions) { _allNodeDefinitions.Clear(); }
            lock (_pluginInfos) { _pluginInfos.Clear(); }
            _initialized = false;
        }

        /// <summary>
        /// <para>按节点类型 ID 查找节点定义。</para>
        /// Finds a node definition by its type identifier.
        /// </summary>
        public INodeDefinition? FindNodeDefinition(string typeId)
        {
            lock (_allNodeDefinitions)
            {
                foreach (var def in _allNodeDefinitions)
                {
                    if (def.TypeId == typeId) return def;
                }
            }
            return null;
        }

        /// <summary>
        /// <para>按类别枚举节点定义。</para>
        /// Enumerates node definitions by category.
        /// </summary>
        public IReadOnlyList<INodeDefinition> GetNodeDefinitionsByCategory(NodeCategory category)
        {
            var result = new List<INodeDefinition>();
            lock (_allNodeDefinitions)
            {
                foreach (var def in _allNodeDefinitions)
                {
                    if (def.Category == category) result.Add(def);
                }
            }
            return result;
        }
    }
}
