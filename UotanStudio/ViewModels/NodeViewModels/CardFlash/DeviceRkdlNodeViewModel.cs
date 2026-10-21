using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// RKDL protocol node set: partition flash with an optional loader, partition read through the Rockchip
/// parameter table and an endpoint wait. Session creation stays inside the device monitor; the node never opens
/// a USB handle itself.
/// <para>RKDL 协议节点集：带可选 loader 的分区刷写、按 Rockchip parameter 表的分区回读与端点等待。
/// 会话创建仍由设备监视服务负责，节点自身不打开 USB 句柄。</para>
/// </summary>
public sealed class DeviceRkdlNodeViewModel : FlowNodeViewModel
{
    private DeviceRkdlNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Catalog-independent specifications for the three RKDL node types.
    /// <para>三个 RKDL 节点类型的目录外规格。</para>
    /// </summary>
    public static IReadOnlyList<DeviceProtocolNodeSpec> Specs { get; } =
    [
        new("Device.Rkdl.Flash", DeviceProtocolKind.Rkdl,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, CreateFlashNode),
        new("Device.Rkdl.ReadPartition", DeviceProtocolKind.Rkdl,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            CreateReadPartitionNode),
        new("Device.Rkdl.Wait", DeviceProtocolKind.Rkdl,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            CreateWaitNode)
    ];

    /// <summary>
    /// Creates the RKDL partition flash node with an optional loader path.
    /// <para>创建带可选 loader 路径的 RKDL 分区刷写节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceRkdlNodeViewModel CreateFlashNode()
    {
        var node = new DeviceRkdlNodeViewModel("RKDL Flash Partition")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Rkdl.Flash"
        };
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Device",
            PortType = NodePortType.Device,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Partition",
            PortType = NodePortType.Text,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Image",
            PortType = NodePortType.Path,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Loader", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Creates the RKDL partition read node using the Rockchip parameter partition table.
    /// <para>创建使用 Rockchip parameter 分区表的 RKDL 分区回读节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceRkdlNodeViewModel CreateReadPartitionNode()
    {
        var node = new DeviceRkdlNodeViewModel("RKDL Read Partition")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Rkdl.ReadPartition"
        };
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Device",
            PortType = NodePortType.Device,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Partition",
            PortType = NodePortType.Text,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Parameter",
            PortType = NodePortType.Path,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Output Path",
            PortType = NodePortType.Path,
            IsRequired = true
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Bytes", PortType = NodePortType.Number });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Creates the RKDL endpoint wait node.
    /// <para>创建 RKDL 端点等待节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceRkdlNodeViewModel CreateWaitNode()
    {
        var node = new DeviceRkdlNodeViewModel("Wait for RKDL Device")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Rkdl.Wait"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Timeout",
            PortType = NodePortType.Number,
            InitialValue = 60d
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Interval",
            PortType = NodePortType.Number,
            InitialValue = 1d
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Elapsed", PortType = NodePortType.Number });
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
                case "Device.Rkdl.Flash":
                    await ExecuteFlashAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Rkdl.ReadPartition":
                    await ExecuteReadPartitionAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Rkdl.Wait":
                    await ExecuteWaitAsync(context).ConfigureAwait(false);
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
            context.Log.Write(LogLevel.Error, Title, $"RKDL node failed: {ex.Message}  RKDL 节点执行失败。");
            throw new InvalidOperationException($"RKDL node failed: {ex.Message}  RKDL 节点执行失败。", ex);
        }
    }

    private async Task ExecuteFlashAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Rkdl, "RKDL");
        var partition = DeviceProtocolNodeSupport.RequireText(this, "Partition", "missing-partition");
        var image = DeviceProtocolNodeSupport.RequireFile(this, "Image", "invalid-image");
        var loader = DeviceProtocolNodeSupport.OptionalFile(this, "Loader", "invalid-loader");

        context.ReportProgress(0);
        var progress = DeviceProtocolNodeSupport.CreateFlashProgress(context);
        var result = await FlashService.FlashRkdlAsync(new DeviceInfo(device), partition, image, loader,
            progress, context.CancellationToken).ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();

        WriteOutput("Result", result.Success);
        WriteOutput("Status", result.Summary);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"RKDL flash failed: {result.Summary}  RKDL 刷写失败。");
            throw new InvalidOperationException(result.Summary);
        }

        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  RKDL 刷写完成。");
    }

    private async Task ExecuteReadPartitionAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Rkdl, "RKDL");
        var partitionName = DeviceProtocolNodeSupport.RequireText(this, "Partition", "missing-partition");
        var parameterPath = DeviceProtocolNodeSupport.RequireFile(this, "Parameter", "invalid-parameter");
        var destination = DeviceProtocolNodeSupport.RequireOutputPath(this, "Output Path", "missing-output");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var partitions = RockchipParameterParser.Parse(parameterPath);
        var target = partitions.FirstOrDefault(partition =>
            string.Equals(partition.Name, partitionName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            WriteOutput("Status", "partition-not-found");
            throw new InvalidOperationException(
                $"Partition '{partitionName}' is not present in the Rockchip parameter table. " +
                $"Rockchip parameter 表中不存在分区 '{partitionName}'。");
        }
        if (target.SizeSectors <= 0)
        {
            WriteOutput("Status", "empty-partition");
            throw new InvalidOperationException(
                $"Partition '{partitionName}' declares no sectors in the parameter table. " +
                $"parameter 表中分区 '{partitionName}' 未声明扇区数。");
        }

        var lengthBytes = checked(target.SizeSectors * RockchipParameterParser.SectorSize);
        context.ReportProgress(0);
        var progress = new Progress<long>(read =>
            context.ReportProgress(Math.Clamp((double)read / lengthBytes, 0.0, 1.0)));
        var written = await RkdlPartitionReader.ReadAsync(device, target, destination, progress,
            context.CancellationToken).ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();

        WriteOutput("Path", destination);
        WriteOutput("Bytes", (double)written);
        WriteOutput("Status", "read");
        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title,
            $"Read '{target.Name}' ({written} bytes) to '{destination}'.  已回读 RKDL 分区。");
    }

    private async Task ExecuteWaitAsync(INodeExecutionContext context)
    {
        var serial = DeviceProtocolNodeSupport.Text(ReadInput("Serial"));
        var timeout = NodeDataUtil.ToDouble(ReadInput("Timeout"), 60);
        var interval = NodeDataUtil.ToDouble(ReadInput("Interval"), 1);
        var (device, elapsed) = await DeviceProtocolNodeSupport.WaitForDeviceAsync(this, context,
            DeviceProtocolKind.Rkdl, serial, timeout, interval).ConfigureAwait(false);

        WriteOutput("Device", device);
        WriteOutput("Status", "found");
        WriteOutput("Elapsed", elapsed.TotalSeconds);
        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title,
            $"RKDL device {device.Serial} appeared after {elapsed.TotalSeconds:0.###} s.  已等待到 RKDL 设备。");
    }
}