using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Scripting;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Device service node view model exposing device state, partition table, A/B slot, recovery wipe, and
/// shell operations through the shared <see cref="IScriptToolGateway"/>.
/// <para>设备服务节点视图模型：通过共享的 <see cref="IScriptToolGateway"/> 暴露设备状态、分区表、
/// A/B 槽位、恢复模式擦除与 shell 操作。</para>
/// </summary>
public class DeviceServiceNodeViewModel : FlowNodeViewModel
{
    private readonly IScriptToolGateway _gateway;

    /// <summary>
    /// <para>创建具有指定标题的设备服务节点。</para>
    /// Creates a device service node with the specified title.
    /// </summary>
    /// <param name="title"><para>节点的显示标题。</para>The display title of the node.</param>
    /// <param name="gateway">
    /// <para>设备动作网关；为 <c>null</c> 时使用 <see cref="DeviceScriptToolGateway.Default"/>。</para>
    /// The device action gateway; uses <see cref="DeviceScriptToolGateway.Default"/> when <c>null</c>.
    /// </param>
    public DeviceServiceNodeViewModel(string title, IScriptToolGateway? gateway = null) : base(title, NodeCategory.Flash)
    {
        _gateway = gateway ?? DeviceScriptToolGateway.Default;
    }

    /// <summary>
    /// <para>创建默认的设备状态查询节点。</para>
    /// Creates a default device state query node.
    /// </summary>
    public static DeviceServiceNodeViewModel CreateStateQueryNode()
    {
        var node = new DeviceServiceNodeViewModel("Device State")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.State.Query"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Protocol", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "State", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的分区表读取节点。</para>
    /// Creates a default partition table read node.
    /// </summary>
    public static DeviceServiceNodeViewModel CreatePartitionTableReadNode()
    {
        var node = new DeviceServiceNodeViewModel("Read Partition Table")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Table.Read"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Show All",
            PortType = NodePortType.Boolean,
            InitialValue = false
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Count", PortType = NodePortType.Number });
        node.Output.Add(new NodeConnectorViewModel { Title = "Partitions", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的活动槽位查询节点。</para>
    /// Creates a default active slot query node.
    /// </summary>
    public static DeviceServiceNodeViewModel CreateSlotQueryNode()
    {
        var node = new DeviceServiceNodeViewModel("Query Slot")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Slot.Query"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Slot", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的活动槽位切换节点。</para>
    /// Creates a default active slot switch node.
    /// </summary>
    public static DeviceServiceNodeViewModel CreateSlotSwitchNode()
    {
        var node = new DeviceServiceNodeViewModel("Switch Slot")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Slot.Switch"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Slot", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的恢复模式擦除节点。</para>
    /// Creates a default recovery wipe node.
    /// </summary>
    public static DeviceServiceNodeViewModel CreateRecoveryCleanNode()
    {
        var node = new DeviceServiceNodeViewModel("Recovery Wipe")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Recovery.Clean"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Mode",
            PortType = NodePortType.Text,
            InitialValue = "factoryreset",
            Choices = [new NodePortChoice("Factory Reset"), new NodePortChoice("Format Data")]
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的 shell 命令执行节点。</para>
    /// Creates a default shell command execution node.
    /// </summary>
    public static DeviceServiceNodeViewModel CreateShellCommandNode()
    {
        var node = new DeviceServiceNodeViewModel("Run Shell Command")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Shell.Command"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Command", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output", PortType = NodePortType.Text });
        return node;
    }

    /// <inheritdoc />
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (TypeKey)
            {
                case "Device.State.Query":
                    ExecuteStateQuery(context);
                    break;
                case "Device.Table.Read":
                    await ExecutePartitionTableReadAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Slot.Query":
                    await ExecuteSlotQueryAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Slot.Switch":
                    await ExecuteSlotSwitchAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Recovery.Clean":
                    await ExecuteRecoveryCleanAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Shell.Command":
                    await ExecuteShellCommandAsync(context).ConfigureAwait(false);
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
            context.Log.Write(LogLevel.Error, Title, $"Device service node failed: {ex.Message}  设备服务节点执行失败。");
            throw;
        }
    }

    private void ExecuteStateQuery(INodeExecutionContext context)
    {
        var device = RequireDevice(context);
        WriteOutput("Protocol", ProtocolText(device));
        WriteOutput("State", device.State);
        context.Log.Write(LogLevel.Info, Title, $"Device {device.Serial}: {ProtocolText(device)} / {device.State}");
    }

    private async Task ExecutePartitionTableReadAsync(INodeExecutionContext context)
    {
        var device = RequireDevice(context);
        var showAll = ReadBoolean("Show All");
        var partitions = await _gateway.ReadPartitionTableAsync(device, showAll, context.CancellationToken)
            .ConfigureAwait(false);
        WriteOutput("Count", (double)partitions.Count);
        WriteOutput("Partitions", string.Join('\n', partitions.Select(entry => $"{entry.Name}={entry.Size}")));
    }

    private async Task ExecuteSlotQueryAsync(INodeExecutionContext context)
    {
        var device = RequireDevice(context);
        var slot = await _gateway.GetCurrentSlotAsync(device, context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Slot", slot);
    }

    private async Task ExecuteSlotSwitchAsync(INodeExecutionContext context)
    {
        var device = RequireDevice(context);
        var slot = await _gateway.SetOtherSlotAsync(device, context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Slot", slot);
    }

    private async Task ExecuteRecoveryCleanAsync(INodeExecutionContext context)
    {
        var device = RequireDevice(context);
        var mode = NormalizeMode(Text(ReadInput("Mode")));
        var output = await _gateway.RecoveryCleanAsync(device, mode, context.CancellationToken)
            .ConfigureAwait(false);
        WriteOutput("Output", output);
    }

    private async Task ExecuteShellCommandAsync(INodeExecutionContext context)
    {
        var device = RequireDevice(context);
        var command = Text(ReadInput("Command"));
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }
        var output = await _gateway.ShellAsync(device, command, context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Output", output);
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

    private bool ReadBoolean(string title) => ReadInput(title) switch
    {
        bool value => value,
        string text => bool.TryParse(text, out var parsed) && parsed,
        _ => false
    };

    private static string NormalizeMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "factory reset" or "factoryreset" => "factoryreset",
        "format data" or "formatdata" => "formatdata",
        _ => value?.Trim() ?? string.Empty
    };

    private static string ProtocolText(DetectedDevice device) => device.Protocol switch
    {
        DeviceProtocolKind.Adb => "adb",
        DeviceProtocolKind.Fastboot => "fastboot",
        DeviceProtocolKind.Edl => "edl",
        DeviceProtocolKind.Rkdl => "rkdl",
        DeviceProtocolKind.Mtk => "mtk",
        DeviceProtocolKind.Hdc => "hdc",
        _ => "unknown"
    };

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}