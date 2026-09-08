using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>APK 元数据编辑器视图：主视图展示应用图标、包名、版本、SDK、ABI 与权限等详细信息。</para>
/// APK metadata editor view: the main view shows the app icon, package name, version, SDK, ABIs
/// and permission details.
/// </summary>
public partial class ApkEditorView : UserControl
{
    /// <summary>
    /// <para>创建 APK 元数据编辑器视图并初始化组件。</para>
    /// Creates the APK metadata editor view and initializes its components.
    /// </summary>
    public ApkEditorView()
    {
        InitializeComponent();
    }

    private async void OnBrowseKey(object? sender, RoutedEventArgs e) => await BrowseAsync("key");
    private async void OnBrowseCertificate(object? sender, RoutedEventArgs e) => await BrowseAsync("certificate");
    private async void OnBrowseOutput(object? sender, RoutedEventArgs e) => await BrowseAsync("output");

    private async Task BrowseAsync(string kind)
    {
        if (DataContext is not ApkEditorViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            if (kind == "output")
            {
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    SuggestedFileName = System.IO.Path.GetFileName(vm.SigningOutputPath),
                    DefaultExtension = "apk",
                    FileTypeChoices = [new FilePickerFileType("APK") { Patterns = ["*.apk"] }],
                    ShowOverwritePrompt = true
                });
                if (file?.TryGetLocalPath() is { } path) vm.SigningOutputPath = path;
            }
            else
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
                if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
                if (kind == "key") vm.SigningKeyPath = path;
                else vm.SigningCertificatePath = path;
            }
        }
        catch (Exception ex)
        {
            vm.SigningStatus = ex.Message;
        }
    }
}
