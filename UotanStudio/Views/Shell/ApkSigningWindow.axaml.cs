using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services.Keys;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Shell;

public partial class ApkSigningWindow : Window
{
    private readonly KeyLibraryService _keyLibrary = new();
    private IReadOnlyList<ManagedKey> _keys = [];

    public ApkSigningWindow() : this(null)
    {
    }

    public ApkSigningWindow(ApkEditorViewModel? model)
    {
        InitializeComponent();
        DataContext = model ?? new ApkEditorViewModel();
        ApkPathBox.Text = (DataContext as ApkEditorViewModel)?.FilePath;
        Opened += (_, _) => RefreshKeys();
    }

    private void RefreshKeys()
    {
        _keys = _keyLibrary.List().Where(key => key.Kind == ManagedKeyKind.Apk).ToArray();
        SigningKeyBox.ItemsSource = _keys;
        SigningCertificateBox.ItemsSource = _keys;
        if (DataContext is ApkEditorViewModel vm)
        {
            SigningKeyBox.SelectedItem = _keys.FirstOrDefault(key =>
                string.Equals(_keyLibrary.PrivatePath(key), vm.SigningKeyPath, StringComparison.OrdinalIgnoreCase));
            SigningCertificateBox.SelectedItem = _keys.FirstOrDefault(key =>
                string.Equals(_keyLibrary.CertificatePath(key), vm.SigningCertificatePath, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async void OnBrowseApk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ApkEditorViewModel vm || StorageProvider is not { } storage) return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("APK") { Patterns = ["*.apk"] }]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                ApkPathBox.Text = path;
                await vm.LoadAsync(path);
            }
        }
        catch (Exception ex)
        {
            vm.SigningStatus = global::UotanStudio.Services.UserError.Describe(ex);
        }
    }

    private async void OnBrowseOutputDirectory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ApkEditorViewModel vm || StorageProvider is not { } storage) return;
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
            var fileName = Path.GetFileName(vm.SigningOutputPath);
            if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
            {
                fileName = string.IsNullOrWhiteSpace(vm.FilePath)
                    ? "signed.apk"
                    : Path.GetFileNameWithoutExtension(vm.FilePath) + "-signed.apk";
            }
            vm.SigningOutputPath = Path.Combine(folder, fileName);
        }
        catch (Exception ex)
        {
            vm.SigningStatus = global::UotanStudio.Services.UserError.Describe(ex);
        }
    }

    private void OnSigningKeyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ApkEditorViewModel vm || SigningKeyBox.SelectedItem is not ManagedKey key) return;
        vm.SigningKeyPath = _keyLibrary.PrivatePath(key);
    }

    private void OnSigningCertificateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ApkEditorViewModel vm || SigningCertificateBox.SelectedItem is not ManagedKey key) return;
        try
        {
            vm.SigningCertificatePath = File.Exists(_keyLibrary.CertificatePath(key))
                ? _keyLibrary.CertificatePath(key)
                : _keyLibrary.EnsureCertificate(key);
        }
        catch (Exception ex)
        {
            vm.SigningStatus = global::UotanStudio.Services.UserError.Describe(ex);
        }
    }

}