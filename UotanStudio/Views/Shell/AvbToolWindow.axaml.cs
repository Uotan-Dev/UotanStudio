using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.Services.Ui;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// AVB tool window: key selection, partition footer operations and A/B OTA patching. Native pickers and
/// confirmations stay owned by this window; every operation runs through the view model.
/// <para>AVB 工具窗口：密钥选择、分区尾部操作与 A/B OTA 修补。原生选择器与确认框由该窗口持有，所有操作经视图模型执行。</para>
/// </summary>
public partial class AvbToolWindow : Window
{
    private bool _confirmingDeletion;

    /// <summary>
    /// Creates the AVB tool window, loads the key library and cancels the close request while an operation runs.
    /// <para>创建 AVB 工具窗口，载入密钥库，并在操作运行期间取消关闭请求。</para>
    /// </summary>
    public AvbToolWindow()
    {
        InitializeComponent();
        DataContext = new AvbToolViewModel();
        Opened += async (_, _) => await Model.RefreshKeysAsync();
        Closing += (_, e) =>
        {
            if (!Model.IsBusy)
            {
                return;
            }

            Model.CancelPendingWork();
            e.Cancel = true;
        };
    }

    private AvbToolViewModel Model => (AvbToolViewModel)DataContext!;

    private async void OnRefreshKeysClick(object? sender, RoutedEventArgs e) => await Model.RefreshKeysAsync();

    private async void OnGenerateKeyClick(object? sender, RoutedEventArgs e) => await Model.GenerateKeyAsync();

    private async void OnImportKeyClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenFileAsync("Avb_KeyImport", "*.pem", "*.der", "*.key");
        if (path is not null)
        {
            await Model.ImportKeyAsync(path);
        }
    }

    private async void OnExportPublicKeyClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickSaveFileAsync("Avb_KeyExportPublic", "avb_pkmd.bin", "bin");
        if (path is not null)
        {
            await Model.ExportAvbPublicKeyAsync(path);
        }
    }

    private async void OnDeleteKeyClick(object? sender, RoutedEventArgs e)
    {
        if (_confirmingDeletion || Model.IsBusy || Model.SelectedKey is not { } key)
        {
            return;
        }

        _confirmingDeletion = true;
        try
        {
            var language = LanguageService.Instance;
            var confirmed = await DialogService.ConfirmAsync(this,
                language.GetString("Avb_KeyDeleteTitle"),
                string.Format(language.GetString("Avb_KeyDeleteConfirm"), key.Name),
                language.GetString("Avb_KeyDelete"));
            if (confirmed)
            {
                await Model.DeleteKeyAsync(key);
            }
        }
        catch (Exception ex)
        {
            Model.Status = UserError.Describe(ex);
        }
        finally
        {
            _confirmingDeletion = false;
        }
    }

    private async void OnAddImagesClick(object? sender, RoutedEventArgs e)
    {
        var paths = await PickImagesAsync();
        if (paths.Count > 0)
        {
            Model.AddImages(paths);
        }
    }

    private void OnRemoveImageClick(object? sender, RoutedEventArgs e) => Model.RemoveSelectedImage();

    private async void OnFooterOutputBrowseClick(object? sender, RoutedEventArgs e)
    {
        var suggested = Model.SelectedImage is { Length: > 0 } image
            ? Path.GetFileName(image) + ".avb.img"
            : null;
        var path = await PickSaveFileAsync("Avb_FooterSelectOutputTitle", suggested, "img");
        if (path is not null)
        {
            Model.FooterOutputPath = path;
        }
    }

    private async void OnTrustedKeyBrowseClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenFileAsync("Avb_FooterSelectTrustedKeyTitle", "*.bin");
        if (path is not null)
        {
            Model.TrustedPublicKeyPath = path;
        }
    }

    private async void OnAddFooterClick(object? sender, RoutedEventArgs e) => await Model.AddFooterAsync();

    private async void OnVerifyImageClick(object? sender, RoutedEventArgs e) => await Model.VerifyImageAsync();

    private async void OnInspectImageClick(object? sender, RoutedEventArgs e) => await Model.InspectImageAsync();

    private async void OnRemoveFooterClick(object? sender, RoutedEventArgs e) => await Model.RemoveFooterAsync();

    private async void OnOtaInputBrowseClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenFileAsync("Avb_OtaSelectInputTitle", "*.zip", "*.bin");
        if (path is null)
        {
            return;
        }

        Model.OtaInputPath = path;
        await Model.LoadOtaPartitionsAsync();
    }

    private async void OnOtaOutputBrowseClick(object? sender, RoutedEventArgs e)
    {
        var suggested = Model.OtaOutputPath is { Length: > 0 } output ? Path.GetFileName(output) : null;
        var path = await PickSaveFileAsync("Avb_OtaSelectOutputTitle", suggested, "zip");
        if (path is not null)
        {
            Model.OtaOutputPath = path;
        }
    }

    private async void OnOtaCertificateBrowseClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenFileAsync("Avb_OtaSelectCertificateTitle", "*.crt", "*.pem", "*.der");
        if (path is not null)
        {
            Model.OtaCertificatePath = path;
        }
    }

    private async void OnLoadOtaPartitionsClick(object? sender, RoutedEventArgs e) =>
        await Model.LoadOtaPartitionsAsync();

    private async void OnPatchOtaClick(object? sender, RoutedEventArgs e) => await Model.PatchOtaAsync();

    private async void OnVerifyOtaClick(object? sender, RoutedEventArgs e) => await Model.VerifyOtaAsync();

    /// <summary>
    /// Picks one file with an optional extension filter; picker failures are reported through the view model status.
    /// <para>选择单个文件，可选按扩展名过滤；选择器失败经视图模型状态上报。</para>
    /// </summary>
    private async Task<string?> PickOpenFileAsync(string titleKey, params string[] patterns)
    {
        try
        {
            var options = new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = LanguageService.Instance.GetString(titleKey),
            };
            if (patterns.Length > 0)
            {
                options.FileTypeFilter = new[] { BuildFileType(patterns) };
            }

            var files = await StorageProvider.OpenFilePickerAsync(options);
            return files.Count == 1 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            Model.Status = UserError.Describe(ex);
            return null;
        }
    }

    /// <summary>
    /// Picks several partition images at once.
    /// <para>一次选择多个分区镜像。</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> PickImagesAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = LanguageService.Instance.GetString("Avb_FooterSelectImageTitle"),
            });
            return [.. files.Select(file => file.TryGetLocalPath()).OfType<string>()];
        }
        catch (Exception ex)
        {
            Model.Status = UserError.Describe(ex);
            return [];
        }
    }

    /// <summary>
    /// Picks one destination file; picker failures are reported through the view model status.
    /// <para>选择单个目标文件；选择器失败经视图模型状态上报。</para>
    /// </summary>
    private async Task<string?> PickSaveFileAsync(string titleKey, string? suggestedFileName, string? extension)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LanguageService.Instance.GetString(titleKey),
                SuggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName) ? null : suggestedFileName,
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
            });
            return file?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            Model.Status = UserError.Describe(ex);
            return null;
        }
    }

    private static FilePickerFileType BuildFileType(string[] patterns) =>
        new(string.Join(" / ", patterns)) { Patterns = patterns };
}