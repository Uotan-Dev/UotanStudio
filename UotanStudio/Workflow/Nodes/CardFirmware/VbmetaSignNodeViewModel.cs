using System.Globalization;
using Avalonia;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Services;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// AVB vbmeta signing node. Re-signs an existing AVB0 vbmeta with an RSA private key, which is the step a
/// repacked boot or recovery image needs before it can pass verification on a device.
/// <para>AVB vbmeta 签名节点。用 RSA 私钥对既有 AVB0 vbmeta 重新签名，是重打包后的 boot 或 recovery 镜像在设备上
/// 通过校验前的必要一步。</para>
/// </summary>
public sealed class VbmetaSignNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a vbmeta signing node with the specified display title.
    /// <para>创建具有指定显示标题的 vbmeta 签名节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public VbmetaSignNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// Creates the vbmeta signing node.
    /// <para>创建 vbmeta 签名节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static VbmetaSignNodeViewModel CreateSignNode()
    {
        var node = new VbmetaSignNodeViewModel("Sign VBMeta")
        {
            Location = new Point(100, 100),
            TypeKey = "Validation.Vbmeta.Sign"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Vbmeta", PortType = NodePortType.Path, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Private Key",
            PortType = NodePortType.Path,
            IsRequired = true,
            IsSensitive = true
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Path", PortType = NodePortType.Path, IsRequired = true });
        node.Output.Add(new NodeConnectorViewModel { Title = "Signed", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Algorithm", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Signs the vbmeta input and writes the result to the output path.
    /// <para>对 vbmeta 输入签名并把结果写到输出路径。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">An input is missing or the key cannot be loaded. <para>输入缺失或密钥无法加载。</para></exception>
    /// <exception cref="InvalidDataException">The signer rejected the input. <para>签名器拒绝了输入。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, "Validation.Vbmeta.Sign", StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var vbmetaPath = Text(ReadInput("Vbmeta"));
        var keyPath = Text(ReadInput("Private Key"));
        var outputPath = Text(ReadInput("Output Path"));
        if (string.IsNullOrWhiteSpace(vbmetaPath))
        {
            throw new InvalidOperationException("The Vbmeta input is required before signing.");
        }
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            throw new InvalidOperationException("The Private Key input is required before signing.");
        }
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException("The Output Path input is required before signing.");
        }

        using var key = AvbSigner.LoadRsaPrivateKey(keyPath)
            ?? throw new InvalidOperationException(
                $"The private key at '{keyPath}' could not be loaded as an RSA private key.  私钥无法解析为 RSA 私钥。");

        var written = await Task.Run(() => AvbSigner.SignVbmetaAsync(vbmetaPath, key, outputPath,
            Array.Empty<AvbHashDescriptor>(), context.CancellationToken), context.CancellationToken).ConfigureAwait(false);

        if (!written.Success)
        {
            throw new InvalidDataException(written.ErrorMessage ?? "VBMeta signing failed.  VBMeta 签名失败。");
        }

        WriteOutput("Signed", written.OutputPath ?? outputPath);
        WriteOutput("Algorithm", written.AlgorithmName);
        context.Log.Write(LogLevel.Info, Title,
            $"Signed vbmeta with {written.AlgorithmName} ({written.OutputSize} bytes).  已完成 VBMeta 签名。");
    }

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}