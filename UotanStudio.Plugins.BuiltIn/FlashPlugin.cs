using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Plugin;

namespace UotanStudio.Plugins.BuiltIn
{
    /// <summary>
    /// <para>内置烧录节点插件，提供设备连接、分区刷入、擦除、重启等节点定义。</para>
    /// Built-in flash node plugin, providing device connect, partition flash, erase, reboot node definitions.
    /// </summary>
    [Plugin("uotanstudio.builtin.flash",
        DisplayName = "Built-in Flash Nodes",
        Version = "1.0.0",
        Author = "UotanStudio",
        Description = "Device connect, partition flash, erase, reboot, bootloader, OTA sideload, overwrite nodes.")]
    public sealed class FlashPlugin : IPlugin
    {
        /// <inheritdoc />
        public string Id => "uotanstudio.builtin.flash";

        /// <inheritdoc />
        public string DisplayName => "Built-in Flash Nodes";

        /// <inheritdoc />
        public string Version => "1.0.0";

        /// <inheritdoc />
        public string Author => "UotanStudio";

        /// <inheritdoc />
        public string Description => "Device connect, partition flash, erase, reboot, bootloader, OTA sideload, overwrite nodes.";

        /// <inheritdoc />
        public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.RegisterNodeDefinition(new Flash.ConnectNodeDefinition());
            context.RegisterNodeDefinition(new Flash.FlashPartitionNodeDefinition());
            context.RegisterNodeDefinition(new Flash.EraseNodeDefinition());
            context.RegisterNodeDefinition(new Flash.RebootNodeDefinition());
            context.RegisterNodeDefinition(new Flash.FlashBootloaderNodeDefinition());
            context.RegisterNodeDefinition(new Flash.EraseAllNodeDefinition());
            context.RegisterNodeDefinition(new Flash.OtaSideloadNodeDefinition());
            context.RegisterNodeDefinition(new Flash.OverwriteFirmwareNodeDefinition());
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
