using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>扩展烧录操作节点视图模型，提供 bootloader 刷写、全部擦除、OTA 侧载与固件覆盖功能。</para>
/// Extended flash operation node view model, providing bootloader flashing, full erase, OTA sideload, and firmware overwrite capabilities.
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
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Any });
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
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Any });
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
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Any });
        node.Input.Add(new NodeConnectorViewModel { Title = "OTA Package", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的固件覆盖节点，使用外部镜像覆盖指定分区。</para>
    /// Creates a default firmware-overwrite node that overwrites a partition with an external image.
    /// </summary>
    public static ExtendedFlashNodeViewModel CreateOverwriteFirmwareNode()
    {
        var node = new ExtendedFlashNodeViewModel("Overwrite Firmware")
        {
            Location = new Point(100, 100),
            TypeKey = "Flash.OverwriteFirmware"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Any });
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>执行与节点标题对应的扩展烧录逻辑：Flash Bootloader / Erase All / OTA Sideload / Overwrite Firmware 校验输入并上报状态，
    /// 实时刷写依赖设备管理器会话，此处校验参数并记录任务。</para>
    /// Executes the extended flash logic matching the node title: Flash Bootloader, Erase All, OTA Sideload and Overwrite Firmware
    /// validate their inputs and report status; live flashing relies on device-manager sessions, so nodes validate and record the task.
    /// </summary>
    /// <param name="context">
    /// <para>执行上下文。</para>
    /// The execution context.
    /// </param>
    /// <returns>
    /// <para>表示异步执行的任务。</para>
    /// A task representing the asynchronous execution.
    /// </returns>
    public override Task ExecuteAsync(INodeExecutionContext context)
    {
        switch (Title)
        {
            case "Flash Bootloader":
                ExecuteFlashBootloader(context);
                break;
            case "Erase All":
                WriteOutput("Status", "recorded");
                context.Log.Write(LogLevel.Info, Title, "Erase-all task recorded.  已记录整机擦除任务。");
                break;
            case "OTA Sideload":
                ExecuteOtaSideload(context);
                break;
            case "Overwrite Firmware":
                ExecuteOverwrite(context);
                break;
            default:
                return base.ExecuteAsync(context);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ExecuteFlashBootloader(INodeExecutionContext context)
    {
        var bootloader = Text(ReadInput("Bootloader"));
        var ok = !string.IsNullOrWhiteSpace(bootloader) && File.Exists(bootloader);
        WriteOutput("Status", ok ? "ready" : "invalid");
        context.Log.Write(ok ? LogLevel.Info : LogLevel.Warn, Title,
            ok ? $"Bootloader ready: {bootloader}  引导加载器校验就绪。" : $"Bootloader path invalid: {bootloader ?? "<empty>"}  引导加载器路径无效。");
    }

    private void ExecuteOtaSideload(INodeExecutionContext context)
    {
        var package = Text(ReadInput("OTA Package"));
        var ok = !string.IsNullOrWhiteSpace(package) && File.Exists(package);
        WriteOutput("Status", ok ? "ready" : "invalid");
        context.Log.Write(ok ? LogLevel.Info : LogLevel.Warn, Title,
            ok ? $"Sideload package ready: {package}  OTA 包校验就绪。" : $"OTA package invalid: {package ?? "<empty>"}  OTA 包路径无效。");
    }

    private void ExecuteOverwrite(INodeExecutionContext context)
    {
        var firmware = Text(ReadInput("Firmware"));
        var ok = !string.IsNullOrWhiteSpace(firmware) && File.Exists(firmware);
        WriteOutput("Status", ok ? "ready" : "invalid");
        context.Log.Write(ok ? LogLevel.Info : LogLevel.Warn, Title,
            ok ? $"Overwrite firmware ready: {firmware}  覆盖固件校验就绪。" : $"Firmware path invalid: {firmware ?? "<empty>"}  固件路径无效。");
    }

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}