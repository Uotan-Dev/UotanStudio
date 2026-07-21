using Avalonia;
using FirmwareKit.Comm.Fastboot;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>烧录操作节点视图模型，提供设备连接、分区刷入、擦除和重启功能。</para>
    /// Flash operation node view model, providing device connection, partition flashing, erasing, and reboot capabilities.
    /// <para>执行逻辑通过 FirmwareKit.Comm.Fastboot.FastbootDriver 接入真实 Fastboot 协议。</para>
    /// Execution logic connects to the real Fastboot protocol via FirmwareKit.Comm.Fastboot.FastbootDriver.
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
        /// <para>执行该烧录节点的真实操作，依据节点标题分派到 FastbootDriver 对应方法。</para>
        /// Executes the real operation of this flash node, dispatching to the corresponding FastbootDriver method based on the node title.
        /// </summary>
        /// <param name="devicePath">设备路径占位（当前实现使用默认 WinUSB 句柄）。</param>
        /// <param name="partition">分区名（如 boot/system）。</param>
        /// <param name="imagePath">镜像文件路径（仅 Flash 使用）。</param>
        /// <param name="cancellationToken">取消令牌（部分同步 API 不接受）。</param>
        /// <returns>执行结果摘要文本。</returns>
        public async Task<string> ExecuteAsync(string devicePath, string partition, string imagePath, CancellationToken cancellationToken)
        {
            _ = devicePath;
            try
            {
                var transport = new FirmwareKit.Comm.Fastboot.Usb.Windows.WinUSBDevice();
                using var driver = new FastbootDriver(transport);

                return Title switch
                {
                    "Connect Device" => $"[Connect] OK: product={driver.GetVar("product")}",
                    "Flash Partition" => await DoFlashAsync(driver, partition, imagePath),
                    "Erase Partition" => DoErase(driver, partition),
                    "Reboot Device" => DoReboot(driver),
                    _ => $"[Flash] Unknown node: {Title}"
                };
            }
            catch (Exception ex)
            {
                return $"[{Title}] FAIL: {ex.Message}";
            }
        }

        private static async Task<string> DoFlashAsync(FastbootDriver driver, string partition, string imagePath)
        {
            if (string.IsNullOrEmpty(partition) || string.IsNullOrEmpty(imagePath))
                return "[Flash] FAIL: partition or image path is empty.";
            if (!System.IO.File.Exists(imagePath))
                return $"[Flash] FAIL: image not found: {imagePath}";
            await Task.Run(() => driver.FlashImage(partition, imagePath, string.Empty));
            return $"[Flash] OK: flashed {partition} from {imagePath}";
        }

        private static string DoErase(FastbootDriver driver, string partition)
        {
            if (string.IsNullOrEmpty(partition))
                return "[Erase] FAIL: partition is empty.";
            driver.ErasePartition(partition);
            return $"[Erase] OK: erased {partition}";
        }

        private static string DoReboot(FastbootDriver driver)
        {
            driver.Reboot(string.Empty);
            return "[Reboot] OK: rebooted device";
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
