using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

public partial class ApkSigningWindow : Window
{
    public ApkSigningWindow() => InitializeComponent();

    private async void OnBrowseKey(object? sender, RoutedEventArgs e) => await BrowseAsync("key");
    private async void OnBrowseCertificate(object? sender, RoutedEventArgs e) => await BrowseAsync("certificate");
    private async void OnBrowseOutput(object? sender, RoutedEventArgs e) => await BrowseAsync("output");

    private async Task BrowseAsync(string kind)
    {
        if (DataContext is not ApkEditorViewModel vm || StorageProvider is not { } storage) return;
        try
        {
            if (kind == "output")
            {
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    SuggestedFileName = Path.GetFileName(vm.SigningOutputPath),
                    DefaultExtension = "apk",
                    FileTypeChoices = [new FilePickerFileType("APK") { Patterns = ["*.apk"] }],
                    ShowOverwritePrompt = true
                });
                if (file?.TryGetLocalPath() is { } path) vm.SigningOutputPath = path;
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } selectedPath) return;
            if (kind == "key") vm.SigningKeyPath = selectedPath;
            else vm.SigningCertificatePath = selectedPath;
        }
        catch (Exception ex)
        {
            vm.SigningStatus = ex.Message;
        }
    }
}
