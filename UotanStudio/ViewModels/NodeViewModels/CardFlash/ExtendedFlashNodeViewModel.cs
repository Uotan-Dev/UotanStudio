using Avalonia;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>扩展烧录操作节点视图模型，提供 bootloader 刷写、全部擦除、OTA 侧载与固件覆盖功能。</para>
    /// Extended flash operation node view model, providing bootloader flashing, full erase, OTA sideload, and firmware overwrite capabilities.
    /// </summary>
    public class ExtendedFlashNodeViewModel : FlowNodeViewModel
    {
        /// <summary>
        /// <para>创建具有指定标题的扩展烧录操作节点。</para>
        /// Creates an extended flash operation node with the specified title.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        public ExtendedFlashNodeViewModel(string title) : base(title, NodeCategory.Flash)
        {
        }

        /// <summary>
        /// <para>创建默认的 bootloader 刷写节点。</para>
        /// Creates a default flash-bootloader node.
        /// </summary>
        public static ExtendedFlashNodeViewModel CreateFlashBootloaderNode()
        {
            var node = new ExtendedFlashNodeViewModel("Flash Bootloader")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Device" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Bootloader" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Status" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的全部擦除节点，擦除设备上的所有分区。</para>
        /// Creates a default erase-all node that wipes every partition on the device.
        /// </summary>
        public static ExtendedFlashNodeViewModel CreateEraseAllNode()
        {
            var node = new ExtendedFlashNodeViewModel("Erase All")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Device" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Status" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的 OTA 侧载节点，通过 sideload 推送 OTA 包。</para>
        /// Creates a default OTA sideload node that pushes an OTA package via sideload.
        /// </summary>
        public static ExtendedFlashNodeViewModel CreateOtaSideloadNode()
        {
            var node = new ExtendedFlashNodeViewModel("OTA Sideload")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Device" });
            node.Input.Add(new NodeConnectorViewModel { Title = "OTA Package" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Status" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的固件覆盖节点，使用外部镜像覆盖指定分区。</para>
        /// Creates a default firmware-overwrite node that overwrites a partition with an external image.
        /// </summary>
        public static ExtendedFlashNodeViewModel CreateOverwriteFirmwareNode()
        {
            var node = new ExtendedFlashNodeViewModel("Overwrite Firmware")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Device" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Firmware" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Status" });
            return node;
        }
    }
}
