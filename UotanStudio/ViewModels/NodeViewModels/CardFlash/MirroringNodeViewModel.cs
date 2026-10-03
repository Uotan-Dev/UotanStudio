using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Mirroring;
using UotanStudio.Services.Scripting;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Mirroring node view model that starts and stops a scrcpy presentation session through the shared
/// <see cref="IScriptToolGateway"/>, sharing the session by id between the Start and Stop nodes.
/// <para>投屏节点视图模型：通过共享的 <see cref="IScriptToolGateway"/> 启动与停止 scrcpy 呈现会话，
/// 在开始与停止节点之间按 id 共享会话。</para>
/// </summary>
public class MirroringNodeViewModel : FlowNodeViewModel
{
    private readonly IScriptToolGateway _gateway;

    /// <summary>
    /// <para>创建具有指定标题的投屏节点。</para>
    /// Creates a mirroring node with the specified title.
    /// </summary>
    /// <param name="title"><para>节点的显示标题。</para>The display title of the node.</param>
    /// <param name="gateway">
    /// <para>设备动作网关；为 <c>null</c> 时使用 <see cref="DeviceScriptToolGateway.Default"/>。</para>
    /// The device action gateway; uses <see cref="DeviceScriptToolGateway.Default"/> when <c>null</c>.
    /// </param>
    public MirroringNodeViewModel(string title, IScriptToolGateway? gateway = null) : base(title, NodeCategory.Flash)
    {
        _gateway = gateway ?? DeviceScriptToolGateway.Default;
    }

    /// <summary>
    /// <para>创建默认的开始投屏节点。</para>
    /// Creates a default start-mirroring node.
    /// </summary>
    public static MirroringNodeViewModel CreateStartNode()
    {
        var node = new MirroringNodeViewModel("Start Mirroring")
        {
            Location = new Point(100, 100),
            TypeKey = "Mirroring.Session.Start"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Session", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的停止投屏节点。</para>
    /// Creates a default stop-mirroring node.
    /// </summary>
    public static MirroringNodeViewModel CreateStopNode()
    {
        var node = new MirroringNodeViewModel("Stop Mirroring")
        {
            Location = new Point(100, 100),
            TypeKey = "Mirroring.Session.Stop"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Session", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <inheritdoc />
    public override Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (TypeKey)
            {
                case "Mirroring.Session.Start":
                    ExecuteStart(context);
                    break;
                case "Mirroring.Session.Stop":
                    ExecuteStop(context);
                    break;
                default:
                    return base.ExecuteAsync(context);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Log.Write(LogLevel.Error, Title, $"Mirroring node failed: {ex.Message}  投屏节点执行失败。");
            throw;
        }
        return Task.CompletedTask;
    }

    private void ExecuteStart(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No device connected. 未连接设备。");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }
        var session = _gateway.StartMirror(device);
        var id = MirroringSessionRegistry.Add(session);
        context.RegisterResource(new SessionDisposer(session));
        WriteOutput("Session", id);
        context.Log.Write(LogLevel.Info, Title, $"Mirroring started for {device.Serial}  投屏已开始。");
    }

    private void ExecuteStop(INodeExecutionContext context)
    {
        var id = Convert.ToString(ReadInput("Session"), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id) || !MirroringSessionRegistry.TryTake(id, out var session))
        {
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Mirroring_NoSession"));
        }
        session!.Dispose();
        WriteOutput("Status", "stopped");
    }

    private sealed class SessionDisposer(ManagedMirrorSession session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            session.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}