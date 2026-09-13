using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
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

    private async void SetCurrentClick(object? sender, RoutedEventArgs e)
    {
        var key = Model.SelectedKey;
        if (key is null || Model.IsBusy || Model.IsSelectedDefault) return;
        var language = LanguageService.Instance;
        var confirmed = await DialogService.ConfirmAsync(this,
            language.GetString("Keys_RestartConfirmTitle"),
            string.Format(language.GetString("Keys_RestartConfirmMessage"), key.Name,
                key.Kind == ManagedKeyKind.Adb ? "ADB" : "HDC"),
            language.GetString("Keys_SetCurrent"));
        if (!confirmed) return;
        await Model.SetCurrentDefaultCommand.ExecuteAsync(null);
        if (Model.Status == "") UotanStudio.Services.Keys.ApplicationRestartService.RestartWithSolution();
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

/// <summary>Shows the "in use" badge when the entry matches the current default signature list. A "Adb"/"Hdc" parameter additionally requires the kind to match, so each purpose shows its own badge.</summary>
/// <summary>当条目命中当前默认签名列表时显示“当前使用”徽标；参数为 "Adb"/"Hdc" 时额外要求用途匹配，使每种用途显示各自徽标。</summary>
internal class DefaultKeyBadgeConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not ManagedKey key || values[1] is not string ids) return false;
        if (!ids.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(key.Kind + ":" + key.Id, StringComparer.Ordinal)) return false;
        // The optional third binding supplies the entry's kind; a mismatched value hides the badge.
        // 可选的第三个绑定提供条目用途；值不匹配时隐藏徽标。
        return values.Count < 3 || values[2] is not ManagedKeyKind kind || key.Kind == kind;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
