using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Plugin;

namespace UotanStudio.Plugins.BuiltIn
{
    /// <summary>
    /// <para>内置固件节点插件，提供固件解包、打包、签名验证、分区提取等节点定义。</para>
    /// Built-in firmware node plugin, providing node definitions for unpack, pack, signature verify, and partition extract.
    /// </summary>
    [Plugin("uotanstudio.builtin.firmware",
        DisplayName = "Built-in Firmware Nodes",
        Version = "1.0.0",
        Author = "UotanStudio",
        Description = "Firmware unpack/pack/verify/extract and extended OTA/dual-slot/bootloader nodes.")]
    public sealed class FirmwarePlugin : IPlugin
    {
        /// <inheritdoc />
        public string Id => "uotanstudio.builtin.firmware";

        /// <inheritdoc />
        public string DisplayName => "Built-in Firmware Nodes";

        /// <inheritdoc />
        public string Version => "1.0.0";

        /// <inheritdoc />
        public string Author => "UotanStudio";

        /// <inheritdoc />
        public string Description => "Firmware unpack/pack/verify/extract and extended OTA/dual-slot/bootloader nodes.";

        /// <inheritdoc />
        public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.RegisterNodeDefinition(new Firmware.UnpackNodeDefinition());
            context.RegisterNodeDefinition(new Firmware.PackNodeDefinition());
            context.RegisterNodeDefinition(new Firmware.VerifyNodeDefinition());
            context.RegisterNodeDefinition(new Firmware.ExtractNodeDefinition());
            context.RegisterNodeDefinition(new Firmware.OtaDeltaNodeDefinition());
            context.RegisterNodeDefinition(new Firmware.MergeSignatureNodeDefinition());
            context.RegisterNodeDefinition(new Firmware.MergeDualSlotNodeDefinition());
            context.RegisterNodeDefinition(new Firmware.FlashBootNodeDefinition());
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
