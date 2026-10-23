using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Registry;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// ADB file transfer nodes: push a local file to the device and pull a device file to a local destination.
/// Both consume the device monitor's managed sync session and never open a socket themselves.
/// <para>ADB 文件传输节点：把本地文件推送到设备，或把设备文件拉取到本地目标。两者都消费设备监视服务的受管同步
/// 会话，自身不打开套接字。</para>
/// </summary>
public sealed class DeviceAdbNodeViewModel : FlowNodeViewModel
{
    private DeviceAdbNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Catalog-independent specifications for the two ADB file transfer node types.
    /// <para>两个 ADB 文件传输节点类型的目录外规格。</para>
    /// </summary>
    public static IReadOnlyList<DeviceProtocolNodeSpec> Specs { get; } =
    [
        new("Device.Adb.Push", DeviceProtocolKind.Adb,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, CreatePushNode),
        new("Device.Adb.Pull", DeviceProtocolKind.Adb,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            CreatePullNode)
    ];

    /// <summary>
    /// Creates the ADB push node.
    /// <para>创建 ADB 推送节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceAdbNodeViewModel CreatePushNode()
    {
        var node = new DeviceAdbNodeViewModel("ADB Push File")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Adb.Push"
        };
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Device",
            PortType = NodePortType.Device,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Local Path",
            PortType = NodePortType.Path,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Remote Path",
            PortType = NodePortType.Text,
            IsRequired = true
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Bytes", PortType = NodePortType.Number });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Creates the ADB pull node.
    /// <para>创建 ADB 拉取节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceAdbNodeViewModel CreatePullNode()
    {
        var node = new DeviceAdbNodeViewModel("ADB Pull File")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Adb.Pull"
        };
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Device",
            PortType = NodePortType.Device,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Remote Path",
            PortType = NodePortType.Text,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Local Path",
            PortType = NodePortType.Path,
            IsRequired = true
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Bytes", PortType = NodePortType.Number });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
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
                case "Device.Adb.Push":
                    await ExecutePushAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Adb.Pull":
                    await ExecutePullAsync(context).ConfigureAwait(false);
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
        catch (InvalidOperationException)
        {
            DeviceProtocolNodeSupport.WriteFailure(this, "failed");
            throw;
        }
        catch (Exception ex)
        {
            DeviceProtocolNodeSupport.WriteFailure(this, "failed");
            context.Log.Write(LogLevel.Error, Title, $"ADB transfer failed: {ex.Message}  ADB 文件传输失败。");
            throw new InvalidOperationException($"ADB transfer failed: {ex.Message}  ADB 文件传输失败。", ex);
        }
    }

    private async Task ExecutePushAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Adb, "ADB");
        var localPath = DeviceProtocolNodeSupport.RequireFile(this, "Local Path", "invalid-local-path");
        var remotePath = DeviceProtocolNodeSupport.RequireText(this, "Remote Path", "missing-remote-path");

        context.ReportProgress(0);
        await App.DeviceMonitor.PushAdbFileAsync(device, localPath, remotePath, context.CancellationToken)
            .ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();

        var bytes = new FileInfo(localPath).Length;
        WriteOutput("Result", true);
        WriteOutput("Bytes", (double)bytes);
        WriteOutput("Status", "pushed");
        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title,
            $"Pushed '{localPath}' to '{remotePath}' ({bytes} bytes).  已推送文件到 ADB 设备。");
    }

    private async Task ExecutePullAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Adb, "ADB");
        var remotePath = DeviceProtocolNodeSupport.RequireText(this, "Remote Path", "missing-remote-path");
        var destination = DeviceProtocolNodeSupport.RequireOutputPath(this, "Local Path", "missing-local-path");
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The local path has no parent directory. 本地路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        context.ReportProgress(0);
        try
        {
            await App.DeviceMonitor.PullAdbFileAsync(device, remotePath, temporary, context.CancellationToken)
                .ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        var bytes = new FileInfo(destination).Length;
        WriteOutput("Path", destination);
        WriteOutput("Bytes", (double)bytes);
        WriteOutput("Status", "pulled");
        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title,
            $"Pulled '{remotePath}' to '{destination}' ({bytes} bytes).  已从 ADB 设备拉取文件。");
    }
}