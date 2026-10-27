using System.Globalization;
using Avalonia;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// MediaTek operation node: connects a serial agent or a USB Boot ROM lease (or reuses a session handle),
/// then runs one flash, erase, read, backup, GPT repair, full dump or return-to-Boot-ROM action.
/// <para>联发科操作节点：建立串口代理或 USB Boot ROM 租约（或复用会话句柄），随后执行刷写、擦除、读取、备份、
/// 分区表修复或整片转储操作。</para>
/// </summary>
/// <remarks>
/// Write, erase, GPT repair and return-to-Boot-ROM actions require the Confirm input before any device byte
/// is sent. The node refuses a USB session handle because a lease cannot outlive the transaction that opened it;
/// USB targets are connected inside this node through the shared device monitor.
/// <para>写入、擦除、分区表修复与返回 Boot ROM 在任何设备字节下发之前都要求 Confirm 输入。节点拒绝 USB 会话句柄，
/// 因为租约无法存活到打开它的事务之外；USB 目标在本节点内部经共享设备监视器连接。</para>
/// </remarks>
public sealed class MtkDeviceOperationNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Mtk.Operation";
    private const int EraseTimeoutMs = 120000;

    /// <summary>
    /// Creates a MediaTek operation node with the specified display title.
    /// <para>创建具有指定显示标题的联发科操作节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public MtkDeviceOperationNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the MediaTek operation node with its ports.
    /// <para>创建带端口的联发科操作节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static MtkDeviceOperationNodeViewModel CreateOperationNode()
    {
        var node = new MtkDeviceOperationNodeViewModel("MTK Operation")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Session", PortType = NodePortType.Any });
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Port", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Baud Rate",
            PortType = NodePortType.Number,
            InitialValue = MtkNodeSupport.DefaultBaudRate,
            IsAdvanced = true
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Loader", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "EMI Path", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Protocol",
            PortType = NodePortType.Text,
            InitialValue = "XFlash"
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Storage",
            PortType = NodePortType.Text,
            InitialValue = "Emmc"
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Physical Partition",
            PortType = NodePortType.Text,
            InitialValue = "0",
            IsAdvanced = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Operation",
            PortType = NodePortType.Text,
            InitialValue = "Flash"
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partition", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Image", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Scatter", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Path", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Timeout",
            PortType = NodePortType.Number,
            InitialValue = 0,
            IsAdvanced = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Verify",
            PortType = NodePortType.Boolean,
            InitialValue = false,
            IsAdvanced = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Confirm",
            PortType = NodePortType.Boolean,
            InitialValue = false
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Bytes", PortType = NodePortType.Number });
        return node;
    }

    /// <summary>
    /// Executes the selected MediaTek operation against a fresh or handed-over session.
    /// <para>在新建或传入的会话上执行选定的联发科操作。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">An input is missing, invalid or unconfirmed. <para>输入缺失、无效或未确认。</para></exception>
    /// <exception cref="NotSupportedException">The selected device is not an MTK target. <para>选定设备不是联发科目标。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, NodeTypeKey, StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        try
        {
            await ExecuteOperationAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            WriteOutput("Result", false);
            WriteOutput("Status", "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            WriteOutput("Result", false);
            if (ReadOutput("Status") is null)
            {
                WriteOutput("Status", "failed");
            }

            context.Log.Write(LogLevel.Error, Title, $"MTK operation failed: {ex.Message}  联发科操作失败。");
            throw;
        }
    }

    private async Task ExecuteOperationAsync(INodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!MtkNodeSupport.TryParseOperation(Text(ReadInput("Operation")), out var operation))
        {
            WriteOutput("Status", "invalid-operation");
            throw new InvalidOperationException(
                "The Operation input must be Flash, Erase, Read, Backup, RepairGpt, FullDump or ReturnToBrom.");
        }

        var sessionValue = ReadInput("Session");
        var handle = sessionValue as MtkSessionHandle;
        if (sessionValue is not null && handle is null)
        {
            WriteOutput("Status", "invalid-session");
            throw new InvalidOperationException("The Session input must be an MTK session handle.");
        }

        if (handle is { IsDisposed: true })
        {
            WriteOutput("Status", "session-disposed");
            throw new InvalidOperationException("The MTK session handle was already released.");
        }

        MtkNodeProtocol protocol;
        MtkXFlashStorageKind storage;
        if (handle is not null)
        {
            protocol = handle.Protocol;
            storage = handle.Storage;
        }
        else
        {
            if (!MtkNodeSupport.TryParseProtocol(Text(ReadInput("Protocol")), out protocol))
            {
                WriteOutput("Status", "invalid-protocol");
                throw new InvalidOperationException("The Protocol input must be XFlash, Legacy, Xml or Scatter.");
            }

            if (!MtkNodeSupport.TryParseStorage(Text(ReadInput("Storage")), out storage))
            {
                WriteOutput("Status", "invalid-storage");
                throw new InvalidOperationException("The Storage input must be Emmc, Ufs, Nor or Nand.");
            }

            if (!MtkNodeSupport.IsStorageSupported(protocol, storage))
            {
                WriteOutput("Status", "unsupported-storage");
                throw new InvalidOperationException(
                    $"The {MtkNodeSupport.ProtocolText(protocol)} protocol does not serve the {MtkNodeSupport.StorageText(storage)} storage family.");
            }
        }

        var partition = (Text(ReadInput("Partition")) ?? string.Empty).Trim();
        var image = Text(ReadInput("Image"));
        var scatter = Text(ReadInput("Scatter"));
        var outputPath = Text(ReadInput("Output Path"));
        var confirm = NodeDataUtil.ToBool(ReadInput("Confirm"));
        var verify = NodeDataUtil.ToBool(ReadInput("Verify"));
        ValidateRequest(operation, partition, image, scatter, outputPath, confirm);

        if (handle is not null)
        {
            await RunCoreAsync(context, handle.Session, protocol, storage, handle, operation, partition, image,
                scatter, outputPath, verify).ConfigureAwait(false);
            return;
        }

        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is not null && device.Protocol != DeviceProtocolKind.Mtk)
        {
            WriteOutput("Status", "invalid-device");
            throw new NotSupportedException("The Device input must be an MTK target.");
        }

        if (device is not null && operation is MtkNodeOperation.ReturnToBrom)
        {
            await App.DeviceMonitor.RunMtkAsync(device, async session =>
            {
                await RunCoreAsync(context, session, protocol, storage, null, operation, partition, image,
                    scatter, outputPath, verify).ConfigureAwait(false);
                return true;
            }, context.CancellationToken).ConfigureAwait(false);
            return;
        }

        var port = Text(ReadInput("Port")) ?? string.Empty;
        if (device is null && string.IsNullOrWhiteSpace(port))
        {
            WriteOutput("Status", "missing-transport");
            throw new InvalidOperationException(
                "The Session, Device or Port input is required before running an MTK operation.");
        }

        var loaderPath = Text(ReadInput("Loader"));
        if (string.IsNullOrWhiteSpace(loaderPath))
        {
            WriteOutput("Status", "missing-loader");
            throw new InvalidOperationException("The Loader input is required to start an MTK agent.");
        }

        if (!File.Exists(loaderPath))
        {
            WriteOutput("Status", "missing-loader");
            throw new FileNotFoundException("The DA loader file was not found.", loaderPath);
        }

        var emiPath = Text(ReadInput("EMI Path"));
        if (!string.IsNullOrWhiteSpace(emiPath) && !File.Exists(emiPath))
        {
            WriteOutput("Status", "missing-emi");
            throw new FileNotFoundException("The EMI file was not found.", emiPath);
        }

        if (protocol == MtkNodeProtocol.Xml && !string.IsNullOrWhiteSpace(emiPath))
        {
            WriteOutput("Status", "unsupported-emi");
            throw new InvalidOperationException(
                "The XML protocol obtains DRAM resources through its host file store, not an EMI file input.");
        }

        if (device is not null)
        {
            var startup = await MtkNodeSupport.BuildStartupAsync(protocol, storage, loaderPath, emiPath,
                context.CancellationToken).ConfigureAwait(false);
            await App.DeviceMonitor.RunMtkAsync(device, async session =>
            {
                var ready = await MtkBootstrap.MatchAsync(session, MtkNodeSupport.ToDaProtocol(protocol), startup,
                    context.CancellationToken).ConfigureAwait(false);
                await RunCoreAsync(context, ready, protocol, storage, null, operation, partition, image, scatter,
                    outputPath, verify).ConfigureAwait(false);
                return true;
            }, context.CancellationToken).ConfigureAwait(false);
            return;
        }

        var baudRate = NodeDataUtil.ToInt(ReadInput("Baud Rate"), MtkNodeSupport.DefaultBaudRate);
        if (baudRate <= 0)
        {
            WriteOutput("Status", "invalid-baud-rate");
            throw new InvalidOperationException("The Baud Rate input must be a positive integer.");
        }

        context.Log.Write(LogLevel.Info, Title,
            $"Connecting the {MtkNodeSupport.ProtocolText(protocol)} agent on {port}.  正在连接串口代理。");
        using var owned = await MtkNodeSupport.ConnectSerialAsync(port, baudRate, protocol, storage, loaderPath,
            emiPath, context.CancellationToken).ConfigureAwait(false);
        await RunCoreAsync(context, owned, protocol, storage, null, operation, partition, image, scatter, outputPath,
            verify).ConfigureAwait(false);
    }

    private void ValidateRequest(MtkNodeOperation operation, string partition, string? image, string? scatter,
        string? outputPath, bool confirm)
    {
        if ((operation is MtkNodeOperation.Flash or MtkNodeOperation.Erase or MtkNodeOperation.RepairGpt
                or MtkNodeOperation.ReturnToBrom)
            && !confirm)
        {
            WriteOutput("Status", "confirmation-required");
            throw new InvalidOperationException(
                $"The Confirm input must be enabled before a MediaTek {operation} operation.");
        }

        if (operation is MtkNodeOperation.ReturnToBrom && NodeDataUtil.ToInt(ReadInput("Timeout"), 0) < 0)
        {
            WriteOutput("Status", "invalid-timeout");
            throw new InvalidOperationException(
                "The Timeout input must be zero or a positive number of seconds. 超时时间必须为零或正数秒。");
        }

        switch (operation)
        {
            case MtkNodeOperation.Flash:
                if (partition.Length == 0)
                {
                    WriteOutput("Status", "missing-partition");
                    throw new InvalidOperationException("The Partition input is required before flashing.");
                }

                if (!string.IsNullOrWhiteSpace(scatter))
                {
                    if (!File.Exists(scatter))
                    {
                        WriteOutput("Status", "missing-scatter");
                        throw new FileNotFoundException("The Scatter file was not found.", scatter);
                    }
                }
                else if (string.IsNullOrWhiteSpace(image))
                {
                    WriteOutput("Status", "missing-image");
                    throw new InvalidOperationException("The Image input is required before flashing.");
                }
                else if (!File.Exists(image))
                {
                    WriteOutput("Status", "missing-image");
                    throw new FileNotFoundException("The image file was not found.", image);
                }

                break;
            case MtkNodeOperation.Erase:
                if (partition.Length == 0)
                {
                    WriteOutput("Status", "missing-partition");
                    throw new InvalidOperationException("The Partition input is required before erasing.");
                }

                break;
            case MtkNodeOperation.Read:
                if (partition.Length == 0)
                {
                    WriteOutput("Status", "missing-partition");
                    throw new InvalidOperationException("The Partition input is required before reading.");
                }

                RequireOutputPath(outputPath);
                break;
            case MtkNodeOperation.Backup:
            case MtkNodeOperation.FullDump:
                RequireOutputPath(outputPath);
                break;
        }
    }

    private void RequireOutputPath(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            WriteOutput("Status", "missing-output");
            throw new InvalidOperationException("The Output Path input is required for this operation.");
        }
    }

    private async Task RunCoreAsync(INodeExecutionContext context, MtkSession session, MtkNodeProtocol protocol,
        MtkXFlashStorageKind storage, MtkSessionHandle? handle, MtkNodeOperation operation, string partition,
        string? image, string? scatter, string? outputPath, bool verify)
    {
        var family = MtkNodeSupport.ToDaProtocol(protocol);
        var physical = ResolvePhysicalText(handle);
        string path;
        long bytes;
        switch (operation)
        {
            case MtkNodeOperation.Flash:
                {
                    var result = await FlashAsync(context, session, family, storage, physical, partition, image, scatter,
                        verify).ConfigureAwait(false);
                    path = !string.IsNullOrWhiteSpace(scatter) ? Path.GetFullPath(scatter) : Path.GetFullPath(image!);
                    bytes = result.WrittenBytes;
                    break;
                }
            case MtkNodeOperation.Erase:
                {
                    var result = await EraseAsync(context, session, family, storage, physical, partition)
                        .ConfigureAwait(false);
                    path = string.Empty;
                    bytes = result.ErasedBytes;
                    break;
                }
            case MtkNodeOperation.Read:
                {
                    path = await ReadAsync(context, session, family, storage, physical, partition, outputPath!)
                        .ConfigureAwait(false);
                    bytes = new FileInfo(path).Length;
                    break;
                }
            case MtkNodeOperation.Backup:
                {
                    (path, bytes) = await BackupAsync(context, session, family, outputPath!).ConfigureAwait(false);
                    break;
                }
            case MtkNodeOperation.FullDump:
                {
                    (path, bytes) = await FullDumpAsync(context, session, family, outputPath!).ConfigureAwait(false);
                    break;
                }
            case MtkNodeOperation.RepairGpt:
                {
                    var result = await RepairGptAsync(context, session, family, storage).ConfigureAwait(false);
                    path = string.Empty;
                    bytes = result.WrittenBytes;
                    break;
                }
            case MtkNodeOperation.ReturnToBrom:
                {
                    var timeoutSeconds = NodeDataUtil.ToInt(ReadInput("Timeout"), 0);
                    await MtkPreloaderService.ReturnToBootRomAsync(session, timeoutSeconds, true,
                        context.CancellationToken).ConfigureAwait(false);
                    path = string.Empty;
                    bytes = 0;
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        WriteOutput("Result", true);
        WriteOutput("Status", "completed");
        WriteOutput("Path", path);
        WriteOutput("Bytes", (double)bytes);
        context.Log.Write(LogLevel.Info, Title,
            $"MTK {operation} completed: {bytes} bytes.  联发科操作已记录完成。");
    }

    private string ResolvePhysicalText(MtkSessionHandle? handle)
    {
        var text = (Text(ReadInput("Physical Partition")) ?? string.Empty).Trim();
        if (text.Length > 0 && !string.Equals(text, "0", StringComparison.Ordinal))
        {
            return text;
        }

        return handle is not null && !string.IsNullOrWhiteSpace(handle.PhysicalPartition)
            ? handle.PhysicalPartition
            : text;
    }

    private static async Task<MtkFlashPlanResult> FlashAsync(INodeExecutionContext context, MtkSession session,
        MtkDaProtocol family, MtkXFlashStorageKind storage, string physical, string partition, string? image,
        string? scatter, bool verify)
    {
        var token = context.CancellationToken;
        var progress = PlanProgress(context);
        switch (family)
        {
            case MtkDaProtocol.XFlash:
                {
                    var snapshot = await session.ReadXFlashGptAsync(MtkNodeSupport.ResolveXFlashPartition(storage, physical),
                        token).ConfigureAwait(false);
                    using var plan = PrepareXFlashPlan(snapshot, scatter, partition, image, verify, token);
                    return await session.ExecuteXFlashPlanAsync(plan, progress, EraseTimeoutMs, token)
                        .ConfigureAwait(false);
                }
            case MtkDaProtocol.Legacy:
                {
                    var snapshot = await session.ReadLegacyGptAsync(MtkNodeSupport.ResolveLegacyPartition(storage, physical),
                        token).ConfigureAwait(false);
                    using var plan = PrepareLegacyPlan(snapshot, scatter, partition, image, verify, token);
                    return await session.ExecuteLegacyPlanAsync(plan, progress, EraseTimeoutMs, token)
                        .ConfigureAwait(false);
                }
            case MtkDaProtocol.Xml:
                {
                    var snapshot = await session.ReadXmlGptAsync(MtkNodeSupport.ResolveXmlRegion(storage, physical),
                        cancellationToken: token).ConfigureAwait(false);
                    using var plan = PrepareXmlPlan(snapshot, scatter, partition, image, verify, token);
                    return await session.ExecuteXmlPlanAsync(plan, progress, EraseTimeoutMs, token)
                        .ConfigureAwait(false);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(family));
        }
    }

    private static MtkFlashPlan PrepareXFlashPlan(MtkGptSnapshot snapshot, string? scatter, string partition,
        string? image, bool verify, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(scatter))
        {
            var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));
            var row = MtkNodeSupport.FindScatterRow(resolution, partition);
            return MtkFlashPlan.Prepare(snapshot, resolution.Storage,
                [MtkScatterPackage.CreateStep(resolution, row, verify)], token);
        }

        var length = new FileInfo(image!).Length;
        return MtkFlashPlan.Prepare(snapshot,
            [new MtkPartitionFlashStep(MtkFlashStepKind.Raw, partition, 0, length, image, 0, verify)], token);
    }

    private static MtkLegacyFlashPlan PrepareLegacyPlan(MtkLegacyGptSnapshot snapshot, string? scatter,
        string partition, string? image, bool verify, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(scatter))
        {
            var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));
            var row = MtkNodeSupport.FindScatterRow(resolution, partition);
            return MtkLegacyFlashPlan.Prepare(snapshot,
                [MtkScatterPackage.CreateLegacyStep(resolution, row, verify)], token);
        }

        var length = new FileInfo(image!).Length;
        return MtkLegacyFlashPlan.Prepare(snapshot,
            [new MtkLegacyPartitionFlashStep(partition, MtkFlashStepKind.Raw, 0, length, image, 0, verify)], token);
    }

    private static MtkXmlFlashPlan PrepareXmlPlan(MtkXmlGptSnapshot snapshot, string? scatter, string partition,
        string? image, bool verify, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(scatter))
        {
            var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));
            var row = MtkNodeSupport.FindScatterRow(resolution, partition);
            return MtkXmlFlashPlan.Prepare(snapshot,
                [MtkScatterPackage.CreateXmlStep(resolution, row, verify)], token);
        }

        var length = new FileInfo(image!).Length;
        return MtkXmlFlashPlan.Prepare(snapshot,
            [new MtkXmlPartitionFlashStep(partition, MtkFlashStepKind.Raw, 0, length, image, 0, verify)], token);
    }

    private static async Task<MtkFlashPlanResult> EraseAsync(INodeExecutionContext context, MtkSession session,
        MtkDaProtocol family, MtkXFlashStorageKind storage, string physical, string partition)
    {
        var token = context.CancellationToken;
        var progress = PlanProgress(context);
        switch (family)
        {
            case MtkDaProtocol.XFlash:
                {
                    var snapshot = await session.ReadXFlashGptAsync(MtkNodeSupport.ResolveXFlashPartition(storage, physical),
                        token).ConfigureAwait(false);
                    var length = MtkNodeSupport.PartitionLength(snapshot, partition);
                    using var plan = MtkFlashPlan.Prepare(snapshot,
                        [new MtkPartitionFlashStep(MtkFlashStepKind.Erase, partition, 0, length)], token);
                    return await session.ExecuteXFlashPlanAsync(plan, progress, EraseTimeoutMs, token)
                        .ConfigureAwait(false);
                }
            case MtkDaProtocol.Legacy:
                {
                    var snapshot = await session.ReadLegacyGptAsync(MtkNodeSupport.ResolveLegacyPartition(storage, physical),
                        token).ConfigureAwait(false);
                    var length = MtkNodeSupport.PartitionLength(snapshot, partition);
                    using var plan = MtkLegacyFlashPlan.Prepare(snapshot,
                        [new MtkLegacyPartitionFlashStep(partition, MtkFlashStepKind.Erase, 0, length)], token);
                    return await session.ExecuteLegacyPlanAsync(plan, progress, EraseTimeoutMs, token)
                        .ConfigureAwait(false);
                }
            case MtkDaProtocol.Xml:
                {
                    var snapshot = await session.ReadXmlGptAsync(MtkNodeSupport.ResolveXmlRegion(storage, physical),
                        cancellationToken: token).ConfigureAwait(false);
                    var length = MtkNodeSupport.PartitionLength(snapshot, partition);
                    using var plan = MtkXmlFlashPlan.Prepare(snapshot,
                        [new MtkXmlPartitionFlashStep(partition, MtkFlashStepKind.Erase, 0, length)], token);
                    return await session.ExecuteXmlPlanAsync(plan, progress, EraseTimeoutMs, token)
                        .ConfigureAwait(false);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(family));
        }
    }

    private static async Task<string> ReadAsync(INodeExecutionContext context, MtkSession session,
        MtkDaProtocol family, MtkXFlashStorageKind storage, string physical, string partition, string outputPath)
    {
        var token = context.CancellationToken;
        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        switch (family)
        {
            case MtkDaProtocol.XFlash:
                {
                    var snapshot = await session.ReadXFlashGptAsync(MtkNodeSupport.ResolveXFlashPartition(storage, physical),
                        token).ConfigureAwait(false);
                    await session.ReadXFlashPartitionToFileAsync(snapshot, partition, destination,
                        cancellationToken: token).ConfigureAwait(false);
                    break;
                }
            case MtkDaProtocol.Legacy:
                {
                    var snapshot = await session.ReadLegacyGptAsync(MtkNodeSupport.ResolveLegacyPartition(storage, physical),
                        token).ConfigureAwait(false);
                    await session.ReadLegacyPartitionToFileAsync(snapshot, partition, destination,
                        cancellationToken: token).ConfigureAwait(false);
                    break;
                }
            case MtkDaProtocol.Xml:
                {
                    var snapshot = await session.ReadXmlGptAsync(MtkNodeSupport.ResolveXmlRegion(storage, physical),
                        cancellationToken: token).ConfigureAwait(false);
                    await session.ReadXmlPartitionToFileAsync(snapshot, partition, destination,
                        cancellationToken: token).ConfigureAwait(false);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(family));
        }

        return destination;
    }

    private static async Task<(string Directory, long Bytes)> BackupAsync(INodeExecutionContext context,
        MtkSession session, MtkDaProtocol family, string outputPath)
    {
        var directory = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(directory);
        var progress = new Progress<FlashProgress>(update => context.ReportProgress(update.Percent / 100.0));
        var result = await MtkBulkReadService.ReadWholeFlashAsync(session, family, directory, progress,
            context.CancellationToken).ConfigureAwait(false);
        return (directory, result.TotalBytes);
    }

    private static async Task<(string Path, long Bytes)> FullDumpAsync(INodeExecutionContext context,
        MtkSession session, MtkDaProtocol family, string outputPath)
    {
        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var progress = new Progress<FlashProgress>(update => context.ReportProgress(update.Percent / 100.0));
        var record = await MtkFullDumpService.CaptureAsync(session, family, destination, progress,
            context.CancellationToken).ConfigureAwait(false);
        return (destination, record.ByteLength);
    }

    private static async Task<MtkFlashPlanResult> RepairGptAsync(INodeExecutionContext context, MtkSession session,
        MtkDaProtocol family, MtkXFlashStorageKind storage)
    {
        using var preparation = await MtkGptRepairService.PrepareAsync(session, family, storage,
            context.CancellationToken).ConfigureAwait(false);
        return await preparation.ExecuteAsync(PlanProgress(context), EraseTimeoutMs, context.CancellationToken)
            .ConfigureAwait(false);
    }

    private static IProgress<MtkFlashPlanProgress> PlanProgress(INodeExecutionContext context) =>
        new Progress<MtkFlashPlanProgress>(update =>
        {
            if (update.TotalBytes > 0)
            {
                context.ReportProgress((double)update.ProcessedBytes / update.TotalBytes);
            }
        });

    private object? ReadOutput(string title) =>
        Output.FirstOrDefault(connector => string.Equals(connector.Title, title, StringComparison.Ordinal))?.Data;

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}