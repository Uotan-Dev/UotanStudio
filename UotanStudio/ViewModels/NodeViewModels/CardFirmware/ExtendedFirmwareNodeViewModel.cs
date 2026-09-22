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
            TypeKey = "Build.Ota.CreateDelta"
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
            TypeKey = "Build.Signature.Merge"
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
            TypeKey = "Build.Slots.Merge"
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
    public static FlashNodeViewModel CreateFlashBootNode() => FlashNodeViewModel.CreateFlashPreset("boot");

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的扩展固件逻辑：Build.Ota.CreateDelta 调用差分引擎生成差分包、
    /// Build.Signature.Merge 把签名追加进镜像、Build.Slots.Merge 拼接 A/B 槽位、Device.Partition.Flash
    /// 调用 <see cref="FlashService.FlashImageAsync"/> 把引导加载程序刷写到设备。</para>
    /// Dispatches extended firmware logic by the node type key (TypeKey): Build.Ota.CreateDelta generates a delta
    /// package via the delta engine, Build.Signature.Merge appends a signature into the image,
    /// Build.Slots.Merge concatenates the A/B slots, and Device.Partition.Flash flashes the bootloader
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
                case "Build.Ota.CreateDelta":
                    await ExecuteOtaDeltaAsync(context).ConfigureAwait(false);
                    break;
                case "Build.Signature.Merge":
                    ExecuteMergeSignature(context);
                    break;
                case "Build.Slots.Merge":
                    ExecuteMergeDualSlot(context);
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
            throw;
        }
    }

    private void ExecuteMergeSignature(INodeExecutionContext context)
    {
        var image = NodeDataUtil.ToBytes(ReadInput("Image"));
        var signature = NodeDataUtil.ToBytes(ReadInput("Signature"));
        if (image == null || signature == null)
        {
            throw new InvalidOperationException("Missing Image or Signature input.  缺少镜像或签名。");
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
            throw new InvalidOperationException($"Source image path invalid or missing: {source ?? "<empty>"}  源镜像路径无效或缺失。");
        }

        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            throw new InvalidOperationException($"Target image path invalid or missing: {target ?? "<empty>"}  目标镜像路径无效或缺失。");
        }

        var targetFull = Path.GetFullPath(target);
        var output = Path.Combine(
            Path.GetDirectoryName(targetFull) ?? ".",
            Path.GetFileNameWithoutExtension(targetFull) + ".delta");

        context.Log.Write(LogLevel.Info, Title, $"Generating delta {source} -> {target}...  正在生成差分包...");
        var result = await UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Engines.DeltaEngine.GenerateDeltaAsync(
            source, target, output, context.CancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            WriteOutput("Delta", output);
            context.Log.Write(LogLevel.Info, Title,
                $"Delta generated: {output} ({result.SourceLength} -> {result.DiffLength} bytes).  差分文件生成完成。");
        }
        else
        {
            throw new InvalidOperationException($"Delta generation failed: {result.ErrorMessage}  差分生成失败。");
        }
    }


    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}