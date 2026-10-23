using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// HDC uninstall node: removes one bundle by name from the selected HDC target after the run-time confirmation
/// gate.
/// <para>HDC 卸载节点：在运行期确认门通过后，按包名从选定的 HDC 目标卸载应用。</para>
/// </summary>
public sealed class HdcUninstallNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Hdc.Uninstall";

    /// <summary>
    /// Creates an HDC uninstall node with the specified display title.
    /// <para>创建具有指定显示标题的 HDC 卸载节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public HdcUninstallNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the HDC uninstall node with its ports.
    /// <para>创建带端口的 HDC 卸载节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static HdcUninstallNodeViewModel CreateUninstallNode()
    {
        var node = new HdcUninstallNodeViewModel("HDC Uninstall")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Bundle", PortType = NodePortType.Text, IsRequired = true });
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
            var bundle = HdcNodeSupport.Text(ReadInput("Bundle"));
            if (string.IsNullOrWhiteSpace(bundle))
            {
                WriteOutput("Status", "missing-bundle");
                throw new InvalidOperationException(
                    "The Bundle input is required before uninstalling an HDC application. " +
                    "卸载 HDC 应用前必须提供 Bundle 输入。");
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            var output = await HdcDeviceService.FromSettings()
                .UninstallAsync(device, bundle, context.CancellationToken).ConfigureAwait(false);
            WriteOutput("Status", "completed");
            WriteOutput("Output", output);
            context.Log.Write(LogLevel.Info, Title, $"HDC uninstall completed on {device.Serial}.  HDC 卸载已完成。");
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