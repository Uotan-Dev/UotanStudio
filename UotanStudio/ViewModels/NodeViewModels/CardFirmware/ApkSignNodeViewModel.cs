using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Signs APK files using user-supplied keys. <para>使用用户提供的密钥签署 APK 文件。</para></summary>
public sealed class ApkSignNodeViewModel : FlowNodeViewModel
{
    /// <summary>Creates the signing node and its ports. <para>创建签名节点及其端口。</para></summary>
    public ApkSignNodeViewModel() : base("Sign APK", NodeCategory.Firmware)
    {
        TypeKey = "Firmware.SignApk";
        foreach (var title in new[] { "APK Path", "Output Path", "Private Key", "Certificate" })
            Input.Add(new NodeConnectorViewModel { Title = title, PortType = NodePortType.Path });
        foreach (var title in new[] { "V1", "V2", "V3" })
            Input.Add(new NodeConnectorViewModel { Title = title, PortType = NodePortType.Boolean, InitialValue = "true" });
        foreach (var title in new[] { "Key Password Environment", "Certificate Password Environment" })
            Input.Add(new NodeConnectorViewModel { Title = title, PortType = NodePortType.Text });
        Output.Add(new NodeConnectorViewModel { Title = "Signed APK", PortType = NodePortType.Path });
        Output.Add(new NodeConnectorViewModel { Title = "Report", PortType = NodePortType.Text });
    }

    /// <summary>Signs asynchronously; failures propagate to the workflow engine. <para>异步签名，失败向工作流引擎传播。</para></summary>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        WriteOutput("Signed APK", null);
        WriteOutput("Report", null);
        var result = await FirmwareService.Instance.SignApkAsync(
            RequiredPath("APK Path"), RequiredPath("Output Path"),
            RequiredPath("Private Key"), RequiredPath("Certificate"),
            keyPassword: Password("Key Password Environment"), certPassword: Password("Certificate Password Environment"),
            enableV1: Scheme("V1"), enableV2: Scheme("V2"), enableV3: Scheme("V3"),
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Report", result.Summary);
        if (!result.Success) throw new InvalidOperationException(result.Summary);
        WriteOutput("Signed APK", RequiredPath("Output Path"));
        context.Log.Write(LogLevel.Info, Title, result.Summary);
    }

    private string RequiredPath(string title) => ReadInput(title) is string path && !string.IsNullOrWhiteSpace(path)
        ? path : throw new ArgumentException($"{title} is required. 必须提供 {title}。");

    private bool Scheme(string title) => ReadInput(title) switch
    {
        null => true,
        bool value => value,
        string text when bool.TryParse(text, out var value) => value,
        _ => throw new ArgumentException($"{title} must be true or false. {title} 必须为 true 或 false。")
    };

    private string? Password(string title)
    {
        if (ReadInput(title) is not string name || string.IsNullOrWhiteSpace(name)) return null;
        return Environment.GetEnvironmentVariable(name)
            ?? throw new ArgumentException($"Password environment variable '{name}' is not set. 密码环境变量未设置。");
    }
}