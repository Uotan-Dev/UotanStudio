using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// HDC disconnect node: disconnects one network HDC target after the run-time confirmation gate. A USB target
/// keeps the existing transport rule and is rejected because it disconnects when unplugged.
/// <para>HDC 断开节点：在运行期确认门通过后断开一个网络 HDC 目标。USB 目标沿用现有传输规则并被拒绝，
/// 因为它在拔出时才会断开。</para>
/// </summary>
public sealed class HdcDisconnectNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Hdc.Disconnect";

    /// <summary>
    /// Creates an HDC disconnect node with the specified display title.
    /// <para>创建具有指定显示标题的 HDC 断开节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public HdcDisconnectNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the HDC disconnect node with its ports.
    /// <para>创建带端口的 HDC 断开节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static HdcDisconnectNodeViewModel CreateDisconnectNode()
    {
        var node = new HdcDisconnectNodeViewModel("HDC Disconnect")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output", PortType = NodePortType.Text });
        return node;
    }

    /// <inheritdoc />
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, NodeTypeKey, StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        try
        {
            var device = HdcNodeSupport.ResolveDevice(this);
            context.CancellationToken.ThrowIfCancellationRequested();
            var output = await HdcDeviceService.FromSettings()
                .DisconnectAsync(device, context.CancellationToken).ConfigureAwait(false);
            WriteOutput("Status", "completed");
            WriteOutput("Output", output);
            context.Log.Write(LogLevel.Info, Title, $"HDC target {device.Serial} disconnected.  HDC 目标已断开。");
        }
        catch (OperationCanceledException)
        {
            WriteOutput("Status", "cancelled");
            throw;
        }
        catch
        {
            HdcNodeSupport.MarkFailure(this);
            throw;
        }
    }
}