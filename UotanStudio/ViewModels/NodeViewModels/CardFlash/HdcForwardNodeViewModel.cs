using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// HDC forward node: creates one forward or reverse TCP mapping for the selected HDC target.
/// <para>HDC 转发节点：为选定的 HDC 目标创建一条正向或反向 TCP 映射。</para>
/// </summary>
public sealed class HdcForwardNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Hdc.Forward";

    /// <summary>
    /// Creates an HDC forward node with the specified display title.
    /// <para>创建具有指定显示标题的 HDC 转发节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public HdcForwardNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the HDC forward node with its ports.
    /// <para>创建带端口的 HDC 转发节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static HdcForwardNodeViewModel CreateForwardNode()
    {
        var node = new HdcForwardNodeViewModel("HDC Forward")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Local Port", PortType = NodePortType.Number, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel { Title = "Remote Port", PortType = NodePortType.Number, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel { Title = "Reverse", PortType = NodePortType.Boolean, InitialValue = false });
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
            var localPort = NodeDataUtil.ToInt(ReadInput("Local Port"), 0);
            var remotePort = NodeDataUtil.ToInt(ReadInput("Remote Port"), 0);
            if (localPort is < 1 or > 65535 || remotePort is < 1 or > 65535)
            {
                WriteOutput("Status", "invalid-port");
                throw new ArgumentOutOfRangeException(nameof(localPort),
                    "TCP ports must be between 1 and 65535. TCP 端口必须介于 1 与 65535 之间。");
            }

            var reverse = NodeDataUtil.ToBool(ReadInput("Reverse"));
            context.CancellationToken.ThrowIfCancellationRequested();
            var output = await HdcDeviceService.FromSettings()
                .ForwardAsync(device, localPort, remotePort, reverse, context.CancellationToken).ConfigureAwait(false);
            WriteOutput("Status", "completed");
            WriteOutput("Output", output);
            context.Log.Write(LogLevel.Info, Title,
                $"HDC {(reverse ? "reverse" : "forward")} mapping {localPort}->{remotePort} created on {device.Serial}.  " +
                "HDC 端口映射已创建。");
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
