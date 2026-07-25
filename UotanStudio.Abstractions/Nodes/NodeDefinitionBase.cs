using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Abstractions.Nodes
{
    /// <summary>
    /// <para>节点定义基类，简化内置节点定义的实现。</para>
    /// Base class for node definitions, simplifying built-in node definition implementation.
    /// </summary>
    public abstract class NodeDefinitionBase : INodeDefinition
    {
        private readonly List<ConnectorDefinition> _connectors = new();

        /// <inheritdoc />
        public abstract string TypeId { get; }

        /// <inheritdoc />
        public abstract string DisplayName { get; }

        /// <inheritdoc />
        public abstract NodeCategory Category { get; }

        /// <inheritdoc />
        public virtual string Description => string.Empty;

        /// <inheritdoc />
        public abstract string PluginId { get; }

        /// <inheritdoc />
        public IReadOnlyList<ConnectorDefinition> Connectors => _connectors;

        /// <inheritdoc />
        public abstract INodeInstance CreateInstance();

        /// <summary>
        /// <para>添加一个输入端口。</para>
        /// Adds an input port.
        /// </summary>
        protected void AddInput(string title, string type = WellKnownConnectorTypes.Any, bool allowMultiple = false)
            => _connectors.Add(ConnectorDefinition.Input(title, type, allowMultiple));

        /// <summary>
        /// <para>添加一个输出端口。</para>
        /// Adds an output port.
        /// </summary>
        protected void AddOutput(string title, string type = WellKnownConnectorTypes.Any, bool allowMultiple = true)
            => _connectors.Add(ConnectorDefinition.Output(title, type, allowMultiple));
    }

    /// <summary>
    /// <para>节点实例基类，提供输入/输出值存储与状态管理。</para>
    /// Base class for node instances, providing input/output value storage and state management.
    /// </summary>
    public abstract class NodeInstanceBase : INodeInstance
    {
        private readonly Dictionary<string, object?> _inputs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object?> _outputs = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public abstract INodeDefinition Definition { get; }

        /// <inheritdoc />
        public string InstanceId { get; } = Guid.NewGuid().ToString("N");

        /// <inheritdoc />
        public NodeExecutionStatus Status { get; set; } = NodeExecutionStatus.Idle;

        /// <inheritdoc />
        public string? ErrorMessage { get; set; }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> InputValues => _inputs;

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> OutputValues => _outputs;

        /// <inheritdoc />
        public void SetInput(string connectorTitle, object? value)
            => _inputs[connectorTitle] = value;

        /// <inheritdoc />
        public void SetOutput(string connectorTitle, object? value)
            => _outputs[connectorTitle] = value;

        /// <inheritdoc />
        public virtual string? ValidateInputs() => null;

        /// <inheritdoc />
        public abstract ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default);

        /// <summary>
        /// <para>便捷获取输入值，提供默认值。</para>
        /// Convenience getter for input values, providing a default.
        /// </summary>
        protected T? GetInput<T>(string connectorTitle, T? defaultValue = default)
        {
            if (_inputs.TryGetValue(connectorTitle, out var v) && v is T typed) return typed;
            return defaultValue;
        }

        /// <summary>
        /// <para>便捷获取输入字符串。</para>
        /// Convenience getter for input strings.
        /// </summary>
        protected string? GetInputString(string connectorTitle)
        {
            if (_inputs.TryGetValue(connectorTitle, out var v))
            {
                return v?.ToString();
            }
            return null;
        }
    }
}
