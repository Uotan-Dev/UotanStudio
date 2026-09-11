using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>Independent key manager window; native pickers stay owned by this window.</summary>
public partial class KeyManagerWindow : Window
{
    private KeyManagerViewModel Model => (KeyManagerViewModel)DataContext!;
    public KeyManagerWindow()
    {
        InitializeComponent();
        DataContext = new KeyManagerViewModel();
        Opened += async (_, _) => await Model.RefreshCommand.ExecuteAsync(null);
        Closing += (_, e) => e.Cancel = Model.IsBusy;
    }

    private async void ImportClick(object? sender, RoutedEventArgs e)
    {
        string? path = null;
        await Model.RunAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
            path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        });
        if (path is not null) await Model.ImportAsync(path);
    }

    private void NewClick(object? sender, RoutedEventArgs e)
    {
        DetailsScroll.Offset = default;
        NewKeyNameBox.Focus();
    }

    private async void CopyClick(object? sender, RoutedEventArgs e) => await Model.RunAsync(async () =>
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(Model.PublicKey);
    });

    private async void ExportPublicClick(object? sender, RoutedEventArgs e) => await ExportAsync(false);
    private async void ExportPairClick(object? sender, RoutedEventArgs e) => await ExportAsync(true);

    private async Task ExportAsync(bool includePrivate)
    {
        string? path = null;
        await Model.RunAsync(async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            path = folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        });
        if (path is not null) await Model.ExportAsync(path, includePrivate);
    }
}