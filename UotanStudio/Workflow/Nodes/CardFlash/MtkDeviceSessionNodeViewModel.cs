using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// MediaTek serial session node: opens one explicitly selected COM/TTY XFlash, Legacy or XML agent,
/// exposes the ready session to downstream nodes and registers it for unconditional release.
/// <para>联发科串口会话节点：在显式选定的 COM/TTY 上启动 XFlash、Legacy 或 XML 代理，把已就绪会话暴露给后续节点，
/// 并登记无条件释放。</para>
/// </summary>
/// <remarks>
/// <para>The node never discovers USB Boot ROM ports by itself; a USB lease belongs to one device transaction and
/// cannot outlive it, so the node reports that path as unsupported instead of holding a stale handle.</para>
/// <para>本节点不自行发现 USB Boot ROM 端口；USB 租约属于单次设备事务，无法跨节点存活，因此该路径会被明确报告为不支持，
/// 而不是持有一个过期句柄。</para>
/// </remarks>
public sealed class MtkDeviceSessionNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Mtk.Session";

    /// <summary>
    /// Creates a MediaTek session node with the specified display title.
    /// <para>创建具有指定显示标题的联发科会话节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public MtkDeviceSessionNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the MediaTek serial session node with its ports.
    /// <para>创建带端口的联发科串口会话节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static MtkDeviceSessionNodeViewModel CreateSessionNode()
    {
        var node = new MtkDeviceSessionNodeViewModel("MTK Session")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Port", PortType = NodePortType.Text, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Baud Rate",
            PortType = NodePortType.Number,
            InitialValue = MtkNodeSupport.DefaultBaudRate,
            IsAdvanced = true
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "DA Path", PortType = NodePortType.Path, IsRequired = true });
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
        node.Output.Add(new NodeConnectorViewModel { Title = "Session", PortType = NodePortType.Any });
        node.Output.Add(new NodeConnectorViewModel { Title = "Port", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Protocol", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Storage", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Chip", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Opens the selected serial agent and publishes the ready session.
    /// <para>打开选定的串口代理并发布已就绪会话。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">A required input is missing or invalid. <para>必填输入缺失或无效。</para></exception>
    /// <exception cref="NotSupportedException">A USB device input was supplied instead of a serial port. <para>提供了 USB 设备输入而非串口。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, NodeTypeKey, StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (NodeDataUtil.ToDevice(ReadInput("Device")) is not null)
            {
                WriteOutput("Status", "usb-session-unsupported");
                throw new NotSupportedException(
                    "A USB Boot ROM session cannot be handed to another node; use Device.Mtk.Operation with the Device input. "
                    + "USB Boot ROM 会话无法传递给其它节点；请改用 Device.Mtk.Operation 的 Device 输入。");
            }

            var port = Text(ReadInput("Port"));
            if (string.IsNullOrWhiteSpace(port))
            {
                WriteOutput("Status", "missing-port");
                throw new InvalidOperationException("The Port input is required before opening an MTK session.");
            }

            var baudRate = NodeDataUtil.ToInt(ReadInput("Baud Rate"), MtkNodeSupport.DefaultBaudRate);
            if (baudRate <= 0)
            {
                WriteOutput("Status", "invalid-baud-rate");
                throw new InvalidOperationException("The Baud Rate input must be a positive integer.");
            }

            if (!MtkNodeSupport.TryParseProtocol(Text(ReadInput("Protocol")), out var protocol))
            {
                WriteOutput("Status", "invalid-protocol");
                throw new InvalidOperationException(
                    "The Protocol input must be XFlash, Legacy, Xml or Scatter.");
            }

            if (!MtkNodeSupport.TryParseStorage(Text(ReadInput("Storage")), out var storage))
            {
                WriteOutput("Status", "invalid-storage");
                throw new InvalidOperationException(
                    "The Storage input must be Emmc, Ufs, Nor or Nand.");
            }

            if (!MtkNodeSupport.IsStorageSupported(protocol, storage))
            {
                WriteOutput("Status", "unsupported-storage");
                throw new InvalidOperationException(
                    $"The {MtkNodeSupport.ProtocolText(protocol)} protocol does not serve the {MtkNodeSupport.StorageText(storage)} storage family.");
            }

            var loaderPath = Text(ReadInput("DA Path"));
            if (string.IsNullOrWhiteSpace(loaderPath))
            {
                WriteOutput("Status", "missing-loader");
                throw new InvalidOperationException("The DA Path input is required to start an MTK agent.");
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

            var physicalPartition = Text(ReadInput("Physical Partition")) ?? "0";
            context.Log.Write(LogLevel.Info, Title,
                $"Opening the {MtkNodeSupport.ProtocolText(protocol)} agent on {port}.  正在打开串口代理。");
            var session = await MtkNodeSupport.ConnectSerialAsync(port, baudRate, protocol, storage,
                loaderPath, emiPath, context.CancellationToken).ConfigureAwait(false);
            var chip = session.DeviceInfo is { } info
                ? $"0x{info.HardwareCode:X4}/0x{info.HardwareSubCode:X4}"
                : "unknown";
            var handle = new MtkSessionHandle(session, port, protocol, storage, physicalPartition, chip);
            try
            {
                context.RegisterResource(handle);
            }
            catch
            {
                await handle.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            WriteOutput("Session", handle);
            WriteOutput("Port", port);
            WriteOutput("Protocol", MtkNodeSupport.ProtocolText(protocol));
            WriteOutput("Storage", MtkNodeSupport.StorageText(storage));
            WriteOutput("Chip", chip);
            WriteOutput("Status", "connected");
            context.Log.Write(LogLevel.Info, Title,
                $"MTK session ready on {port} ({MtkNodeSupport.ProtocolText(protocol)}, chip {chip}).  联发科会话已就绪。");
        }
        catch (OperationCanceledException)
        {
            WriteOutput("Status", "cancelled");
            throw;
        }
        catch
        {
            if (ReadOutput("Status") is null)
            {
                WriteOutput("Status", "failed");
            }

            throw;
        }
    }

    private object? ReadOutput(string title) =>
        Output.FirstOrDefault(connector => string.Equals(connector.Title, title, StringComparison.Ordinal))?.Data;

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}