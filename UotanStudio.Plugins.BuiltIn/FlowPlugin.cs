using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Plugin;

namespace UotanStudio.Plugins.BuiltIn
{
    /// <summary>
    /// <para>内置流程控制节点插件，提供开始、结束、分支、循环、延迟、并行、顺序、终止节点定义。</para>
    /// Built-in flow control plugin, providing start, end, branch, loop, delay, parallel, sequence, terminate node definitions.
    /// </summary>
    [Plugin("uotanstudio.builtin.flow",
        DisplayName = "Built-in Flow Nodes",
        Version = "1.0.0",
        Author = "UotanStudio",
        Description = "Start, end, branch, loop, delay, parallel, sequence, terminate flow control nodes.")]
    public sealed class FlowPlugin : IPlugin
    {
        /// <inheritdoc />
        public string Id => "uotanstudio.builtin.flow";

        /// <inheritdoc />
        public string DisplayName => "Built-in Flow Nodes";

        /// <inheritdoc />
        public string Version => "1.0.0";

        /// <inheritdoc />
        public string Author => "UotanStudio";

        /// <inheritdoc />
        public string Description => "Start, end, branch, loop, delay, parallel, sequence, terminate flow control nodes.";

        /// <inheritdoc />
        public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.RegisterNodeDefinition(new Flow.StartNodeDefinition());
            context.RegisterNodeDefinition(new Flow.EndNodeDefinition());
            context.RegisterNodeDefinition(new Flow.BranchNodeDefinition());
            context.RegisterNodeDefinition(new Flow.LoopNodeDefinition());
            context.RegisterNodeDefinition(new Flow.DelayNodeDefinition());
            context.RegisterNodeDefinition(new Flow.ParallelNodeDefinition());
            context.RegisterNodeDefinition(new Flow.SequenceNodeDefinition());
            context.RegisterNodeDefinition(new Flow.TerminateNodeDefinition());
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
