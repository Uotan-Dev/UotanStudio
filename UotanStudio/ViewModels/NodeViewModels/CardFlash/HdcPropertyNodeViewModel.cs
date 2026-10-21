using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// HDC property node: reads one system parameter from the selected HDC target and publishes both the raw value
/// and a status summary.
/// <para>HDC 属性节点：从选定的 HDC 目标读取一个系统参数，并发布原始值与状态摘要。</para>
/// </summary>
public sealed class HdcPropertyNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Hdc.Property";

    /// <summary>
    /// Creates an HDC property node with the specified display title.
    /// <para>创建具有指定显示标题的 HDC 属性节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public HdcPropertyNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the HDC property node with its ports.
    /// <para>创建带端口的 HDC 属性节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static HdcPropertyNodeViewModel CreatePropertyNode()
    {
        var node = new HdcPropertyNodeViewModel("HDC Property")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Name", PortType = NodePortType.Text, IsRequired = true });
        node.Output.Add(new NodeConnectorViewModel { Title = "Value", PortType = NodePortType.Text });
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
            var name = HdcNodeSupport.Text(ReadInput("Name"));
            if (string.IsNullOrWhiteSpace(name))
            {
                WriteOutput("Status", "missing-name");
                throw new InvalidOperationException(
                    "The Name input is required before reading an HDC parameter. " +
                    "读取 HDC 参数前必须提供 Name 输入。");
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            var value = await HdcDeviceService.FromSettings()
                .GetPropertyAsync(device, name, context.CancellationToken).ConfigureAwait(false);
            WriteOutput("Value", value);
            WriteOutput("Status", "completed");
            WriteOutput("Output", value);
            context.Log.Write(LogLevel.Info, Title, $"HDC property '{name}' read on {device.Serial}.  HDC 属性已读取。");
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
