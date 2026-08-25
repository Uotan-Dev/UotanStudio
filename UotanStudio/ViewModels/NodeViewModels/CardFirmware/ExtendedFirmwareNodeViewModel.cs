using System;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>扩展固件操作节点视图模型，提供 OTA 差分生成、签名合并、双卡合并与 spl/uboot 刷写功能。</para>
    /// Extended firmware operation node view model, providing OTA delta generation, signature merging, dual-slot merging, and spl/uboot flashing capabilities.
    /// </summary>
    public class ExtendedFirmwareNodeViewModel : FlowNodeViewModel
    {
        /// <summary>
        /// <para>创建具有指定标题的扩展固件操作节点。</para>
        /// Creates an extended firmware operation node with the specified title.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        public ExtendedFirmwareNodeViewModel(string title) : base(title, NodeCategory.Firmware)
        {
        }

        /// <summary>
        /// <para>创建默认的 OTA 差分包生成节点。</para>
        /// Creates a default OTA delta package generation node.
        /// </summary>
        public static ExtendedFirmwareNodeViewModel CreateOtaDeltaNode()
        {
            var node = new ExtendedFirmwareNodeViewModel("OTA Delta")
            {
                Location = new Point(100, 100),
                TypeKey = "Firmware.OtaDelta"
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Source", PortType = NodePortType.Path });
            node.Input.Add(new NodeConnectorViewModel { Title = "Target", PortType = NodePortType.Path });
            node.Output.Add(new NodeConnectorViewModel { Title = "Delta", PortType = NodePortType.Data });
            return node;
        }

        /// <summary>
        /// <para>创建默认的签名合并节点，将签名嵌入固件镜像。</para>
        /// Creates a default merge-signature node that embeds a signature into a firmware image.
        /// </summary>
        public static ExtendedFirmwareNodeViewModel CreateMergeSignatureNode()
        {
            var node = new ExtendedFirmwareNodeViewModel("Merge Signature")
            {
                Location = new Point(100, 100),
                TypeKey = "Firmware.MergeSignature"
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Image", PortType = NodePortType.Data });
            node.Input.Add(new NodeConnectorViewModel { Title = "Signature", PortType = NodePortType.Data });
            node.Output.Add(new NodeConnectorViewModel { Title = "Signed", PortType = NodePortType.Data });
            return node;
        }

        /// <summary>
        /// <para>创建默认的双卡合并节点，将 A/B 槽位合并为单一镜像。</para>
        /// Creates a default dual-slot merge node that combines A/B slots into a single image.
        /// </summary>
        public static ExtendedFirmwareNodeViewModel CreateMergeDualSlotNode()
        {
            var node = new ExtendedFirmwareNodeViewModel("Merge Dual Slot")
            {
                Location = new Point(100, 100),
                TypeKey = "Firmware.MergeDualSlot"
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Slot A", PortType = NodePortType.Data });
            node.Input.Add(new NodeConnectorViewModel { Title = "Slot B", PortType = NodePortType.Data });
            node.Output.Add(new NodeConnectorViewModel { Title = "Merged", PortType = NodePortType.Data });
            return node;
        }

        /// <summary>
        /// <para>创建默认的 spl/uboot 刷写节点。</para>
        /// Creates a default spl/uboot flashing node.
        /// </summary>
        public static ExtendedFirmwareNodeViewModel CreateFlashBootNode()
        {
            var node = new ExtendedFirmwareNodeViewModel("Flash Bootloader")
            {
                Location = new Point(100, 100),
                TypeKey = "Firmware.FlashBoot"
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Bootloader", PortType = NodePortType.Path });
            node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
            return node;
        }

        /// <summary>
        /// <para>执行与节点标题对应的扩展固件逻辑：OTA Delta 记录差分任务、Merge Signature 把签名追加进镜像、
        /// Merge Dual Slot 拼接 A/B 槽位、Flash Boot 记录 bootloader 刷写任务。</para>
        /// Executes the extended firmware logic matching the node title: OTA Delta records a delta task, Merge Signature appends
        /// a signature into the image, Merge Dual Slot concatenates the A/B slots, and Flash Boot records a bootloader flash task.
        /// </summary>
        /// <param name="context">
        /// <para>执行上下文。</para>
        /// The execution context.
        /// </param>
        /// <returns>
        /// <para>表示异步执行的任务。</para>
        /// A task representing the asynchronous execution.
        /// </returns>
        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            switch (Title)
            {
                case "OTA Delta":
                    context.Log.Write(LogLevel.Warn, Title,
                        "OTA delta generation requires a bsdiff engine (not bundled); parameters recorded.  生成差分包需要 bsdiff 引擎（未内置），已记录参数。");
                    break;
                case "Merge Signature":
                    ExecuteMergeSignature(context);
                    break;
                case "Merge Dual Slot":
                    ExecuteMergeDualSlot(context);
                    break;
                case "Flash Boot":
                    {
                        var bootloader = Convert.ToString(ReadInput("Bootloader"), System.Globalization.CultureInfo.InvariantCulture);
                        WriteOutput("Status", "queued");
                        context.Log.Write(LogLevel.Info, Title, $"Bootloader flash queued: {bootloader ?? "<empty>"}  已记录刷写任务。");
                        break;
                    }
                default:
                    return base.ExecuteAsync(context);
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private void ExecuteMergeSignature(INodeExecutionContext context)
        {
            var image = NodeDataUtil.ToBytes(ReadInput("Image"));
            var signature = NodeDataUtil.ToBytes(ReadInput("Signature"));
            if (image == null || signature == null)
            {
                context.Log.Write(LogLevel.Warn, Title, "Missing Image or Signature input.  缺少镜像或签名。");
                return;
            }

            var signed = new byte[image.Length + signature.Length];
            image.CopyTo(signed, 0);
            signature.CopyTo(signed, image.Length);
            WriteOutput("Signed", signed);
            context.Log.Write(LogLevel.Info, Title, $"Appended {signature.Length}-byte signature to {image.Length}-byte image.  已合并签名。");
        }

        private void ExecuteMergeDualSlot(INodeExecutionContext context)
        {
            var a = NodeDataUtil.ToBytes(ReadInput("Slot A")) ?? [];
            var b = NodeDataUtil.ToBytes(ReadInput("Slot B")) ?? [];
            var merged = new byte[a.Length + b.Length];
            a.CopyTo(merged, 0);
            b.CopyTo(merged, a.Length);
            WriteOutput("Merged", merged);
            context.Log.Write(LogLevel.Info, Title, $"Merged slot A ({a.Length}B) + slot B ({b.Length}B).  已合并双槽。");
        }
    }
}