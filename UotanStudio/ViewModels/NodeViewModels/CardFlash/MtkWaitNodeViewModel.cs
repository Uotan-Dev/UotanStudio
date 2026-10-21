using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// MediaTek wait node: polls the shared device monitor until one MTK endpoint appears, optionally matching a
/// serial, and publishes that target for the operation node.
/// <para>联发科等待节点：轮询共享设备监视器，直到出现一个 MTK 端点（可选按序列号匹配），并把该目标发布给操作节点。</para>
/// </summary>
/// <remarks>
/// <para>USB Boot ROM discovery and re-enumeration remain the device monitor's responsibility; this node only
/// observes its scan results and never opens a USB handle.</para>
/// <para>USB Boot ROM 的发现与重新枚举仍由设备监视器负责；本节点只观察其扫描结果，绝不打开 USB 句柄。</para>
/// </remarks>
public sealed class MtkWaitNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Mtk.Wait";
    private const double PollIntervalSeconds = 1;

    /// <summary>
    /// Creates a MediaTek wait node with the specified display title.
    /// <para>创建具有指定显示标题的联发科等待节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public MtkWaitNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the MediaTek wait node with its ports.
    /// <para>创建带端口的联发科等待节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static MtkWaitNodeViewModel CreateWaitNode()
    {
        var node = new MtkWaitNodeViewModel("Wait for MTK Device")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Protocol",
            PortType = NodePortType.Text,
            InitialValue = "Mtk"
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Timeout",
            PortType = NodePortType.Number,
            InitialValue = 60d
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Waits for a matching MediaTek endpoint until the timeout elapses.
    /// <para>等待匹配的联发科端点，直到超时。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The protocol is not MTK, the timeout is not positive, or no
    /// matching endpoint appeared before the deadline. <para>协议不是 MTK、超时非正，或截止时间前未出现匹配端点。</para></exception>
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
            if (!TryParseProtocol(DeviceProtocolNodeSupport.Text(ReadInput("Protocol"))))
            {
                WriteOutput("Status", "invalid-protocol");
                throw new InvalidOperationException(
                    "The Protocol input must be Mtk. 协议输入必须为 Mtk。");
            }

            var serial = DeviceProtocolNodeSupport.Text(ReadInput("Serial"));
            var timeout = NodeDataUtil.ToDouble(ReadInput("Timeout"), 60);
            var (device, _) = await DeviceProtocolNodeSupport.WaitForDeviceAsync(this, context,
                DeviceProtocolKind.Mtk, serial, timeout, PollIntervalSeconds).ConfigureAwait(false);

            WriteOutput("Device", device);
            WriteOutput("Status", "connected");
            context.ReportProgress(1);
            context.Log.Write(LogLevel.Info, Title,
                $"MTK target {device.Serial} is ready.  联发科目标已就绪。");
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
            context.Log.Write(LogLevel.Error, Title, $"Wait for MTK device failed: {ex.Message}  等待联发科设备失败。");
            throw new InvalidOperationException($"Wait for MTK device failed: {ex.Message}  等待联发科设备失败。", ex);
        }
    }

    private static bool TryParseProtocol(string? text) =>
        (text ?? string.Empty).Trim().ToLowerInvariant() is "" or "mtk";
}
