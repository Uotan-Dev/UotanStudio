using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Nodes;

namespace UotanStudio.Plugins.BuiltIn.Flow
{
    /// <summary>
    /// <para>开始节点定义（工作流入口）。</para>
    /// Start node definition (workflow entry point).
    /// </summary>
    public sealed class StartNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flow.start";

        /// <inheritdoc />
        public override string DisplayName => "Start";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flow;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flow";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new StartNodeInstance();

        /// <summary>
        /// <para>开始节点实例。</para>
        /// Start node instance.
        /// </summary>
        public sealed class StartNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new StartNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flow", "Workflow started");
                await Task.CompletedTask.ConfigureAwait(false);
                SetOutput("Flow", "start");
            }
        }
    }

    /// <summary>
    /// <para>结束节点定义（工作流出口）。</para>
    /// End node definition (workflow exit point).
    /// </summary>
    public sealed class EndNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flow.end";

        /// <inheritdoc />
        public override string DisplayName => "End";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flow;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flow";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new EndNodeInstance();

        /// <summary>
        /// <para>结束节点实例。</para>
        /// End node instance.
        /// </summary>
        public sealed class EndNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new EndNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flow", "Workflow ended");
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <para>条件分支节点定义。</para>
    /// Branch node definition.
    /// </summary>
    public sealed class BranchNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flow.branch";

        /// <inheritdoc />
        public override string DisplayName => "Branch";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flow;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flow";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new BranchNodeInstance();

        /// <summary>
        /// <para>条件分支节点实例。</para>
        /// Branch node instance.
        /// </summary>
        public sealed class BranchNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new BranchNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var condition = GetInput<object?>("Condition", null);
                var isTrue = condition is bool b && b;
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flow", $"Branch condition: {condition} → {(isTrue ? "True" : "False")}");
                await Task.CompletedTask.ConfigureAwait(false);
                SetOutput(isTrue ? "True" : "False", "branch");
            }
        }
    }

    /// <summary>
    /// <para>循环节点定义。</para>
    /// Loop node definition.
    /// </summary>
    public sealed class LoopNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flow.loop";

        /// <inheritdoc />
        public override string DisplayName => "Loop";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flow;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flow";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new LoopNodeInstance();

        /// <summary>
        /// <para>循环节点实例。</para>
        /// Loop node instance.
        /// </summary>
        public sealed class LoopNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new LoopNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var count = GetInput<int>("Count", 0);
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flow", $"Loop iteration starting, count={count}");
                await Task.CompletedTask.ConfigureAwait(false);
                SetOutput("Body", "loop-body");
                SetOutput("Index", 0);
            }
        }
    }

    /// <summary>
    /// <para>延迟节点定义。</para>
    /// Delay node definition.
    /// </summary>
    public sealed class DelayNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flow.delay";

        /// <inheritdoc />
        public override string DisplayName => "Delay";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flow;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flow";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new DelayNodeInstance();

        /// <summary>
        /// <para>延迟节点实例。</para>
        /// Delay node instance.
        /// </summary>
        public sealed class DelayNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new DelayNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var ms = GetInput<int>("Milliseconds", 100);
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flow", $"Delaying for {ms}ms");
                await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
                SetOutput("Flow", "delayed");
            }
        }
    }

    /// <summary>
    /// <para>并行执行节点定义。</para>
    /// Parallel node definition.
    /// </summary>
    public sealed class ParallelNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flow.parallel";

        /// <inheritdoc />
        public override string DisplayName => "Parallel";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flow;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flow";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new ParallelNodeInstance();

        /// <summary>
        /// <para>并行执行节点实例。</para>
        /// Parallel node instance.
        /// </summary>
        public sealed class ParallelNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new ParallelNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flow", "Parallel branches starting");
                await Task.CompletedTask.ConfigureAwait(false);
                SetOutput("Branch A", "parallel-a");
                SetOutput("Branch B", "parallel-b");
            }
        }
    }

    /// <summary>
    /// <para>顺序执行节点定义。</para>
    /// Sequence node definition.
    /// </summary>
    public sealed class SequenceNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flow.sequence";

        /// <inheritdoc />
        public override string DisplayName => "Sequence";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flow;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flow";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new SequenceNodeInstance();

        /// <summary>
        /// <para>顺序执行节点实例。</para>
        /// Sequence node instance.
        /// </summary>
        public sealed class SequenceNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new SequenceNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flow", "Sequence inputs collected");
                await Task.CompletedTask.ConfigureAwait(false);
                SetOutput("Flow", "sequenced");
            }
        }
    }

    /// <summary>
    /// <para>终止节点定义。</para>
    /// Terminate node definition.
    /// </summary>
    public sealed class TerminateNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flow.terminate";

        /// <inheritdoc />
        public override string DisplayName => "Terminate";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flow;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flow";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new TerminateNodeInstance();

        /// <summary>
        /// <para>终止节点实例。</para>
        /// Terminate node instance.
        /// </summary>
        public sealed class TerminateNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new TerminateNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var reason = GetInputString("Reason") ?? "unspecified";
                context.Log.Write(Abstractions.Logging.LogLevel.Warn, "Flow", $"Workflow terminated: {reason}");
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }
}
