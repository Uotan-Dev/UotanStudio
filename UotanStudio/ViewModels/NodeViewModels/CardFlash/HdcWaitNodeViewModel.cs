using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// HDC wait node: polls the shared device monitor until one HDC target appears, optionally matching a serial,
/// and publishes that target for downstream HDC nodes.
/// <para>HDC 等待节点：轮询共享设备监视器，直到出现一个 HDC 目标（可选按序列号匹配），并把该目标发布给后续 HDC 节点。</para>
/// </summary>
public sealed class HdcWaitNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Hdc.Wait";
    private const int PollIntervalMs = 500;

    /// <summary>
    /// Creates an HDC wait node with the specified display title.
    /// <para>创建具有指定显示标题的 HDC 等待节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public HdcWaitNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the HDC wait node with its ports.
    /// <para>创建带端口的 HDC 等待节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static HdcWaitNodeViewModel CreateWaitNode()
    {
        var node = new HdcWaitNodeViewModel("Wait for HDC")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Timeout",
            PortType = NodePortType.Number,
            InitialValue = 60
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Ready", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Waits for a matching HDC target until the timeout elapses.
    /// <para>等待匹配的 HDC 目标，直到超时。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive or exceeds 86400 seconds. <para>超时非正或超过 86400 秒。</para></exception>
    /// <exception cref="TimeoutException">No matching target appeared before the deadline. <para>截止时间前未出现匹配目标。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, NodeTypeKey, StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var serial = HdcNodeSupport.Text(ReadInput("Serial"));
        TimeSpan timeout;
        try
        {
            timeout = HdcNodeSupport.ValidateWaitTimeout(NodeDataUtil.ToDouble(ReadInput("Timeout"), 60));
        }
        catch (ArgumentOutOfRangeException)
        {
            WriteOutput("Ready", false);
            WriteOutput("Status", "invalid-timeout");
            throw;
        }

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        context.Log.Write(LogLevel.Info, Title,
            $"Waiting for an HDC target{(string.IsNullOrWhiteSpace(serial) ? string.Empty : " " + serial)}...  正在等待 HDC 目标。");
        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var match = App.DeviceMonitor.Scan().FirstOrDefault(candidate =>
                candidate.Protocol == DeviceProtocolKind.Hdc
                && (string.IsNullOrWhiteSpace(serial)
                    || string.Equals(candidate.Serial, serial, StringComparison.Ordinal)));
            if (match is not null)
            {
                WriteOutput("Device", match);
                WriteOutput("Serial", match.Serial);
                WriteOutput("Ready", true);
                WriteOutput("Status", "connected");
                context.Log.Write(LogLevel.Info, Title, $"HDC target {match.Serial} is ready.  HDC 目标已就绪。");
                return;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                WriteOutput("Ready", false);
                WriteOutput("Status", "timeout");
                throw new TimeoutException(
                    $"No HDC target appeared within {timeout.TotalSeconds:0.###} seconds.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(PollIntervalMs, remaining)),
                context.CancellationToken).ConfigureAwait(false);
        }
    }
}