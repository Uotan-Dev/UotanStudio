using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Device property query node view model: queries a device property
/// (fastboot getvar / ADB getprop) via <see cref="FlashService.GetDevicePropertyAsync"/> and
/// writes the value to the Value port.
/// <para>设备属性查询节点视图模型：通过 <see cref="FlashService.GetDevicePropertyAsync"/>
/// 查询设备属性（fastboot getvar / ADB getprop）并把值写入 Value 端口。</para>
/// </summary>
public class DevicePropertyNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a device property query node with the specified title.
    /// <para>创建具有指定标题的设备属性查询节点。</para>
    /// </summary>
    /// <param name="title">
    /// The display title of the node.
    /// <para>节点的显示标题。</para>
    /// </param>
    public DevicePropertyNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates a default device property query node.
    /// <para>创建默认的设备属性查询节点。</para>
    /// </summary>
    public static DevicePropertyNodeViewModel CreateDevicePropertyNode()
    {
        var node = new DevicePropertyNodeViewModel("Get Property")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Property.Read"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Name", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Value", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>Dispatches the property query logic by the node type key (TypeKey): Device.Property.Read queries the device property and outputs the value.
    /// <para>按节点类型键（TypeKey）分发属性查询逻辑：Device.Property.Read 查询设备属性并输出值。</para></summary>
    /// <param name="context">The execution context.<para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution.<para>表示异步执行的任务。</para></returns>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (TypeKey)
            {
                case "Device.Property.Read":
                    await ExecuteGetPropAsync(context).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            context.Log.Write(LogLevel.Error, Title, $"Get property failed: {ex.Message}  属性查询失败。");
            throw;
        }
    }

    private async Task ExecuteGetPropAsync(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        var name = Text(ReadInput("Name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        var result = await FlashService.Default.GetDevicePropertyAsync(
            new DeviceInfo(device), name, context.CancellationToken).ConfigureAwait(false);
        var value = result.Success ? ExtractValue(result.Summary, name) : string.Empty;
        WriteOutput("Value", value);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Summary);
        }
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title,
            result.Success
                ? $"{result.Summary}  属性查询完成。"
                : $"Get property failed: {result.Summary}  属性查询失败。");
    }

    /// <summary>
    /// Extracts the value portion from a <c>name=value</c> summary (after the first '='; returns the
    /// whole text when no '=' is present).
    /// <para>从 <c>name=value</c> 摘要中提取值部分（等号之后；无等号时原样返回）。</para>
    /// </summary>
    private static string ExtractValue(string summary, string name)
    {
        if (string.IsNullOrEmpty(summary))
        {
            return string.Empty;
        }

        var eq = summary.IndexOf('=');
        return eq >= 0 ? summary[(eq + 1)..] : name;
    }

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}