using Avalonia;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>烧录操作节点视图模型，提供设备连接、分区刷入、擦除和重启功能。</para>
    /// Flash operation node view model, providing device connection, partition flashing, erasing, and reboot capabilities.
    /// </summary>
    public class FlashNodeViewModel : FlowNodeViewModel
    {
        /// <summary>
        /// <para>创建具有指定标题的烧录操作节点。</para>
        /// Creates a flash operation node with the specified title.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        public FlashNodeViewModel(string title) : base(title, NodeCategory.Flash)
        {
        }

        /// <summary>
        /// <para>创建默认的设备连接节点。</para>
        /// Creates a default device connection node.
        /// </summary>
        public static FlashNodeViewModel CreateConnectNode()
        {
            var node = new FlashNodeViewModel("Connect Device")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Mode" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Device" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Status" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的分区刷入节点。</para>
        /// Creates a default partition flash node.
        /// </summary>
        public static FlashNodeViewModel CreateFlashNode()
        {
            var node = new FlashNodeViewModel("Flash Partition")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Device" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Partition" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Image" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Result" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的擦除节点。</para>
        /// Creates a default erase node.
        /// </summary>
        public static FlashNodeViewModel CreateEraseNode()
        {
            var node = new FlashNodeViewModel("Erase Partition")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Device" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Partition" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Result" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的重启节点。</para>
        /// Creates a default reboot node.
        /// </summary>
        public static FlashNodeViewModel CreateRebootNode()
        {
            var node = new FlashNodeViewModel("Reboot Device")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Device" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Mode" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Status" });
            return node;
        }
    }
}