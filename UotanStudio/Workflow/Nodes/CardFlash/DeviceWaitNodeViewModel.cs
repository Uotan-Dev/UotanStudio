using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Registry;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Device wait node: polls the device monitor by serial and protocol until a matching endpoint appears, then
/// publishes it on the Device port. A timeout fails the node instead of returning an absent device.
/// <para>设备等待节点：按序列号与协议轮询设备监视服务，直到出现匹配端点并在 Device 端口发布。超时会使节点失败，
/// 而不是返回不存在的设备。</para>
/// </summary>
public sealed class DeviceWaitNodeViewModel : FlowNodeViewModel
{
    private DeviceWaitNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Catalog-independent specification for the protocol-agnostic device wait node.
    /// <para>与协议无关的设备等待节点的目录外规格。</para>
    /// </summary>
    public static IReadOnlyList<DeviceProtocolNodeSpec> Specs { get; } =
    [
        new("Device.Wait.ForDevice", null,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            CreateWaitForDeviceNode)
    ];

    /// <summary>
    /// Creates the device wait node.
    /// <para>创建设备等待节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceWaitNodeViewModel CreateWaitForDeviceNode()
    {
        var node = new DeviceWaitNodeViewModel("Wait for Device")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Wait.ForDevice"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Protocol",
            PortType = NodePortType.Text,
            InitialValue = "any"
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Timeout",
            PortType = NodePortType.Number,
            InitialValue = 30d
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
            if (!string.Equals(TypeKey, "Device.Wait.ForDevice", StringComparison.Ordinal))
            {
                await base.ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            var serial = DeviceProtocolNodeSupport.Text(ReadInput("Serial"));
            var protocol = ParseProtocol(DeviceProtocolNodeSupport.Text(ReadInput("Protocol")));
            var timeout = NodeDataUtil.ToDouble(ReadInput("Timeout"), 30);
            var interval = NodeDataUtil.ToDouble(ReadInput("Interval"), 1);
            var (device, elapsed) = await DeviceProtocolNodeSupport.WaitForDeviceAsync(this, context, protocol,
                serial, timeout, interval).ConfigureAwait(false);

            WriteOutput("Device", device);
            WriteOutput("Status", "found");
            WriteOutput("Elapsed", elapsed.TotalSeconds);
            context.ReportProgress(1);
            context.Log.Write(LogLevel.Info, Title,
                $"Device {device.Serial} ({device.Protocol}) appeared after {elapsed.TotalSeconds:0.###} s. " +
                "已等待到设备。");
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
            context.Log.Write(LogLevel.Error, Title, $"Wait for device failed: {ex.Message}  等待设备失败。");
            throw new InvalidOperationException($"Wait for device failed: {ex.Message}  等待设备失败。", ex);
        }
    }

    private static DeviceProtocolKind? ParseProtocol(string? text) =>
        text?.Trim().ToLowerInvariant() switch
        {
            null or "" or "any" => null,
            "adb" => DeviceProtocolKind.Adb,
            "fastboot" => DeviceProtocolKind.Fastboot,
            "edl" => DeviceProtocolKind.Edl,
            "rkdl" => DeviceProtocolKind.Rkdl,
            "mtk" => DeviceProtocolKind.Mtk,
            "hdc" => DeviceProtocolKind.Hdc,
            _ => throw new InvalidOperationException(
                $"Unsupported device protocol '{text}'. 不支持的设备协议 '{text}'。")
        };
}