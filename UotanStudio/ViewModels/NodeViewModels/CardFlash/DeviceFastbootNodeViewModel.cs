using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Fastboot variable query node: reads one variable through the existing flash service and publishes its value.
/// The node rejects a non-Fastboot device before querying and never opens the USB handle itself.
/// <para>Fastboot 变量查询节点：经现有刷写服务读取一个变量并发布其值。节点在查询前拒绝非 Fastboot 设备，
/// 自身不打开 USB 句柄。</para>
/// </summary>
public sealed class DeviceFastbootNodeViewModel : FlowNodeViewModel
{
    private DeviceFastbootNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Catalog-independent specification for the Fastboot variable node.
    /// <para>Fastboot 变量节点的目录外规格。</para>
    /// </summary>
    public static IReadOnlyList<DeviceProtocolNodeSpec> Specs { get; } =
    [
        new("Device.Fastboot.GetVariable", DeviceProtocolKind.Fastboot,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            CreateGetVariableNode)
    ];

    /// <summary>
    /// Creates the Fastboot variable query node.
    /// <para>创建 Fastboot 变量查询节点。</para>
    /// </summary>
    /// <returns>The configured node instance.<para>配置完成的节点实例。</para></returns>
    public static DeviceFastbootNodeViewModel CreateGetVariableNode()
    {
        var node = new DeviceFastbootNodeViewModel("Fastboot Get Variable")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Fastboot.GetVariable"
        };
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Device",
            PortType = NodePortType.Device,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Name",
            PortType = NodePortType.Text,
            IsRequired = true
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Value", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <inheritdoc />
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(TypeKey, "Device.Fastboot.GetVariable", StringComparison.Ordinal))
            {
                await base.ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            var device = DeviceProtocolNodeSupport.RequireDevice(this, context, DeviceProtocolKind.Fastboot,
                "Fastboot");
            var name = DeviceProtocolNodeSupport.RequireText(this, "Name", "missing-name");

            context.ReportProgress(0);
            var result = await FlashService.Default.GetDevicePropertyAsync(new DeviceInfo(device), name,
                context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();

            if (!result.Success)
            {
                WriteOutput("Status", result.Summary);
                context.Log.Write(LogLevel.Error, Title, $"Get variable failed: {result.Summary}  变量查询失败。");
                throw new InvalidOperationException(result.Summary);
            }

            WriteOutput("Value", ExtractValue(result.Summary));
            WriteOutput("Status", "ok");
            context.ReportProgress(1);
            context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  Fastboot 变量查询完成。");
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
            context.Log.Write(LogLevel.Error, Title, $"Get variable failed: {ex.Message}  变量查询失败。");
            throw new InvalidOperationException($"Get variable failed: {ex.Message}  变量查询失败。", ex);
        }
    }

    private static string ExtractValue(string summary)
    {
        if (string.IsNullOrEmpty(summary))
        {
            return string.Empty;
        }

        var separator = summary.IndexOf('=');
        return separator >= 0 ? summary[(separator + 1)..] : summary;
    }
}