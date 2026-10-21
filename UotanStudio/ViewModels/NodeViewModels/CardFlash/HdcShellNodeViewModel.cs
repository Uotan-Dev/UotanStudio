using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// HDC shell node: runs one command through the host's existing HDC shell capability and publishes its output.
/// <para>HDC shell 节点：通过宿主现有的 HDC shell 能力执行一条命令并发布输出。</para>
/// </summary>
public sealed class HdcShellNodeViewModel : FlowNodeViewModel
{
    private const string NodeTypeKey = "Device.Hdc.Shell";

    /// <summary>
    /// Creates an HDC shell node with the specified display title.
    /// <para>创建具有指定显示标题的 HDC shell 节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public HdcShellNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the HDC shell node with its ports.
    /// <para>创建带端口的 HDC shell 节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static HdcShellNodeViewModel CreateShellNode()
    {
        var node = new HdcShellNodeViewModel("HDC Shell")
        {
            Location = new Point(100, 100),
            TypeKey = NodeTypeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Serial", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Command", PortType = NodePortType.Text, IsRequired = true });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Executes the shell command on the selected HDC target.
    /// <para>在选定的 HDC 目标上执行 shell 命令。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The target or command input is missing. <para>目标或命令输入缺失。</para></exception>
    /// <exception cref="NotSupportedException">The Device input is not an HDC target. <para>Device 输入不是 HDC 目标。</para></exception>
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

            var command = HdcNodeSupport.Text(ReadInput("Command"));
            if (string.IsNullOrWhiteSpace(command))
            {
                WriteOutput("Status", "missing-command");
                throw new InvalidOperationException("The Command input is required before running an HDC shell command.");
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            var output = await HdcDeviceService.FromSettings()
                .ShellAsync(device!, command, context.CancellationToken).ConfigureAwait(false);
            WriteOutput("Output", output);
            WriteOutput("Result", true);
            WriteOutput("Status", "completed");
            context.Log.Write(LogLevel.Info, Title, $"HDC shell completed on {device!.Serial}.  HDC shell 已完成。");
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