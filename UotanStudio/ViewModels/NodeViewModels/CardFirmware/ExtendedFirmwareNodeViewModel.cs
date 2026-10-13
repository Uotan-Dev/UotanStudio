using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Extended firmware operation node view model for OTA delta generation and loading unsupported legacy build nodes.
/// <para>扩展固件操作节点视图模型，生成 OTA 差分文件并保留不支持的旧构建节点身份以供加载。</para>
/// </summary>
public class ExtendedFirmwareNodeViewModel : FlowNodeViewModel
{
    public override bool IsUnavailable => TypeKey is "Build.Signature.Merge" or "Build.Slots.Merge";

    /// <summary>
    /// Creates an extended firmware operation node with the specified title.
    /// <para>创建具有指定标题的扩展固件操作节点。</para>
    /// </summary>
    /// <param name="title">
    /// The display title of the node.
    /// <para>节点的显示标题。</para>
    /// </param>
    public ExtendedFirmwareNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// Creates a default OTA delta package generation node.
    /// <para>创建默认的 OTA 差分包生成节点。</para>
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
        node.Output.Add(new NodeConnectorViewModel { Title = "Artifact", PortType = NodePortType.Artifact });
        return node;
    }

    /// <summary>
    /// Creates the unsupported signature-append node for loading existing workflows.
    /// <para>创建仅供旧工作流恢复的签名拼接节点。</para>
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
    /// Creates the unsupported slot-concatenation node for loading existing workflows.
    /// <para>创建仅供旧工作流恢复的槽位拼接节点。</para>
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
    /// Generates an OTA delta by type key and explicitly rejects unsupported signature and slot concatenation types.
    /// <para>按节点类型键生成 OTA 差分；旧签名与槽位拼接类型显式拒绝执行。</para>
    /// </summary>
    /// <param name="context">
    /// The execution context.
    /// <para>执行上下文。</para>
    /// </param>
    /// <returns>
    /// A task representing the asynchronous execution.
    /// <para>表示异步执行的任务。</para>
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
                    throw new NotSupportedException(
                        "Appending signature bytes cannot produce a valid signed firmware image. Use a format-specific signing operation.");
                case "Build.Slots.Merge":
                    throw new NotSupportedException(
                        "Concatenating A/B images cannot produce a valid dual-slot image. Use a format-specific build operation.");
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
        var result = await UotanStudio.Firmware.OtaDelta.Diff.DeltaEngine.GenerateDeltaAsync(
            source, target, output, context.CancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            WriteOutput("Delta", new FileArtifact(output));
            WriteOutput("Artifact", new FileArtifact(output));
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