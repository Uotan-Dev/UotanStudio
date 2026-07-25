using System.Collections.Generic;
using UotanStudio.Abstractions.Logging;
using UotanStudio.Abstractions.Nodes;

namespace UotanStudio.Abstractions.Plugin
{
    /// <summary>
    /// <para>默认 <see cref="IPluginContext"/> 实现，收集节点定义并桥接统一日志。</para>
    /// Default <see cref="IPluginContext"/> implementation, collecting node definitions and bridging the unified log.
    /// </summary>
    public sealed class DefaultPluginContext : IPluginContext
    {
        private readonly List<INodeDefinition> _definitions = new();
        private readonly IUnifiedLog _log;
        private readonly PluginLogAdapter _logAdapter;
        private readonly PluginRegistrar _registrar;

        /// <summary>
        /// <para>创建插件上下文。</para>
        /// Creates the plugin context.
        /// </summary>
        /// <param name="log">
        /// <para>统一日志服务。</para>
        /// Unified log service.
        /// </param>
        /// <param name="pluginId">
        /// <para>使用此上下文的插件 ID（用于日志来源标识）。</para>
        /// Plugin ID using this context (used as the log source tag).
        /// </param>
        public DefaultPluginContext(IUnifiedLog log, string pluginId)
        {
            _log = log;
            _logAdapter = new PluginLogAdapter(log, pluginId);
            _registrar = new PluginRegistrar();
        }

        /// <inheritdoc />
        public void RegisterNodeDefinition(INodeDefinition definition)
            => _registrar.RegisterNodeDefinition(definition);

        /// <inheritdoc />
        public IReadOnlyList<INodeDefinition> NodeDefinitions => _registrar.NodeDefinitions;

        /// <inheritdoc />
        public IPluginLog Log => _logAdapter;

        /// <inheritdoc />
        public T? GetService<T>() where T : class => _registrar.GetService<T>();
    }

    /// <summary>
    /// <para>将 <see cref="IUnifiedLog"/> 适配为 <see cref="IPluginLog"/>，自动填充来源为插件 ID。</para>
    /// Adapts <see cref="IUnifiedLog"/> to <see cref="IPluginLog"/>, automatically tagging the source as the plugin ID.
    /// </summary>
    public sealed class PluginLogAdapter : IPluginLog
    {
        private readonly IUnifiedLog _log;
        private readonly string _source;

        /// <summary>
        /// <para>创建适配器。</para>
        /// Creates the adapter.
        /// </summary>
        public PluginLogAdapter(IUnifiedLog log, string source)
        {
            _log = log;
            _source = source;
        }

        /// <inheritdoc />
        public void Trace(string message) => _log.Write(LogLevel.Trace, _source, message);

        /// <inheritdoc />
        public void Debug(string message) => _log.Write(LogLevel.Debug, _source, message);

        /// <inheritdoc />
        public void Info(string message) => _log.Write(LogLevel.Info, _source, message);

        /// <inheritdoc />
        public void Warn(string message) => _log.Write(LogLevel.Warn, _source, message);

        /// <inheritdoc />
        public void Error(string message) => _log.Write(LogLevel.Error, _source, message);
    }
}
