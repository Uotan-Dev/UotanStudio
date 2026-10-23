using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// HDC pull node: receives one remote path into a local file through the host's existing HDC file-transfer
/// capability, publishing the destination only after a successful transfer.
/// <para>HDC 拉取节点：通过宿主现有的 HDC 文件传输能力，把远端路径接收到本地文件，仅在传输成功后发布目标路径。</para>
/// </summary>
public sealed class HdcPullNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Hdc.Pull";

    /// <summary>
    /// Creates an HDC pull node with the specified display title.
    /// <para>创建具有指定显示标题的 HDC 拉取节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public HdcPullNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the HDC pull node with its ports.
    /// <para>创建带端口的 HDC 拉取节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static HdcPullNodeViewModel CreatePullNode()
    {
        var node = new HdcPullNodeViewModel("HDC Pull")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Remote Path", PortType = NodePortType.Text, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel { Title = "Local Path", PortType = NodePortType.Path, IsRequired = true });
        node.Output.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Receives the remote path into the local destination.
    /// <para>把远端路径接收到本地目标。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The target or path input is missing. <para>目标或路径输入缺失。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, NodeTypeKey, StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        try
        {
            var serial = HdcNodeSupport.Text(ReadInput("Serial"));
            if (!HdcNodeSupport.TryResolveDevice(ReadInput("Device"), serial, out var device, out var status))
            {
                WriteOutput("Status", status);
                var message = HdcNodeSupport.DeviceFailureMessage(status, serial);
                throw status == "invalid-device"
                    ? new NotSupportedException(message)
                    : new InvalidOperationException(message);
            }

            var remotePath = HdcNodeSupport.Text(ReadInput("Remote Path"));
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                WriteOutput("Status", "missing-remote");
                throw new InvalidOperationException("The Remote Path input is required before pulling.");
            }

            var localPath = HdcNodeSupport.Text(ReadInput("Local Path"));
            if (string.IsNullOrWhiteSpace(localPath))
            {
                WriteOutput("Status", "missing-local");
                throw new InvalidOperationException("The Local Path input is required before pulling.");
            }

            var destination = Path.GetFullPath(localPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            context.CancellationToken.ThrowIfCancellationRequested();
            var result = await HdcDeviceService.FromSettings()
                .ReceiveFileAsync(device!, remotePath, destination, context.CancellationToken).ConfigureAwait(false);
            WriteOutput("Path", destination);
            WriteOutput("Result", true);
            WriteOutput("Status", string.IsNullOrWhiteSpace(result) ? "completed" : result);
            context.Log.Write(LogLevel.Info, Title, $"HDC pull completed on {device!.Serial}.  HDC 拉取已完成。");
        }
        catch (OperationCanceledException)
        {
            WriteOutput("Result", false);
            WriteOutput("Status", "cancelled");
            throw;
        }
        catch
        {
            WriteOutput("Result", false);
            if (ReadOutput("Status") is null)
            {
                WriteOutput("Status", "failed");
            }

            throw;
        }
    }

    private object? ReadOutput(string title) =>
        Output.FirstOrDefault(connector => string.Equals(connector.Title, title, StringComparison.Ordinal))?.Data;
}