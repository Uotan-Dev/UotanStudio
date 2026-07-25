using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Plugin;

namespace UotanStudio.Plugins.BuiltIn
{
    /// <summary>
    /// <para>内置输出节点插件，提供日志输出、进度报告、结果保存、通知、邮件、控制台、文件导出节点定义。</para>
    /// Built-in output plugin, providing log, progress, save, notify, email, console, file-export node definitions.
    /// </summary>
    [Plugin("uotanstudio.builtin.output",
        DisplayName = "Built-in Output Nodes",
        Version = "1.0.0",
        Author = "UotanStudio",
        Description = "Log, progress, save, notify, email, console, file-export output nodes.")]
    public sealed class OutputPlugin : IPlugin
    {
        /// <inheritdoc />
        public string Id => "uotanstudio.builtin.output";

        /// <inheritdoc />
        public string DisplayName => "Built-in Output Nodes";

        /// <inheritdoc />
        public string Version => "1.0.0";

        /// <inheritdoc />
        public string Author => "UotanStudio";

        /// <inheritdoc />
        public string Description => "Log, progress, save, notify, email, console, file-export output nodes.";

        /// <inheritdoc />
        public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.RegisterNodeDefinition(new Output.LogNodeDefinition());
            context.RegisterNodeDefinition(new Output.ProgressNodeDefinition());
            context.RegisterNodeDefinition(new Output.SaveNodeDefinition());
            context.RegisterNodeDefinition(new Output.NotifyNodeDefinition());
            context.RegisterNodeDefinition(new Output.EmailNodeDefinition());
            context.RegisterNodeDefinition(new Output.ConsoleNodeDefinition());
            context.RegisterNodeDefinition(new Output.FileExportNodeDefinition());
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
