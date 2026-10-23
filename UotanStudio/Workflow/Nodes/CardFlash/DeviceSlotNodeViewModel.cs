using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Device slot node: reads or switches the active A/B slot through <see cref="FastbootSlotService"/> and
/// reports the slot on the Slot and Status ports. The node never opens a device handle itself.
/// <para>设备槽位节点：经 <see cref="FastbootSlotService"/> 读取或切换 A/B 活动槽位，并在 Slot 与 Status 端口上报槽位。
/// 节点自身不打开设备句柄。</para>
/// </summary>
public sealed class DeviceSlotNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a device slot node with the specified display title.
    /// <para>创建具有指定显示标题的设备槽位节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public DeviceSlotNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the current-slot query node.
    /// <para>创建当前槽位查询节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static DeviceSlotNodeViewModel CreateQueryNode()
    {
        var node = new DeviceSlotNodeViewModel("Query Active Slot")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Slot.Query"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Slot", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Creates the other-slot switch node.
    /// <para>创建另一槽位切换节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static DeviceSlotNodeViewModel CreateSwitchNode()
    {
        var node = new DeviceSlotNodeViewModel("Switch Active Slot")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Slot.Switch"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Slot", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Dispatches the slot query or switch by the node type key.
    /// <para>按节点类型键分发槽位查询或切换。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The device input is missing or the device does not report a recognized slot. <para>缺少设备输入，或设备未上报可识别的槽位。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (TypeKey)
            {
                case "Device.Slot.Query":
                    await ExecuteQueryAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Slot.Switch":
                    await ExecuteSwitchAsync(context).ConfigureAwait(false);
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
            context.Log.Write(LogLevel.Error, Title, $"Device slot node failed: {ex.Message}  设备槽位节点执行失败。");
            throw;
        }
    }

    private async Task ExecuteQueryAsync(INodeExecutionContext context)
    {
        var device = RequireDevice(context);
        var slot = await FastbootSlotService.GetCurrentSlotAsync(device, context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Slot", slot);
        WriteOutput("Status", "current");
        context.Log.Write(LogLevel.Info, Title, $"Current active slot: {slot}.  当前活动槽位已读取。");
    }

    private async Task ExecuteSwitchAsync(INodeExecutionContext context)
    {
        var device = RequireDevice(context);
        var slot = await FastbootSlotService.SetOtherAsync(device, context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Slot", slot);
        WriteOutput("Status", "switched");
        context.Log.Write(LogLevel.Info, Title, $"Active slot switched to {slot}.  活动槽位已切换。");
    }

    private DetectedDevice RequireDevice(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No device connected. 未连接设备。");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }
        return device;
    }
}