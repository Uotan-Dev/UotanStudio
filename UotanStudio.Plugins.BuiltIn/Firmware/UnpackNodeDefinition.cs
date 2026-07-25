using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Nodes;

namespace UotanStudio.Plugins.BuiltIn.Firmware
{
    /// <summary>
    /// <para>固件解包节点定义。</para>
    /// Firmware unpack node definition.
    /// </summary>
    public sealed class UnpackNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "firmware.unpack";

        /// <inheritdoc />
        public override string DisplayName => "Unpack Firmware";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Firmware;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.firmware";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new UnpackNodeInstance();

        /// <summary>
        /// <para>固件解包节点实例。</para>
        /// Firmware unpack node instance.
        /// </summary>
        public sealed class UnpackNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new UnpackNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var firmwarePath = GetInputString("Firmware Path");
                if (string.IsNullOrEmpty(firmwarePath))
                {
                    ErrorMessage = "Missing firmware path";
                    return;
                }

                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Firmware", $"Unpacking firmware: {firmwarePath}");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                SetOutput("Output Dir", firmwarePath + ".unpacked");
                SetOutput("Partitions", new[] { "boot", "system", "vendor" });
            }
        }
    }

    /// <summary>
    /// <para>固件打包节点定义。</para>
    /// Firmware pack node definition.
    /// </summary>
    public sealed class PackNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "firmware.pack";

        /// <inheritdoc />
        public override string DisplayName => "Pack Firmware";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Firmware;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.firmware";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new PackNodeInstance();

        /// <summary>
        /// <para>固件打包节点实例。</para>
        /// Firmware pack node instance.
        /// </summary>
        public sealed class PackNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new PackNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var inputDir = GetInputString("Input Dir");
                if (string.IsNullOrEmpty(inputDir))
                {
                    ErrorMessage = "Missing input directory";
                    return;
                }

                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Firmware", $"Packing firmware from: {inputDir}");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                SetOutput("Firmware", inputDir + ".img");
            }
        }
    }

    /// <summary>
    /// <para>固件签名验证节点定义。</para>
    /// Firmware signature verification node definition.
    /// </summary>
    public sealed class VerifyNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "firmware.verify";

        /// <inheritdoc />
        public override string DisplayName => "Verify Signature";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Firmware;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.firmware";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new VerifyNodeInstance();

        /// <summary>
        /// <para>固件签名验证节点实例。</para>
        /// Firmware signature verification node instance.
        /// </summary>
        public sealed class VerifyNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new VerifyNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var firmware = GetInputString("Firmware");
                if (string.IsNullOrEmpty(firmware))
                {
                    ErrorMessage = "Missing firmware input";
                    return;
                }

                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Firmware", $"Verifying signature: {firmware}");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                SetOutput("Valid", true);
                SetOutput("Report", "Signature valid.");
            }
        }
    }

    /// <summary>
    /// <para>固件分区提取节点定义。</para>
    /// Firmware partition extraction node definition.
    /// </summary>
    public sealed class ExtractNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "firmware.extract";

        /// <inheritdoc />
        public override string DisplayName => "Extract Partition";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Firmware;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.firmware";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new ExtractNodeInstance();

        /// <summary>
        /// <para>固件分区提取节点实例。</para>
        /// Firmware partition extraction node instance.
        /// </summary>
        public sealed class ExtractNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new ExtractNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var firmware = GetInputString("Firmware");
                var partition = GetInputString("Partition Name");
                if (string.IsNullOrEmpty(firmware) || string.IsNullOrEmpty(partition))
                {
                    ErrorMessage = "Missing firmware or partition name";
                    return;
                }

                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Firmware", $"Extracting partition '{partition}' from {firmware}");
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                SetOutput("Image", $"{firmware}.{partition}.img");
            }
        }
    }

    /// <summary>
    /// <para>OTA 差分包节点定义。</para>
    /// OTA delta package node definition.
    /// </summary>
    public sealed class OtaDeltaNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "firmware.ota_delta";

        /// <inheritdoc />
        public override string DisplayName => "OTA Delta";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Firmware;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.firmware";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new OtaDeltaNodeInstance();

        /// <summary>
        /// <para>OTA 差分包节点实例。</para>
        /// OTA delta package node instance.
        /// </summary>
        public sealed class OtaDeltaNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new OtaDeltaNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var source = GetInputString("Source");
                var target = GetInputString("Target");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Firmware", $"Generating OTA delta from {source} to {target}");
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                SetOutput("Delta", $"{source}.delta.bin");
            }
        }
    }

    /// <summary>
    /// <para>签名合并节点定义。</para>
    /// Merge-signature node definition.
    /// </summary>
    public sealed class MergeSignatureNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "firmware.merge_signature";

        /// <inheritdoc />
        public override string DisplayName => "Merge Signature";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Firmware;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.firmware";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new MergeSignatureNodeInstance();

        /// <summary>
        /// <para>签名合并节点实例。</para>
        /// Merge-signature node instance.
        /// </summary>
        public sealed class MergeSignatureNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new MergeSignatureNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var image = GetInputString("Image");
                var signature = GetInputString("Signature");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Firmware", $"Merging signature '{signature}' into image '{image}'");
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
                SetOutput("Signed", $"{image}.signed");
            }
        }
    }

    /// <summary>
    /// <para>双槽位合并节点定义。</para>
    /// Dual-slot merge node definition.
    /// </summary>
    public sealed class MergeDualSlotNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "firmware.merge_dual_slot";

        /// <inheritdoc />
        public override string DisplayName => "Merge Dual Slot";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Firmware;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.firmware";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new MergeDualSlotNodeInstance();

        /// <summary>
        /// <para>双槽位合并节点实例。</para>
        /// Dual-slot merge node instance.
        /// </summary>
        public sealed class MergeDualSlotNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new MergeDualSlotNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var slotA = GetInputString("Slot A");
                var slotB = GetInputString("Slot B");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Firmware", $"Merging dual slots: A={slotA}, B={slotB}");
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                SetOutput("Merged", "merged_dual_slot.img");
            }
        }
    }

    /// <summary>
    /// <para>spl/uboot 刷写节点定义。</para>
    /// Bootloader flash node definition.
    /// </summary>
    public sealed class FlashBootNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "firmware.flash_boot";

        /// <inheritdoc />
        public override string DisplayName => "Flash Bootloader";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Firmware;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.firmware";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new FlashBootNodeInstance();

        /// <summary>
        /// <para>spl/uboot 刷写节点实例。</para>
        /// Bootloader flash node instance.
        /// </summary>
        public sealed class FlashBootNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new FlashBootNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var bootloader = GetInputString("Bootloader");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Firmware", $"Flashing bootloader: {bootloader}");
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                SetOutput("Status", "Bootloader flashed");
            }
        }
    }
}
