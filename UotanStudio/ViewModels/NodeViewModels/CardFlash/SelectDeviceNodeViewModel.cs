using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>设备选择节点视图模型：按序列号（可选）或默认第一台设备解析 <see cref="DetectedDevice"/>
/// 并输出，供后续烧录节点通过 Device 端口消费。</para>
/// Device selection node view model: resolves a <see cref="DetectedDevice"/> by an optional serial
/// (or the first scanned device) and outputs it for downstream flash nodes via the Device port.
/// </summary>
public class SelectDeviceNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的设备选择节点。</para>
    /// Creates a device selection node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public SelectDeviceNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// <para>创建默认的设备选择节点。</para>
    /// Creates a default device selection node.
    /// </summary>
    public static SelectDeviceNodeViewModel CreateSelectDeviceNode()
    {
        var node = new SelectDeviceNodeViewModel("Select Device")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Target.Select"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发设备选择逻辑：Device.Target.Select 从序列号或首台设备解析设备并输出。</para>
    /// Dispatches the device selection logic by the node type key (TypeKey): Device.Target.Select resolves
    /// a device from the serial or the first scanned device and outputs it.
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
        switch (TypeKey)
        {
            case "Device.Target.Select":
                ExecuteSelect(context);
                break;
            default:
                return base.ExecuteAsync(context);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ExecuteSelect(INodeExecutionContext context)
    {
        var serial = Text(ReadInput("Serial"));
        var monitor = App.DeviceMonitor;

        DetectedDevice? device;
        if (!string.IsNullOrWhiteSpace(serial))
        {
            device = monitor.FindDeviceBySerial(serial);
            if (device is null)
            {
                context.Log.Write(LogLevel.Warn, Title, $"No device found with serial: {serial}  未找到序列号为 {serial} 的设备。");
                WriteOutput("Status", "not-found");
                return;
            }
        }
        else
        {
            device = monitor.Scan().FirstOrDefault();
            if (device is null)
            {
                context.Log.Write(LogLevel.Warn, Title, "No device found. 未找到任何设备。");
                WriteOutput("Status", "not-found");
                return;
            }
        }

        WriteOutput("Device", device);
        WriteOutput("Status", "connected");
        context.Log.Write(LogLevel.Info, Title, $"Selected device: {device.Serial} ({device.Protocol})  已选择设备。");
    }

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}