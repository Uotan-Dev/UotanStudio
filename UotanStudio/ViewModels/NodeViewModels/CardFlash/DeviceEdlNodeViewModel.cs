using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// EDL protocol node set: named-partition flash, erase, backup, GPT repair, UFS provisioning, UFS reset and
/// partition read. Every operation goes through the existing EDL services; the node never opens a USB handle or
/// frames a Firehose command itself.
/// <para>EDL 协议节点集：按名刷写、擦除、备份、GPT 修复、UFS 配置、UFS 复位与分区回读。所有操作都经现有 EDL 服务执行；
/// 节点自身不打开 USB 句柄，也不自行成帧 Firehose 命令。</para>
/// </summary>
public sealed class DeviceEdlNodeViewModel : FlowNodeViewModel
{
    private DeviceEdlNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Catalog-independent specifications for the seven EDL node types.
    /// <para>七个 EDL 节点类型的目录外规格。</para>
    /// </summary>
    public static IReadOnlyList<DeviceProtocolNodeSpec> Specs { get; } =
    [
        new("Device.Edl.Flash", DeviceProtocolKind.Edl,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, CreateFlashNode),
        new("Device.Edl.Erase", DeviceProtocolKind.Edl,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, CreateEraseNode),
        new("Device.Edl.Backup", DeviceProtocolKind.Edl,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            CreateBackupNode),
        new("Device.Edl.RepairGpt", DeviceProtocolKind.Edl,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, CreateRepairGptNode),
        new("Device.Edl.UfsProvision", DeviceProtocolKind.Edl,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, CreateUfsProvisionNode),
        new("Device.Edl.UfsReset", DeviceProtocolKind.Edl,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, CreateUfsResetNode),
        new("Device.Edl.ReadPartition", DeviceProtocolKind.Edl,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            CreateReadPartitionNode)
    ];

    /// <summary>
    /// Creates the named-partition EDL flash node.
    /// <para>创建按名刷写的 EDL 节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceEdlNodeViewModel CreateFlashNode()
    {
        var node = new DeviceEdlNodeViewModel("EDL Flash Partition")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Edl.Flash"
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
        node.Input.Add(new NodeConnectorViewModel { Title = "Programmer", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "LUN", PortType = NodePortType.Number });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Verify",
            PortType = NodePortType.Boolean,
            InitialValue = false
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Creates the named-partition EDL erase node.
    /// <para>创建按名擦除的 EDL 节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceEdlNodeViewModel CreateEraseNode()
    {
        var node = new DeviceEdlNodeViewModel("EDL Erase Partition")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Edl.Erase"
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
        node.Input.Add(new NodeConnectorViewModel { Title = "Programmer", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "LUN", PortType = NodePortType.Number });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Creates the EDL partition backup node backed by the shared backup service.
    /// <para>创建由共享备份服务支撑的 EDL 分区备份节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceEdlNodeViewModel CreateBackupNode()
    {
        var node = new DeviceEdlNodeViewModel("EDL Backup Partition")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Edl.Backup"
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
    /// Creates the EDL GPT repair node.
    /// <para>创建 EDL GPT 修复节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceEdlNodeViewModel CreateRepairGptNode()
    {
        var node = new DeviceEdlNodeViewModel("EDL Repair GPT")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Edl.RepairGpt"
        };
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Device",
            PortType = NodePortType.Device,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Programmer", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "LUN", PortType = NodePortType.Number });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Creates the EDL UFS provisioning node with an optional preview mode.
    /// <para>创建带可选预览模式的 EDL UFS 配置节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceEdlNodeViewModel CreateUfsProvisionNode()
    {
        var node = new DeviceEdlNodeViewModel("EDL UFS Provision")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Edl.UfsProvision"
        };
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Device",
            PortType = NodePortType.Device,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Profile",
            PortType = NodePortType.Path,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Programmer", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Preview",
            PortType = NodePortType.Boolean,
            InitialValue = false
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Creates the EDL UFS reset node that discards the target's pending UFS configuration after confirmation.
    /// <para>创建在确认后丢弃目标待生效 UFS 配置的 EDL UFS 复位节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceEdlNodeViewModel CreateUfsResetNode()
    {
        var node = new DeviceEdlNodeViewModel("EDL UFS Reset")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Edl.UfsReset"
        };
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Device",
            PortType = NodePortType.Device,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Programmer", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Confirm",
            PortType = NodePortType.Boolean,
            InitialValue = false
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Creates the EDL partition read node with an explicit programmer and LUN.
    /// <para>创建带显式 programmer 与 LUN 的 EDL 分区回读节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceEdlNodeViewModel CreateReadPartitionNode()
    {
        var node = new DeviceEdlNodeViewModel("EDL Read Partition")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Edl.ReadPartition"
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
            Title = "Output Path",
            PortType = NodePortType.Path,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Programmer", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "LUN", PortType = NodePortType.Number });
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
                case "Device.Edl.Flash":
                    await ExecuteFlashAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Edl.Erase":
                    await ExecuteEraseAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Edl.Backup":
                    await ExecuteBackupAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Edl.RepairGpt":
                    await ExecuteRepairGptAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Edl.UfsProvision":
                    await ExecuteUfsProvisionAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Edl.UfsReset":
                    await ExecuteUfsResetAsync(context).ConfigureAwait(false);
                    break;
                case "Device.Edl.ReadPartition":
                    await ExecuteReadPartitionAsync(context).ConfigureAwait(false);
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
            context.Log.Write(LogLevel.Error, Title, $"EDL node failed: {ex.Message}  EDL 节点执行失败。");
            throw new InvalidOperationException($"EDL node failed: {ex.Message}  EDL 节点执行失败。", ex);
        }
    }

    private async Task ExecuteFlashAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Edl, "EDL");
        var partition = DeviceProtocolNodeSupport.RequireText(this, "Partition", "missing-partition");
        var image = DeviceProtocolNodeSupport.RequireFile(this, "Image", "invalid-image");
        var programmer = DeviceProtocolNodeSupport.OptionalFile(this, "Programmer", "invalid-programmer");
        var lun = DeviceProtocolNodeSupport.OptionalLun(this);
        var verify = NodeDataUtil.ToBool(ReadInput("Verify"), false);

        context.ReportProgress(0);
        var progress = DeviceProtocolNodeSupport.CreateFlashProgress(context);
        var result = await EdlGptPartitionService.FlashAsync(new DeviceInfo(device), partition, image, verify,
            progress, context.CancellationToken, programmer, initialization: null, lun: lun).ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();

        WriteOutput("Result", result.Success);
        WriteOutput("Status", result.Summary);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"EDL flash failed: {result.Summary}  EDL 刷写失败。");
            throw new InvalidOperationException(result.Summary);
        }

        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  EDL 刷写完成。");
    }

    private async Task ExecuteEraseAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Edl, "EDL");
        var partition = DeviceProtocolNodeSupport.RequireText(this, "Partition", "missing-partition");
        var programmer = DeviceProtocolNodeSupport.OptionalFile(this, "Programmer", "invalid-programmer");
        var lun = DeviceProtocolNodeSupport.OptionalLun(this);

        context.ReportProgress(0);
        var progress = DeviceProtocolNodeSupport.CreateFlashProgress(context);
        var result = await EdlGptPartitionService.EraseAsync(new DeviceInfo(device), partition, progress,
            context.CancellationToken, programmer, initialization: null, lun: lun).ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();

        WriteOutput("Result", result.Success);
        WriteOutput("Status", result.Summary);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"EDL erase failed: {result.Summary}  EDL 擦除失败。");
            throw new InvalidOperationException(result.Summary);
        }

        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  EDL 擦除完成。");
    }

    private async Task ExecuteBackupAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Edl, "EDL");
        var partition = DeviceProtocolNodeSupport.RequireText(this, "Partition", "missing-partition");
        var destination = DeviceProtocolNodeSupport.RequireOutputPath(this, "Output Path", "missing-output");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        context.ReportProgress(0);
        var progress = DeviceProtocolNodeSupport.CreateBackupProgress(context);
        var result = await BackupService.BackupPartitionAsync(new DeviceInfo(device), partition, destination,
            progress, context.CancellationToken).ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();

        var bytes = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        WriteOutput("Path", destination);
        WriteOutput("Bytes", (double)bytes);
        WriteOutput("Status", result.Summary);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"EDL backup failed: {result.Summary}  EDL 备份失败。");
            throw new InvalidOperationException(result.Summary);
        }

        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title,
            $"Backed up '{partition}' to '{destination}' ({bytes} bytes).  已备份 EDL 分区。");
    }

    private async Task ExecuteRepairGptAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Edl, "EDL");
        var programmer = DeviceProtocolNodeSupport.OptionalFile(this, "Programmer", "invalid-programmer");
        var lun = DeviceProtocolNodeSupport.OptionalLun(this) ?? 0;

        context.ReportProgress(0);
        var progress = DeviceProtocolNodeSupport.CreateFlashProgress(context);
        var result = await EdlGptPartitionService.RepairGptAsync(new DeviceInfo(device), true,
            progress, context.CancellationToken, programmer, initialization: null, physicalPartition: lun)
            .ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();

        WriteOutput("Result", result.Success);
        WriteOutput("Status", result.Summary);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"EDL GPT repair failed: {result.Summary}  EDL GPT 修复失败。");
            throw new InvalidOperationException(result.Summary);
        }

        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  EDL GPT 修复完成。");
    }

    private async Task ExecuteUfsProvisionAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Edl, "EDL");
        var profile = DeviceProtocolNodeSupport.RequireFile(this, "Profile", "invalid-profile");
        var programmer = DeviceProtocolNodeSupport.OptionalFile(this, "Programmer", "invalid-programmer");
        var preview = NodeDataUtil.ToBool(ReadInput("Preview"), false);

        context.ReportProgress(0);
        var progress = DeviceProtocolNodeSupport.CreateFlashProgress(context);
        var result = preview
            ? await EdlUfsProvisionService.PreviewAsync(new DeviceInfo(device), profile, progress,
                context.CancellationToken, programmer).ConfigureAwait(false)
            : await EdlUfsProvisionService.ProvisionAsync(new DeviceInfo(device), profile, true,
                progress, context.CancellationToken, programmer).ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();

        WriteOutput("Result", result.Success);
        WriteOutput("Status", result.Summary);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"EDL UFS provisioning failed: {result.Summary}  EDL UFS 配置失败。");
            throw new InvalidOperationException(result.Summary);
        }

        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  EDL UFS 配置完成。");
    }

    private async Task ExecuteUfsResetAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Edl, "EDL");
        var programmer = DeviceProtocolNodeSupport.OptionalFile(this, "Programmer", "invalid-programmer");
        if (!NodeDataUtil.ToBool(ReadInput("Confirm")))
        {
            WriteOutput("Status", "confirmation-required");
            throw new InvalidOperationException(
                "The Confirm input must be enabled before resetting the pending UFS configuration. " +
                "复位待生效 UFS 配置前必须启用 Confirm 输入。");
        }

        context.ReportProgress(0);
        var progress = DeviceProtocolNodeSupport.CreateFlashProgress(context);
        var result = await EdlUfsProvisionService.ResetAsync(new DeviceInfo(device), confirmed: true,
            progress: progress, ct: context.CancellationToken, programmerPath: programmer).ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();

        WriteOutput("Result", result.Success);
        WriteOutput("Status", result.Summary);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"EDL UFS reset failed: {result.Summary}  EDL UFS 复位失败。");
            throw new InvalidOperationException(result.Summary);
        }

        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  EDL UFS 复位完成。");
    }

    private async Task ExecuteReadPartitionAsync(INodeExecutionContext context)
    {
        var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Edl, "EDL");
        var partition = DeviceProtocolNodeSupport.RequireText(this, "Partition", "missing-partition");
        var destination = DeviceProtocolNodeSupport.RequireOutputPath(this, "Output Path", "missing-output");
        var programmer = DeviceProtocolNodeSupport.OptionalFile(this, "Programmer", "invalid-programmer");
        var lun = DeviceProtocolNodeSupport.OptionalLun(this);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        context.ReportProgress(0);
        var progress = DeviceProtocolNodeSupport.CreateBackupProgress(context);
        var result = await EdlGptPartitionService.BackupAsync(new DeviceInfo(device), partition, destination,
            progress, context.CancellationToken, programmer, initialization: null, lun: lun).ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();

        var bytes = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        WriteOutput("Path", destination);
        WriteOutput("Bytes", (double)bytes);
        WriteOutput("Status", result.Success ? "read" : result.Summary);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"EDL partition read failed: {result.Summary}  EDL 分区回读失败。");
            throw new InvalidOperationException(result.Summary);
        }

        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title,
            $"Read '{partition}' to '{destination}' ({bytes} bytes).  已回读 EDL 分区。");
    }
}