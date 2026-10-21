using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// HDC install node: installs one local HAP, HSP or APP package on the selected HDC target after the run-time
/// confirmation gate.
/// <para>HDC 安装节点：在运行期确认门通过后，把本地 HAP、HSP 或 APP 包安装到选定的 HDC 目标。</para>
/// </summary>
public sealed class HdcInstallNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Hdc.Install";

    /// <summary>
    /// Creates an HDC install node with the specified display title.
    /// <para>创建具有指定显示标题的 HDC 安装节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public HdcInstallNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the HDC install node with its ports.
    /// <para>创建带端口的 HDC 安装节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static HdcInstallNodeViewModel CreateInstallNode()
    {
        var node = new HdcInstallNodeViewModel("HDC Install")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Package", PortType = NodePortType.Path, IsRequired = true });
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
            var package = HdcNodeSupport.Text(ReadInput("Package"));
            if (string.IsNullOrWhiteSpace(package))
            {
                WriteOutput("Status", "missing-package");
                throw new InvalidOperationException(
                    "The Package input is required before installing an HDC application. " +
                    "安装 HDC 应用前必须提供 Package 输入。");
            }

            var path = Path.GetFullPath(package);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                WriteOutput("Status", "missing-package");
                throw new FileNotFoundException("The package path was not found.", path);
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            var output = await HdcDeviceService.FromSettings()
                .InstallAsync(device, path, context.CancellationToken).ConfigureAwait(false);
            WriteOutput("Status", "completed");
            WriteOutput("Output", output);
            context.Log.Write(LogLevel.Info, Title, $"HDC install completed on {device.Serial}.  HDC 安装已完成。");
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
