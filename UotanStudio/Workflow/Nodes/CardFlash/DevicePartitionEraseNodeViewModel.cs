using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Device partition erase node: erases one partition through <see cref="FlashService.ErasePartitionAsync"/> and
/// reports the outcome on the Result and Status ports. The node never opens a device handle itself.
/// <para>设备分区擦除节点：经 <see cref="FlashService.ErasePartitionAsync"/> 擦除一个分区，并在 Result 与 Status 端口上报结果。
/// 节点自身不打开设备句柄。</para>
/// </summary>
public sealed class DevicePartitionEraseNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a partition erase node with the specified display title.
    /// <para>创建具有指定显示标题的分区擦除节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public DevicePartitionEraseNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the partition erase node.
    /// <para>创建分区擦除节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static DevicePartitionEraseNodeViewModel CreateEraseNode()
    {
        var node = new DevicePartitionEraseNodeViewModel("Erase Partition")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Partition.Erase"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partition", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Erases the requested partition and reports the outcome.
    /// <para>擦除请求的分区并上报结果。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The device input is missing, the partition input is empty, or the erase failed. <para>缺少设备输入、分区输入为空，或擦除失败。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, "Device.Partition.Erase", StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No device connected. 未连接设备。");
            WriteOutput("Result", false);
            WriteOutput("Status", "no-device");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        var partition = Convert.ToString(ReadInput("Partition"), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(partition))
        {
            context.Log.Write(LogLevel.Warn, Title, "No partition specified. 未指定分区。");
            WriteOutput("Result", false);
            WriteOutput("Status", "missing-partition");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        var progress = new Progress<FlashProgress>(update => context.ReportProgress(update.Percent / 100.0));
        var result = await FlashService.Default.ErasePartitionAsync(
            new DeviceInfo(device), partition, progress, context.CancellationToken).ConfigureAwait(false);

        WriteOutput("Result", result.Success);
        WriteOutput("Status", result.Summary);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"Erase failed: {result.Summary}  擦除失败。");
            throw new InvalidOperationException(result.Summary);
        }

        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  分区擦除完成。");
    }
}