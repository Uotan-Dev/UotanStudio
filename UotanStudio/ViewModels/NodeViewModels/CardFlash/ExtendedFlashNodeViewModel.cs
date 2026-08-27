using UotanStudio.Services.Devices;
using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services.Flash;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>扩展烧录操作节点视图模型，提供 bootloader 刷写、全部擦除、OTA 侧载与固件覆盖功能，
/// 均接入真实后端服务。</para>
/// Extended flash operation node view model, providing bootloader flashing, full erase, OTA sideload,
/// and firmware overwrite capabilities, all wired to the real backend services.
/// </summary>
public class ExtendedFlashNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的扩展烧录操作节点。</para>
    /// Creates an extended flash operation node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public ExtendedFlashNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// <para>创建默认的 bootloader 刷写节点。</para>
    /// Creates a default flash-bootloader node.
    /// </summary>
    public static ExtendedFlashNodeViewModel CreateFlashBootloaderNode()
    {
        var node = new ExtendedFlashNodeViewModel("Flash Bootloader")
        {
            Location = new Point(100, 100),
            TypeKey = "Flash.Bootloader"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Bootloader", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的全部擦除节点，擦除设备上的所有分区。</para>
    /// Creates a default erase-all node that wipes every partition on the device.
    /// </summary>
    public static ExtendedFlashNodeViewModel CreateEraseAllNode()
    {
        var node = new ExtendedFlashNodeViewModel("Erase All")
        {
            Location = new Point(100, 100),
            TypeKey = "Flash.EraseAll"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的 OTA 侧载节点，通过 sideload 推送 OTA 包。</para>
    /// Creates a default OTA sideload node that pushes an OTA package via sideload.
    /// </summary>
    public static ExtendedFlashNodeViewModel CreateOtaSideloadNode()
    {
        var node = new ExtendedFlashNodeViewModel("OTA Sideload")
        {
            Location = new Point(100, 100),
            TypeKey = "Flash.OtaSideload"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "OTA Package", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的固件覆盖节点，使用外部镜像覆盖指定分区（默认 <c>super</c>，可覆盖）。</para>
    /// Creates a default firmware-overwrite node that overwrites a partition with an external image
    /// (defaults to <c>super</c>, overridable).
    /// </summary>
    public static ExtendedFlashNodeViewModel CreateOverwriteFirmwareNode()
    {
        var node = new ExtendedFlashNodeViewModel("Overwrite Firmware")
        {
            Location = new Point(100, 100),
            TypeKey = "Flash.OverwriteFirmware"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partition", PortType = NodePortType.Text, InitialValue = "super" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的扩展烧录逻辑：Flash.Bootloader 刷写 bootloader 分区、
    /// Flash.EraseAll 调 <see cref="FlashService.EraseAllAsync"/>、Flash.OtaSideload 调
    /// <see cref="FlashService.FlashSideloadAsync"/>、Flash.OverwriteFirmware 覆盖指定分区。
    /// 设备缺失时告警并短路。</para>
    /// Dispatches extended flash logic by the node type key (TypeKey): Flash.Bootloader flashes the
    /// bootloader partition, Flash.EraseAll calls <see cref="FlashService.EraseAllAsync"/>,
    /// Flash.OtaSideload calls <see cref="FlashService.FlashSideloadAsync"/>, and
    /// Flash.OverwriteFirmware overwrites the selected partition. A missing device logs a warning and
    /// short-circuits.
    /// </summary>
    /// <param name="context">
    /// <para>执行上下文。</para>
    /// The execution context.
    /// </param>
    /// <returns>
    /// <para>表示异步执行的任务。</para>
    /// A task representing the asynchronous execution.
    /// </returns>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (TypeKey)
            {
                case "Flash.Bootloader":
                    await ExecuteFlashBootloaderAsync(context).ConfigureAwait(false);
                    break;
                case "Flash.EraseAll":
                    await ExecuteEraseAllAsync(context).ConfigureAwait(false);
                    break;
                case "Flash.OtaSideload":
                    await ExecuteOtaSideloadAsync(context).ConfigureAwait(false);
                    break;
                case "Flash.OverwriteFirmware":
                    await ExecuteOverwriteAsync(context).ConfigureAwait(false);
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
            context.Log.Write(LogLevel.Error, Title, $"Extended flash node failed: {ex.Message}  扩展烧录节点执行失败。");
        }
    }

    private async Task ExecuteFlashBootloaderAsync(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No device connected. 未连接设备。");
            WriteOutput("Status", "no-device");
            return;
        }

        var bootloader = Text(ReadInput("Bootloader"));
        if (string.IsNullOrWhiteSpace(bootloader) || !File.Exists(bootloader))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Bootloader path invalid: {bootloader ?? "<empty>"}  引导加载器路径无效。");
            WriteOutput("Status", "invalid");
            return;
        }

        var progress = new Progress<FlashProgress>(p => context.ReportProgress(p.Percent / 100.0));
        var result = await FlashService.FlashImageAsync(
            new DeviceInfo(device), "bootloader", bootloader, progress, context.CancellationToken).ConfigureAwait(false);

        WriteOutput("Status", result.Summary);
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title,
            result.Success
                ? $"{result.Summary}  引导加载器刷写完成。"
                : $"Bootloader flash failed: {result.Summary}  引导加载器刷写失败。");
    }

    private async Task ExecuteEraseAllAsync(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No device connected. 未连接设备。");
            WriteOutput("Status", "no-device");
            return;
        }

        var progress = new Progress<FlashProgress>(p => context.ReportProgress(p.Percent / 100.0));
        var result = await FlashService.EraseAllAsync(
            new DeviceInfo(device), progress, context.CancellationToken).ConfigureAwait(false);

        WriteOutput("Status", result.Summary);
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title,
            result.Success
                ? $"{result.Summary}  整机擦除完成。"
                : $"Erase-all failed: {result.Summary}  整机擦除失败。");
    }

    private async Task ExecuteOtaSideloadAsync(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No device connected. 未连接设备。");
            WriteOutput("Status", "no-device");
            return;
        }

        var package = Text(ReadInput("OTA Package"));
        if (string.IsNullOrWhiteSpace(package) || !File.Exists(package))
        {
            context.Log.Write(LogLevel.Warn, Title, $"OTA package invalid: {package ?? "<empty>"}  OTA 包路径无效。");
            WriteOutput("Status", "invalid");
            return;
        }

        var progress = new Progress<FlashProgress>(p => context.ReportProgress(p.Percent / 100.0));
        var result = await FlashService.FlashSideloadAsync(
            new DeviceInfo(device), package, progress, context.CancellationToken).ConfigureAwait(false);

        WriteOutput("Status", result.Summary);
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title,
            result.Success
                ? $"{result.Summary}  OTA 侧载完成。"
                : $"OTA sideload failed: {result.Summary}  OTA 侧载失败。");
    }

    private async Task ExecuteOverwriteAsync(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"));
        if (device is null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No device connected. 未连接设备。");
            WriteOutput("Status", "no-device");
            return;
        }

        var partition = Text(ReadInput("Partition"));
        if (string.IsNullOrWhiteSpace(partition))
        {
            partition = "super";
        }

        var firmware = Text(ReadInput("Firmware"));
        if (string.IsNullOrWhiteSpace(firmware) || !File.Exists(firmware))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Firmware path invalid: {firmware ?? "<empty>"}  固件路径无效。");
            WriteOutput("Status", "invalid");
            return;
        }

        var progress = new Progress<FlashProgress>(p => context.ReportProgress(p.Percent / 100.0));
        var result = await FlashService.FlashImageAsync(
            new DeviceInfo(device), partition, firmware, progress, context.CancellationToken).ConfigureAwait(false);

        WriteOutput("Status", result.Summary);
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title,
            result.Success
                ? $"{result.Summary}  固件覆盖完成。"
                : $"Firmware overwrite failed: {result.Summary}  固件覆盖失败。");
    }

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}