using Avalonia;

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
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Source" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Target" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Delta" });
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
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Image" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Signature" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Signed" });
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
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Slot A" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Slot B" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Merged" });
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
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Bootloader" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Status" });
            return node;
        }
    }
}
