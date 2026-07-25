using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Nodes;

namespace UotanStudio.Plugins.BuiltIn.Output
{
    /// <summary>
    /// <para>日志输出节点定义。</para>
    /// Log output node definition.
    /// </summary>
    public sealed class LogNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "output.log";

        /// <inheritdoc />
        public override string DisplayName => "Log Output";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Output;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.output";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new LogNodeInstance();

        /// <summary>
        /// <para>日志输出节点实例。</para>
        /// Log output node instance.
        /// </summary>
        public sealed class LogNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new LogNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var msg = GetInputString("Message") ?? "(no message)";
                var level = GetInputString("Level") ?? "Info";
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Output", $"[LogNode:{level}] {msg}");
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <para>进度报告节点定义。</para>
    /// Progress report node definition.
    /// </summary>
    public sealed class ProgressNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "output.progress";

        /// <inheritdoc />
        public override string DisplayName => "Progress Report";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Output;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.output";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new ProgressNodeInstance();

        /// <summary>
        /// <para>进度报告节点实例。</para>
        /// Progress report node instance.
        /// </summary>
        public sealed class ProgressNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new ProgressNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var progress = GetInput<double>("Progress", 0.0);
                var status = GetInputString("Status") ?? "running";
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Output", $"[Progress] {progress:P1} — {status}");
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <para>结果保存节点定义。</para>
    /// Save result node definition.
    /// </summary>
    public sealed class SaveNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "output.save";

        /// <inheritdoc />
        public override string DisplayName => "Save Result";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Output;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.output";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new SaveNodeInstance();

        /// <summary>
        /// <para>结果保存节点实例。</para>
        /// Save result node instance.
        /// </summary>
        public sealed class SaveNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new SaveNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var data = GetInput<object?>("Data", null);
                var path = GetInputString("Path");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Output", $"[Save] data='{data}', path='{path}'");
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <para>通知节点定义。</para>
    /// Notify node definition.
    /// </summary>
    public sealed class NotifyNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "output.notify";

        /// <inheritdoc />
        public override string DisplayName => "Notify";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Output;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.output";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new NotifyNodeInstance();

        /// <summary>
        /// <para>通知节点实例。</para>
        /// Notify node instance.
        /// </summary>
        public sealed class NotifyNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new NotifyNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var msg = GetInputString("Message") ?? "(no message)";
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Output", $"[Notify] {msg}");
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <para>邮件发送节点定义。</para>
    /// Email send node definition.
    /// </summary>
    public sealed class EmailNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "output.email";

        /// <inheritdoc />
        public override string DisplayName => "Send Email";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Output;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.output";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new EmailNodeInstance();

        /// <summary>
        /// <para>邮件发送节点实例。</para>
        /// Email send node instance.
        /// </summary>
        public sealed class EmailNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new EmailNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var to = GetInputString("To");
                var subject = GetInputString("Subject") ?? "(no subject)";
                var body = GetInputString("Body") ?? string.Empty;
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Output", $"[Email] to={to}, subject='{subject}', body-len={body.Length}");
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <para>控制台输出节点定义。</para>
    /// Console output node definition.
    /// </summary>
    public sealed class ConsoleNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "output.console";

        /// <inheritdoc />
        public override string DisplayName => "Console Output";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Output;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.output";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new ConsoleNodeInstance();

        /// <summary>
        /// <para>控制台输出节点实例。</para>
        /// Console output node instance.
        /// </summary>
        public sealed class ConsoleNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new ConsoleNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var text = GetInputString("Text") ?? string.Empty;
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Output", $"[Console] {text}");
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <para>文件导出节点定义。</para>
    /// File export node definition.
    /// </summary>
    public sealed class FileExportNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "output.file_export";

        /// <inheritdoc />
        public override string DisplayName => "File Export";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Output;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.output";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new FileExportNodeInstance();

        /// <summary>
        /// <para>文件导出节点实例。</para>
        /// File export node instance.
        /// </summary>
        public sealed class FileExportNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new FileExportNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var data = GetInput<object?>("Data", null);
                var path = GetInputString("Path");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Output", $"[FileExport] data='{data}', path='{path}'");
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }
}
