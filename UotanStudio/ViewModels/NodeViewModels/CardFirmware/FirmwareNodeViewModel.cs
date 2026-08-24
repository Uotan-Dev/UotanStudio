using Avalonia;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>固件操作节点视图模型，提供固件解包、打包、签名验证和分区提取功能。</para>
    /// Firmware operation node view model, providing firmware unpack, pack, signature verification, and partition extraction capabilities.
    /// </summary>
    public class FirmwareNodeViewModel : FlowNodeViewModel
    {
        /// <summary>
        /// <para>创建具有指定标题的固件操作节点。</para>
        /// Creates a firmware operation node with the specified title.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        public FirmwareNodeViewModel(string title) : base(title, NodeCategory.Firmware)
        {
        }

        /// <summary>
        /// <para>创建默认的固件解包节点。</para>
        /// Creates a default firmware unpack node.
        /// </summary>
        /// <returns>配置好输入输出端口的固件解包节点。</returns>
        public static FirmwareNodeViewModel CreateUnpackNode()
        {
            var node = new FirmwareNodeViewModel("Unpack Firmware")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Firmware Path" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Output Dir" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Partitions" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的固件打包节点。</para>
        /// Creates a default firmware pack node.
        /// </summary>
        public static FirmwareNodeViewModel CreatePackNode()
        {
            var node = new FirmwareNodeViewModel("Pack Firmware")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Input Dir" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Config" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Firmware" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的签名验证节点。</para>
        /// Creates a default signature verification node.
        /// </summary>
        public static FirmwareNodeViewModel CreateVerifyNode()
        {
            var node = new FirmwareNodeViewModel("Verify Signature")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Firmware" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Valid" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Report" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的分区提取节点。</para>
        /// Creates a default partition extraction node.
        /// </summary>
        public static FirmwareNodeViewModel CreateExtractNode()
        {
            var node = new FirmwareNodeViewModel("Extract Partition")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Firmware" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Partition Name" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Image" });
            return node;
        }
    }
}