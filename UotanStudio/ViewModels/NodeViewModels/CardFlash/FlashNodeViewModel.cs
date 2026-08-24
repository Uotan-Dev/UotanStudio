using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

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

        /// <summary>
        /// <para>执行与节点标题对应的烧录逻辑：Connect Device 校验连接参数、Flash Partition 校验镜像并上报结果、
        /// Erase Partition 校验分区、Reboot Device 校验重启模式。实时刷写依赖设备管理器建立的会话，此处校验参数并记录任务。</para>
        /// Executes the flash logic matching the node title: Connect Device validates connection parameters, Flash Partition validates
        /// the image and reports a result, Erase Partition validates the partition, and Reboot Device validates the reboot mode.
        /// Live flashing relies on a device-manager session; nodes validate parameters and record the task.
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
                case "Connect Device":
                    ExecuteConnect(context);
                    break;
                case "Flash Partition":
                    ExecuteFlash(context);
                    break;
                case "Erase Partition":
                    ExecuteErase(context);
                    break;
                case "Reboot Device":
                    ExecuteReboot(context);
                    break;
                default:
                    return base.ExecuteAsync(context);
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private void ExecuteConnect(INodeExecutionContext context)
        {
            var mode = Text(ReadInput("Mode"));
            var target = Text(ReadInput("Device"));
            WriteOutput("Status", $"recorded:{mode}");
            context.Log.Write(LogLevel.Info, Title, $"Connect requested. device={target ?? "<empty>"} mode={mode ?? "fastboot"}  已记录连接任务。");
        }

        private void ExecuteFlash(INodeExecutionContext context)
        {
            var image = Text(ReadInput("Image"));
            var partition = Text(ReadInput("Partition"));
            var imageOk = !string.IsNullOrWhiteSpace(image) && File.Exists(image);
            WriteOutput("Result", imageOk);
            context.Log.Write(imageOk ? LogLevel.Info : LogLevel.Warn, Title,
                imageOk
                    ? $"Flash '{partition ?? "<empty>"}' from '{image}' recorded.  已记录刷写任务。"
                    : $"Image path invalid or missing: {image ?? "<empty>"}  镜像路径无效或缺失。");
        }

        private void ExecuteErase(INodeExecutionContext context)
        {
            var partition = Text(ReadInput("Partition"));
            var ok = !string.IsNullOrWhiteSpace(partition);
            WriteOutput("Result", ok);
            context.Log.Write(ok ? LogLevel.Info : LogLevel.Warn, Title,
                ok ? $"Erase '{partition}' recorded.  已记录擦除任务。" : "No Partition specified.  未指定分区。");
        }

        private void ExecuteReboot(INodeExecutionContext context)
        {
            var mode = Text(ReadInput("Mode"));
            var target = Text(ReadInput("Device"));
            WriteOutput("Status", $"reboot:{mode}");
            context.Log.Write(LogLevel.Info, Title, $"Reboot requested. device={target ?? "<empty>"} mode={mode ?? "system"}  已记录重启任务。");
        }

        private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}