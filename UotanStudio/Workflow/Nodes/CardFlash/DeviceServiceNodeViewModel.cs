using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Scripting;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Device service node view model exposing the partition table and shell operations through
/// the shared <see cref="IScriptToolGateway"/>.
/// <para>设备服务节点视图模型：通过共享的 <see cref="IScriptToolGateway"/> 暴露分区表读取与 shell 操作。</para>
/// </summary>
public class DeviceServiceNodeViewModel : FlowNodeViewModel
{
    private readonly IScriptToolGateway _gateway;

    /// <summary>
    /// Creates a device service node with the specified title.
    /// <para>创建具有指定标题的设备服务节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    /// <param name="gateway">
    /// The device action gateway; uses <see cref="DeviceScriptToolGateway.Default"/> when <c>null</c>.
    /// <para>设备动作网关；为 <c>null</c> 时使用 <see cref="DeviceScriptToolGateway.Default"/>。</para>
    /// </param>
    public DeviceServiceNodeViewModel(string title, IScriptToolGateway? gateway = null) : base(title, NodeCategory.Flash)
    {
        _gateway = gateway ?? DeviceScriptToolGateway.Default;
    }

    /// <summary>
    /// Creates a default partition table read node.
    /// <para>创建默认的分区表读取节点。</para>
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
        node.Output.Add(new NodeConnectorViewModel { Title = "Entries", PortType = NodePortType.List });
        node.Output.Add(new NodeConnectorViewModel { Title = "Names", PortType = NodePortType.List });
        return node;
    }

    /// <summary>
    /// Creates a default shell command execution node.
    /// <para>创建默认的 shell 命令执行节点。</para>
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
                case "Device.Table.Read":
                    await ExecutePartitionTableReadAsync(context).ConfigureAwait(false);
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

    private async Task ExecutePartitionTableReadAsync(INodeExecutionContext context)
    {
        var device = RequireDevice(context);
        var showAll = ReadBoolean("Show All");
        var partitions = await _gateway.ReadPartitionTableAsync(device, showAll, context.CancellationToken)
            .ConfigureAwait(false);
        WriteOutput("Count", (double)partitions.Count);
        WriteOutput("Partitions", string.Join('\n', partitions.Select(entry => $"{entry.Name}={entry.Size}")));
        WriteOutput("Entries", (IReadOnlyList<object?>)[.. partitions.Select(entry => (object?)$"{entry.Name}={entry.Size}")]);
        WriteOutput("Names", (IReadOnlyList<object?>)[.. partitions.Select(entry => (object?)entry.Name)]);
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

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}