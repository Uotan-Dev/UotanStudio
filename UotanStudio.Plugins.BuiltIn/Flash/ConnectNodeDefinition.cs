using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Nodes;

namespace UotanStudio.Plugins.BuiltIn.Flash
{
    /// <summary>
    /// <para>设备连接节点定义。</para>
    /// Device connect node definition.
    /// </summary>
    public sealed class ConnectNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flash.connect";

        /// <inheritdoc />
        public override string DisplayName => "Connect Device";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flash;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flash";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new ConnectNodeInstance();

        /// <summary>
        /// <para>设备连接节点实例。</para>
        /// Device connect node instance.
        /// </summary>
        public sealed class ConnectNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new ConnectNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var mode = GetInputString("Mode") ?? "adb";
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flash", $"Connecting device in mode '{mode}'");
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                SetOutput("Device", $"device-{mode}");
                SetOutput("Status", "Connected");
            }
        }
    }

    /// <summary>
    /// <para>分区刷入节点定义。</para>
    /// Partition flash node definition.
    /// </summary>
    public sealed class FlashPartitionNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flash.partition";

        /// <inheritdoc />
        public override string DisplayName => "Flash Partition";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flash;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flash";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new FlashPartitionNodeInstance();

        /// <summary>
        /// <para>分区刷入节点实例。</para>
        /// Partition flash node instance.
        /// </summary>
        public sealed class FlashPartitionNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new FlashPartitionNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var device = GetInputString("Device");
                var partition = GetInputString("Partition");
                var image = GetInputString("Image");
                if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(partition) || string.IsNullOrEmpty(image))
                {
                    ErrorMessage = "Missing device, partition or image";
                    return;
                }

                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flash", $"Flashing partition '{partition}' from '{image}' to '{device}'");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                SetOutput("Result", "Flashed");
            }
        }
    }

    /// <summary>
    /// <para>分区擦除节点定义。</para>
    /// Partition erase node definition.
    /// </summary>
    public sealed class EraseNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flash.erase";

        /// <inheritdoc />
        public override string DisplayName => "Erase Partition";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flash;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flash";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new EraseNodeInstance();

        /// <summary>
        /// <para>分区擦除节点实例。</para>
        /// Partition erase node instance.
        /// </summary>
        public sealed class EraseNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new EraseNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var device = GetInputString("Device");
                var partition = GetInputString("Partition");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flash", $"Erasing partition '{partition}' on '{device}'");
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                SetOutput("Result", "Erased");
            }
        }
    }

    /// <summary>
    /// <para>设备重启节点定义。</para>
    /// Device reboot node definition.
    /// </summary>
    public sealed class RebootNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flash.reboot";

        /// <inheritdoc />
        public override string DisplayName => "Reboot Device";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flash;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flash";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new RebootNodeInstance();

        /// <summary>
        /// <para>设备重启节点实例。</para>
        /// Device reboot node instance.
        /// </summary>
        public sealed class RebootNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new RebootNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var device = GetInputString("Device");
                var mode = GetInputString("Mode") ?? "normal";
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flash", $"Rebooting '{device}' to mode '{mode}'");
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                SetOutput("Status", "Rebooting");
            }
        }
    }

    /// <summary>
    /// <para>Bootloader 刷入节点定义。</para>
    /// Bootloader flash node definition.
    /// </summary>
    public sealed class FlashBootloaderNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flash.bootloader";

        /// <inheritdoc />
        public override string DisplayName => "Flash Bootloader";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flash;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flash";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new FlashBootloaderNodeInstance();

        /// <summary>
        /// <para>Bootloader 刷入节点实例。</para>
        /// Bootloader flash node instance.
        /// </summary>
        public sealed class FlashBootloaderNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new FlashBootloaderNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var device = GetInputString("Device");
                var bootloader = GetInputString("Bootloader");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flash", $"Flashing bootloader '{bootloader}' to '{device}'");
                await Task.Delay(600, cancellationToken).ConfigureAwait(false);
                SetOutput("Status", "Bootloader flashed");
            }
        }
    }

    /// <summary>
    /// <para>全部擦除节点定义。</para>
    /// Erase all node definition.
    /// </summary>
    public sealed class EraseAllNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flash.erase_all";

        /// <inheritdoc />
        public override string DisplayName => "Erase All";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flash;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flash";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new EraseAllNodeInstance();

        /// <summary>
        /// <para>全部擦除节点实例。</para>
        /// Erase all node instance.
        /// </summary>
        public sealed class EraseAllNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new EraseAllNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var device = GetInputString("Device");
                context.Log.Write(Abstractions.Logging.LogLevel.Warn, "Flash", $"Erasing ALL partitions on '{device}'");
                await Task.Delay(800, cancellationToken).ConfigureAwait(false);
                SetOutput("Status", "Erased all");
            }
        }
    }

    /// <summary>
    /// <para>OTA 侧载节点定义。</para>
    /// OTA sideload node definition.
    /// </summary>
    public sealed class OtaSideloadNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flash.ota_sideload";

        /// <inheritdoc />
        public override string DisplayName => "OTA Sideload";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flash;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flash";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new OtaSideloadNodeInstance();

        /// <summary>
        /// <para>OTA 侧载节点实例。</para>
        /// OTA sideload node instance.
        /// </summary>
        public sealed class OtaSideloadNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new OtaSideloadNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var device = GetInputString("Device");
                var ota = GetInputString("OTA Package");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flash", $"Sideloading OTA package '{ota}' to '{device}'");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                SetOutput("Status", "Sideloaded");
            }
        }
    }

    /// <summary>
    /// <para>固件覆盖节点定义。</para>
    /// Firmware overwrite node definition.
    /// </summary>
    public sealed class OverwriteFirmwareNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "flash.overwrite_firmware";

        /// <inheritdoc />
        public override string DisplayName => "Overwrite Firmware";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Flash;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.flash";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new OverwriteFirmwareNodeInstance();

        /// <summary>
        /// <para>固件覆盖节点实例。</para>
        /// Firmware overwrite node instance.
        /// </summary>
        public sealed class OverwriteFirmwareNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new OverwriteFirmwareNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var device = GetInputString("Device");
                var firmware = GetInputString("Firmware");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Flash", $"Overwriting firmware '{firmware}' on '{device}'");
                await Task.Delay(700, cancellationToken).ConfigureAwait(false);
                SetOutput("Status", "Firmware overwritten");
            }
        }
    }
}
