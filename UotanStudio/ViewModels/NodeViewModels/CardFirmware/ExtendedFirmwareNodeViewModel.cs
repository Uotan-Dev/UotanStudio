using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>扩展固件操作节点视图模型，提供 OTA 差分生成、签名合并、双卡合并与 spl/uboot 刷写功能。</para>
/// Extended firmware operation node view model, providing OTA delta generation, signature merging, dual-slot merging, and spl/uboot flashing capabilities.
/// </summary>
public class ExtendedFirmwareNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的扩展固件操作节点。</para>
    /// Creates an extended firmware operation node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public ExtendedFirmwareNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// <para>创建默认的 OTA 差分包生成节点。</para>
    /// Creates a default OTA delta package generation node.
    /// </summary>
    public static ExtendedFirmwareNodeViewModel CreateOtaDeltaNode()
    {
        var node = new ExtendedFirmwareNodeViewModel("OTA Delta")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.OtaDelta"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Source", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Target", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Delta", PortType = NodePortType.Data });
        return node;
    }

    /// <summary>
    /// <para>创建默认的签名合并节点，将签名嵌入固件镜像。</para>
    /// Creates a default merge-signature node that embeds a signature into a firmware image.
    /// </summary>
    public static ExtendedFirmwareNodeViewModel CreateMergeSignatureNode()
    {
        var node = new ExtendedFirmwareNodeViewModel("Merge Signature")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.MergeSignature"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Image", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel { Title = "Signature", PortType = NodePortType.Data });
        node.Output.Add(new NodeConnectorViewModel { Title = "Signed", PortType = NodePortType.Data });
        return node;
    }

    /// <summary>
    /// <para>创建默认的双卡合并节点，将 A/B 槽位合并为单一镜像。</para>
    /// Creates a default dual-slot merge node that combines A/B slots into a single image.
    /// </summary>
    public static ExtendedFirmwareNodeViewModel CreateMergeDualSlotNode()
    {
        var node = new ExtendedFirmwareNodeViewModel("Merge Dual Slot")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.MergeDualSlot"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Slot A", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel { Title = "Slot B", PortType = NodePortType.Data });
        node.Output.Add(new NodeConnectorViewModel { Title = "Merged", PortType = NodePortType.Data });
        return node;
    }

    /// <summary>
    /// <para>创建默认的 spl/uboot 刷写节点，通过 Device 与 Bootloader 输入执行真实刷写。</para>
    /// Creates a default spl/uboot flashing node that performs the real flash via the Device and Bootloader inputs.
    /// </summary>
    public static ExtendedFirmwareNodeViewModel CreateFlashBootNode()
    {
        var node = new ExtendedFirmwareNodeViewModel("Flash Bootloader")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.FlashBoot"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device });
        node.Input.Add(new NodeConnectorViewModel { Title = "Bootloader", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的扩展固件逻辑：Firmware.OtaDelta 调用差分引擎生成差分包、
    /// Firmware.MergeSignature 把签名追加进镜像、Firmware.MergeDualSlot 拼接 A/B 槽位、Firmware.FlashBoot
    /// 调用 <see cref="FlashService.FlashImageAsync"/> 把引导加载程序刷写到设备。</para>
    /// Dispatches extended firmware logic by the node type key (TypeKey): Firmware.OtaDelta generates a delta
    /// package via the delta engine, Firmware.MergeSignature appends a signature into the image,
    /// Firmware.MergeDualSlot concatenates the A/B slots, and Firmware.FlashBoot flashes the bootloader
    /// image to the device via <see cref="FlashService.FlashImageAsync"/>.
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
                case "Firmware.OtaDelta":
                    await ExecuteOtaDeltaAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.MergeSignature":
                    ExecuteMergeSignature(context);
                    break;
                case "Firmware.MergeDualSlot":
                    ExecuteMergeDualSlot(context);
                    break;
                case "Firmware.FlashBoot":
                    await ExecuteFlashBootAsync(context).ConfigureAwait(false);
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
            context.Log.Write(LogLevel.Error, Title, $"Extended firmware node failed: {ex.Message}  扩展固件节点执行失败。");
        }
    }

    private void ExecuteMergeSignature(INodeExecutionContext context)
    {
        var image = NodeDataUtil.ToBytes(ReadInput("Image"));
        var signature = NodeDataUtil.ToBytes(ReadInput("Signature"));
        if (image == null || signature == null)
        {
            context.Log.Write(LogLevel.Warn, Title, "Missing Image or Signature input.  缺少镜像或签名。");
            return;
        }

        var signed = new byte[image.Length + signature.Length];
        image.CopyTo(signed, 0);
        signature.CopyTo(signed, image.Length);
        WriteOutput("Signed", signed);
        context.Log.Write(LogLevel.Info, Title, $"Appended {signature.Length}-byte signature to {image.Length}-byte image.  已合并签名。");
    }

    private void ExecuteMergeDualSlot(INodeExecutionContext context)
    {
        var a = NodeDataUtil.ToBytes(ReadInput("Slot A")) ?? [];
        var b = NodeDataUtil.ToBytes(ReadInput("Slot B")) ?? [];
        var merged = new byte[a.Length + b.Length];
        a.CopyTo(merged, 0);
        b.CopyTo(merged, a.Length);
        WriteOutput("Merged", merged);
        context.Log.Write(LogLevel.Info, Title, $"Merged slot A ({a.Length}B) + slot B ({b.Length}B).  已合并双槽。");
    }

    private async Task ExecuteOtaDeltaAsync(INodeExecutionContext context)
    {
        var source = Text(ReadInput("Source"));
        var target = Text(ReadInput("Target"));
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Source image path invalid or missing: {source ?? "<empty>"}  源镜像路径无效或缺失。");
            return;
        }

        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Target image path invalid or missing: {target ?? "<empty>"}  目标镜像路径无效或缺失。");
            return;
        }

        var targetFull = Path.GetFullPath(target);
        var output = Path.Combine(
            Path.GetDirectoryName(targetFull) ?? ".",
            Path.GetFileNameWithoutExtension(targetFull) + ".delta");

        context.Log.Write(LogLevel.Info, Title, $"Generating delta {source} -> {target}...  正在生成差分包...");
        var result = await UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.DeltaEngine.GenerateDeltaAsync(
            source, target, output, context.CancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            WriteOutput("Delta", output);
            context.Log.Write(LogLevel.Info, Title,
                $"Delta generated: {output} ({result.SourceLength} -> {result.DiffLength} bytes).  差分文件生成完成。");
        }
        else
        {
            context.Log.Write(LogLevel.Error, Title,
                $"Delta generation failed: {result.ErrorMessage}  差分生成失败。");
        }
    }

    private async Task ExecuteFlashBootAsync(INodeExecutionContext context)
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
            context.Log.Write(LogLevel.Warn, Title, $"Bootloader image path invalid or missing: {bootloader ?? "<empty>"}  引导镜像路径无效或缺失。");
            WriteOutput("Status", "invalid-image");
            return;
        }

        var progress = new Progress<FlashProgress>(p => context.ReportProgress(p.Percent / 100.0));
        var result = await FlashService.Default.FlashImageAsync(
            new DeviceInfo(device), "boot", bootloader, progress, context.CancellationToken).ConfigureAwait(false);

        WriteOutput("Status", result.Summary);
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title,
            result.Success
                ? $"{result.Summary}  引导加载程序刷写完成。"
                : $"Bootloader flash failed: {result.Summary}  引导加载程序刷写失败。");
    }

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}