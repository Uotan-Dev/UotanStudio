using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.Editors;

public partial class ApkEditorViewModel
{
    /// <summary>Private key path. <para>私钥路径。</para></summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignApkCommand))]
    private string _signingKeyPath = string.Empty;
    /// <summary>Certificate path. <para>证书路径。</para></summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignApkCommand))]
    private string _signingCertificatePath = string.Empty;
    /// <summary>Output APK path. <para>输出 APK 路径。</para></summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignApkCommand))]
    private string _signingOutputPath = string.Empty;
    /// <summary>In-memory private key password. <para>仅存内存的私钥密码。</para></summary>
    [ObservableProperty] private string _signingKeyPassword = string.Empty;
    /// <summary>In-memory certificate password. <para>仅存内存的证书密码。</para></summary>
    [ObservableProperty] private string _signingCertificatePassword = string.Empty;
    /// <summary>Enable JAR signatures. <para>启用 JAR 签名。</para></summary>
    [ObservableProperty] private bool _signingV1 = true;
    /// <summary>Enable v2 signatures. <para>启用 v2 签名。</para></summary>
    [ObservableProperty] private bool _signingV2 = true;
    /// <summary>Enable v3 signatures. <para>启用 v3 签名。</para></summary>
    [ObservableProperty] private bool _signingV3 = true;
    /// <summary>Signing result shown in the document. <para>文档中显示的签名结果。</para></summary>
    [ObservableProperty] private string _signingStatus = string.Empty;

    /// <summary>
    /// <para>必要项齐备（APK、私钥、证书与输出路径）时才允许执行签名，未填写完整时按钮禁用。</para>
    /// Signing is allowed only when the APK, private key, certificate and output path are all set; the
    /// execute button stays disabled while the form is incomplete.
    /// </summary>
    private bool CanSignApk() =>
        !string.IsNullOrWhiteSpace(FilePath)
        && !string.IsNullOrWhiteSpace(SigningKeyPath)
        && !string.IsNullOrWhiteSpace(SigningCertificatePath)
        && !string.IsNullOrWhiteSpace(SigningOutputPath);

    [RelayCommand(CanExecute = nameof(CanSignApk), IncludeCancelCommand = true)]
    private async Task SignApkAsync(CancellationToken cancellationToken)
    {
        SigningStatus = string.Empty;
        try
        {
            var result = await FirmwareService.Instance.SignApkAsync(
                FilePath ?? string.Empty, SigningOutputPath, SigningKeyPath, SigningCertificatePath,
                SigningKeyPassword, SigningCertificatePassword, SigningV1, SigningV2, SigningV3,
                cancellationToken);
            SigningStatus = result.Summary;
            SigningStateDisplay = result.Summary;
        }
        catch (OperationCanceledException)
        {
            SigningStatus = Services.LanguageService.Instance.GetString("ApkSign_Cancelled");
        }
        finally
        {
            SigningKeyPassword = string.Empty;
            SigningCertificatePassword = string.Empty;
        }
    }
}