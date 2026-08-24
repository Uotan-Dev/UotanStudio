using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;

namespace UotanStudio.Areas.NodeEditor.ViewModels;

/// <summary>
/// <para>固件操作节点视图模型，提供固件解包、打包、签名验证和分区提取功能。</para>
/// Firmware operation node view model, providing firmware unpack, pack, signature verification, and partition extraction capabilities.
/// <para>执行逻辑通过 FirmwareService 接入 FirmwareKit.CPIO / Payload / AVB / Oppo 真实后端。</para>
/// Execution logic connects to real FirmwareKit.CPIO / Payload / AVB / Oppo backends via FirmwareService.
/// </summary>
/// <remarks>
/// <para>创建具有指定固定键的固件操作节点，显示标题按键本地化。</para>
/// Creates a firmware operation node with the specified fixed key; the display title is localized by the key.
/// </remarks>
/// <param name="key">
/// <para>节点的固定键（本地化资源键）。</para>
/// The fixed key of the node (localization resource key).
/// </param>
public class FirmwareNodeViewModel(string key) : FlowNodeViewModel(LanguageService.Instance.GetString(key), NodeCategory.Firmware, key)
{
    /// <summary>
    /// <para>执行该固件节点的真实操作，依据节点固定键分派到 FirmwareService 对应方法。</para>
    /// Executes the real operation of this firmware node, dispatching to the corresponding FirmwareService method based on the node key.
    /// </summary>
    /// <param name="firmwarePath">固件文件路径（来自输入端口绑定）。</param>
    /// <param name="outputDirectory">输出目录（来自输入端口绑定）。</param>
    /// <param name="partitionName">分区名（可选，仅 Extract 节点使用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果摘要文本。</returns>
    public async Task<string> ExecuteAsync(string firmwarePath, string outputDirectory, string? partitionName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(firmwarePath))
        {
            return "Error: firmware path is empty.";
        }

        var svc = FirmwareService.Instance;
        var result = Key switch
        {
            NodeKeys.FirmwareUnpack => await svc.UnpackCpioAsync(firmwarePath, string.IsNullOrEmpty(outputDirectory) ? "unpacked" : outputDirectory, true),
            NodeKeys.FirmwarePack => await svc.PackCpioAsync(firmwarePath, outputDirectory, cancellationToken),
            NodeKeys.FirmwareVerify => await svc.VerifyAvbAsync(firmwarePath, cancellationToken),
            NodeKeys.FirmwareExtract => await svc.ExtractPayloadAsync(firmwarePath, string.IsNullOrEmpty(outputDirectory) ? "extracted" : outputDirectory, partitionName),
            _ => new FirmwareOpResult { Success = false, Summary = $"Unknown firmware node: {Title}" }
        };
        return result.Success ? $"[{Title}] OK: {result.Summary}" : $"[{Title}] FAIL: {result.Summary}";
    }

    /// <summary>
    /// <para>创建默认的固件解包节点。</para>
    /// Creates a default firmware unpack node.
    /// </summary>
    /// <returns>配置好输入输出端口的固件解包节点。</returns>
    public static FirmwareNodeViewModel CreateUnpackNode()
    {
        var node = new FirmwareNodeViewModel(NodeKeys.FirmwareUnpack)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware Path" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output Dir" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Partitions" });
        return node;
    }

    /// <summary>
    /// <para>创建默认的固件打包节点。</para>
    /// Creates a default firmware pack node.
    /// </summary>
    public static FirmwareNodeViewModel CreatePackNode()
    {
        var node = new FirmwareNodeViewModel(NodeKeys.FirmwarePack)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input Dir" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Config" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Firmware" });
        return node;
    }

    /// <summary>
    /// <para>创建默认的签名验证节点。</para>
    /// Creates a default signature verification node.
    /// </summary>
    public static FirmwareNodeViewModel CreateVerifyNode()
    {
        var node = new FirmwareNodeViewModel(NodeKeys.FirmwareVerify)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Valid" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Report" });
        return node;
    }

    /// <summary>
    /// <para>创建默认的分区提取节点。</para>
    /// Creates a default partition extraction node.
    /// </summary>
    public static FirmwareNodeViewModel CreateExtractNode()
    {
        var node = new FirmwareNodeViewModel(NodeKeys.FirmwareExtract)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partition Name" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Image" });
        return node;
    }
}