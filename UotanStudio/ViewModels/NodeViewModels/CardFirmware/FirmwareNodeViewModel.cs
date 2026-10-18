using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Firmware operation node view model, providing firmware unpack, pack, signature verification, and partition extraction capabilities.
/// <para>固件操作节点视图模型，提供固件解包、打包、签名验证和分区提取功能。</para>
/// </summary>
public partial class FirmwareNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a firmware operation node with the specified title.
    /// <para>创建具有指定标题的固件操作节点。</para>
    /// </summary>
    /// <param name="title">
    /// The display title of the node.
    /// <para>节点的显示标题。</para>
    /// </param>
    public FirmwareNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>Dispatches firmware logic by the node type key (TypeKey): Firmware.Container.Unpack unpacks (boot/oppo/dt), Firmware.Container.Pack packs (boot/cpio/dtbo), Validation.Signature.Verify verifies an AVB signature, and Firmware.Container.Extract extracts a partition from a payload.
    /// <para>按节点类型键（TypeKey）分发对应的固件逻辑：Firmware.Container.Unpack 解包（boot/oppo/dt）、Firmware.Container.Pack 打包（boot/cpio/dtbo）、 Validation.Signature.Verify 校验 AVB 签名、Firmware.Container.Extract 从 payload 提取分区。</para></summary>
    /// <param name="context">The execution context.<para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution.<para>表示异步执行的任务。</para></returns>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            switch (TypeKey)
            {
                case "Firmware.Container.Unpack":
                    await ExecuteUnpackAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.Container.Pack":
                    await ExecutePackAsync(context).ConfigureAwait(false);
                    break;
                case "Boot.CommandLine.Edit":
                    await ExecuteCmdlineEditAsync(context).ConfigureAwait(false);
                    break;
                case "Validation.Signature.Verify":
                    await ExecuteVerifyAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.Container.Extract":
                    await ExecuteExtractAsync(context).ConfigureAwait(false);
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
            context.Log.Write(LogLevel.Error, Title, $"Firmware node failed: {ex.Message}  固件节点执行失败。");
            throw;
        }
    }

    private static string? PathText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

    private static string DeriveOutputDir(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, string.IsNullOrEmpty(name) ? "out" : name + "_out");
    }
}