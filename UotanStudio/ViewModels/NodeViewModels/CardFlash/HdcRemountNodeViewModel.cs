using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// HDC remount node: requests the selected HDC target's native remount service after the run-time confirmation
/// gate.
/// <para>HDC 重新挂载节点：在运行期确认门通过后，请求选定 HDC 目标的原生重新挂载服务。</para>
/// </summary>
public sealed class HdcRemountNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Hdc.Remount";

    /// <summary>
    /// Creates an HDC remount node with the specified display title.
    /// <para>创建具有指定显示标题的 HDC 重新挂载节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public HdcRemountNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the HDC remount node with its ports.
    /// <para>创建带端口的 HDC 重新挂载节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static HdcRemountNodeViewModel CreateRemountNode()
    {
        var node = new HdcRemountNodeViewModel("HDC Remount")
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
                .RemountAsync(device, context.CancellationToken).ConfigureAwait(false);
            WriteOutput("Status", "completed");
            WriteOutput("Output", output);
            context.Log.Write(LogLevel.Info, Title, $"HDC remount completed on {device.Serial}.  HDC 重新挂载已完成。");
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