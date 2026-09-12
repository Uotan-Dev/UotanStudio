using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.Services.Keys;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>Independent key manager window; native pickers stay owned by this window.</summary>
public partial class KeyManagerWindow : Window
{
    private KeyManagerViewModel Model => (KeyManagerViewModel)DataContext!;
    private bool _confirmingDeletion;
    private bool? _creationActionsStacked;
    public KeyManagerWindow()
    {
        InitializeComponent();
        DataContext = new KeyManagerViewModel();
        KeyCreationActions.SizeChanged += (_, _) => UpdateCreationActionsLayout();
        ImportKeyButton.SizeChanged += (_, _) => UpdateCreationActionsLayout();
        GenerateKeyButton.SizeChanged += (_, _) => UpdateCreationActionsLayout();
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

    private void UpdateCreationActionsLayout()
    {
        if (KeyCreationActions.Bounds.Width <= 0) return;
        var stacked = KeyCreationActions.Bounds.Width < ImportKeyButton.DesiredSize.Width + GenerateKeyButton.DesiredSize.Width + 226;
        if (_creationActionsStacked == stacked) return;
        _creationActionsStacked = stacked;
        KeyCreationActions.ColumnDefinitions = new ColumnDefinitions(stacked ? "Auto,*" : "Auto,*,Auto");
        KeyCreationActions.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "Auto");
        Grid.SetRow(GenerateKeyButton, stacked ? 1 : 0);
        Grid.SetColumn(GenerateKeyButton, stacked ? 1 : 2);
    }

    private void NewClick(object? sender, RoutedEventArgs e)
    {
        DetailsScroll.Offset = default;
        NewKeyNameBox.Focus();
    }

    private async void DeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_confirmingDeletion || Model.IsBusy || sender is not Control { DataContext: ManagedKey key }) return;
        var model = Model;
        _confirmingDeletion = true;
        try
        {
            var language = LanguageService.Instance;
            if (await DialogService.ConfirmAsync(this, language.GetString("Keys_Delete"),
                string.Format(language.GetString("Keys_DeleteConfirm"), key.Name), language.GetString("Menu_Delete")))
                await model.DeleteKeyAsync(key);
        }
        catch (Exception ex) { model.Status = ex.Message; }
        finally { _confirmingDeletion = false; }
    }

    private async void CopyClick(object? sender, RoutedEventArgs e) => await Model.RunAsync(async () =>
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(Model.PublicKey);
    });

    private async void ExportPublicClick(object? sender, RoutedEventArgs e) => await ExportAsync(false);
    private async void ExportPairClick(object? sender, RoutedEventArgs e) => await ExportAsync(true);

    private async void ExportCertificateClick(object? sender, RoutedEventArgs e)
    {
        string? path = null;
        await Model.RunAsync(async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            path = folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        });
        if (path is not null) await Model.ExportCertificateAsync(path);
    }

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
