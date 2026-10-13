using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.NodeViewModels;

public partial class FirmwareNodeViewModel
{
    /// <summary>
    /// Creates a default signature verification node.
    /// <para>创建默认的签名验证节点。</para>
    /// </summary>
    public static FirmwareNodeViewModel CreateVerifyNode()
    {
        var node = new FirmwareNodeViewModel("Verify Signature")
        {
            Location = new Point(100, 100),
            TypeKey = "Validation.Signature.Verify"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Public Key", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Valid", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Report", PortType = NodePortType.Text });
        return node;
    }

    private async Task ExecuteVerifyAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Firmware"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Firmware input was not found.", path);
        }

        if (!await HasMagicAsync(path, "AVB0", context.CancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Signature verification requires an AVB vbmeta image.");
        }

        var publicKey = PathText(ReadInput("Public Key"));
        var result = await FirmwareService.Instance.VerifyAvbAsync(path,
            publicKeyPath: string.IsNullOrWhiteSpace(publicKey) ? null : publicKey,
            context.CancellationToken).ConfigureAwait(false);
        var report = result.Success ? result.Summary : $"Verification failed: {result.Summary}  校验失败。";
        WriteOutput("Valid", result.Success);
        WriteOutput("Report", report);
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title, report);
        if (!result.Success)
        {
            throw new InvalidDataException(report);
        }
    }

    private static async Task<bool> HasMagicAsync(string path, string magic, CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(path);
        var buffer = new byte[magic.Length];
        var read = await fs.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return read >= magic.Length && System.Text.Encoding.ASCII.GetString(buffer, 0, magic.Length) == magic;
    }
}