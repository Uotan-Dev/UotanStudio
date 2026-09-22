using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>烧录操作节点视图模型，提供设备连接、分区刷入、擦除和重启功能，均接入真实后端服务。</para>
/// Flash operation node view model, providing device connection, partition flashing, erasing,
/// and reboot capabilities, all wired to the real backend services.
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
    /// <para>创建默认的分区刷入节点。</para>
    /// Creates a default partition flash node.
    /// </summary>
    public static FlashNodeViewModel CreateFlashNode()
    {
        var node = new FlashNodeViewModel("Flash Partition")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Partition.Flash"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partition", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Image", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>Creates a partition-flash preset using the same explicit partition input and execution contract.</summary>
    public static FlashNodeViewModel CreateFlashPreset(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var node = CreateFlashNode();
        node.Input.Single(input => input.Title == "Partition").InitialValue = partition;
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
    /// <para>创建默认的重启节点。</para>
    /// Creates a default reboot node.
    /// </summary>
    public static FlashNodeViewModel CreateRebootNode()
    {
        var node = new FlashNodeViewModel("Reboot Device")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Power.Reboot"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Mode", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的烧录逻辑：Device.Partition.Flash 调
    /// <see cref="FlashService.FlashImageAsync"/>、Device.Partition.Erase 调
    /// <see cref="FlashService.ErasePartitionAsync"/>、Device.Power.Reboot 调
    /// <see cref="DeviceMonitorService.RebootAsync"/>。设备缺失时告警并短路。</para>
    /// Dispatches flash logic by the node type key (TypeKey): Device.Partition.Flash calls
    /// <see cref="FlashService.FlashImageAsync"/>, Device.Partition.Erase calls
    /// <see cref="FlashService.ErasePartitionAsync"/>, and Device.Power.Reboot calls
    /// <see cref="DeviceMonitorService.RebootAsync"/>. A missing device logs a warning and short-circuits.
    /// </summary>
    /// <param name="context">
    /// <para>执行上下文。</para>
    /// The execution context.
    /// </param>
    /// <returns>
    /// <para>表示异步执行的任务。</para>
    /// A task representing the asynchronous execution.
    /// </returns>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (TypeKey)
            {
                case "Device.Partition.Flash":
                    await ExecuteFlashAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Partition.Erase":
                    await ExecuteEraseAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Power.Reboot":
                    await ExecuteRebootAsync(context).ConfigureAwait(false);
                    break;
                default:
                    await base.ExecuteAsync(context).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Log.Write(LogLevel.Error, Title, $"Flash node failed: {ex.Message}  烧录节点执行失败。");
            throw;
        }
    }

    private async Task ExecuteFlashAsync(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No device connected. 未连接设备。");
            WriteOutput("Result", false);
            WriteOutput("Status", "no-device");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        var partition = Text(ReadInput("Partition"));
        if (string.IsNullOrWhiteSpace(partition))
        {
            context.Log.Write(LogLevel.Warn, Title, "No partition specified. 未指定分区。");
            WriteOutput("Result", false);
            WriteOutput("Status", "missing-partition");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        var image = Text(ReadInput("Image"));
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Image path invalid or missing: {image ?? "<empty>"}  镜像路径无效或缺失。");
            WriteOutput("Result", false);
            WriteOutput("Status", "invalid-image");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        var progress = new Progress<FlashProgress>(p => context.ReportProgress(p.Percent / 100.0));
        var result = await FlashService.Default.FlashImageAsync(
            new DeviceInfo(device), partition, image, progress, context.CancellationToken).ConfigureAwait(false);

        WriteOutput("Result", result.Success);
        WriteOutput("Status", result.Summary);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Summary);
        }
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title,
            result.Success
                ? $"{result.Summary}  刷写完成。"
                : $"Flash failed: {result.Summary}  刷写失败。");
    }

    private async Task ExecuteEraseAsync(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No device connected. 未连接设备。");
            WriteOutput("Result", false);
            WriteOutput("Status", "no-device");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        var partition = Text(ReadInput("Partition"));
        if (string.IsNullOrWhiteSpace(partition))
        {
            context.Log.Write(LogLevel.Warn, Title, "No partition specified. 未指定分区。");
            WriteOutput("Result", false);
            WriteOutput("Status", "missing-partition");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        var progress = new Progress<FlashProgress>(p => context.ReportProgress(p.Percent / 100.0));
        var result = await FlashService.Default.ErasePartitionAsync(
            new DeviceInfo(device), partition, progress, context.CancellationToken).ConfigureAwait(false);

        WriteOutput("Result", result.Success);
        WriteOutput("Status", result.Summary);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Summary);
        }
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title,
            result.Success
                ? $"{result.Summary}  擦除完成。"
                : $"Erase failed: {result.Summary}  擦除失败。");
    }

    private async Task ExecuteRebootAsync(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No device connected. 未连接设备。");
            WriteOutput("Status", "no-device");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        var mode = Text(ReadInput("Mode"));
        var ok = await DeviceMonitorService.Instance.RebootAsync(
            device, mode ?? "now", context.CancellationToken).ConfigureAwait(false);

        WriteOutput("Status", ok ? $"rebooted:{mode ?? "now"}" : "reboot-failed");
        if (!ok)
        {
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_RebootFailed"));
        }
        context.Log.Write(ok ? LogLevel.Info : LogLevel.Error, Title,
            ok
                ? $"Reboot requested. device={device.Serial} mode={mode ?? "now"}  已请求重启。"
                : $"Reboot failed. device={device.Serial} mode={mode ?? "now"}  重启失败。");
    }

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}