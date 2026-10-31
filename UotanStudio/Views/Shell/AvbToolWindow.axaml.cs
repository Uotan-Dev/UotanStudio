using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Composition;
using UotanStudio.Services;
using UotanStudio.Services.Ui;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Shell;
using Ursa.Controls;

namespace UotanStudio.Views.Shell;

/// <summary>
/// AVB tool window: key selection, partition footer operations and A/B OTA patching. Native pickers and
/// confirmations stay owned by this window; every operation runs through the view model.
/// <para>AVB 工具窗口：密钥选择、分区尾部操作与 A/B OTA 修补。原生选择器与确认框由该窗口持有，所有操作经视图模型执行。</para>
/// </summary>
public partial class AvbToolWindow : Window
{
    /// <summary>
    /// Creates the AVB tool window, loads the key library and cancels the close request while an operation runs.
    /// <para>创建 AVB 工具窗口，载入密钥库，并在操作运行期间取消关闭请求。</para>
    /// </summary>
    public AvbToolWindow()
    {
        InitializeComponent();
        DataContext = ServiceHub.TryGet<AvbToolViewModel>() ?? new AvbToolViewModel();
        Opened += async (_, _) => await RunOperationAsync(Model.RefreshKeysAsync);
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

    private async void OnRefreshKeysClick(object? sender, RoutedEventArgs e) => await RunOperationAsync(Model.RefreshKeysAsync);

    private async void OnAvbKeyDropDownOpened(object? sender, EventArgs e) => await RunOperationAsync(Model.RefreshKeysAsync);

    private async void OnExportPublicKeyClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickSaveFileAsync("Avb_KeyExportPublic", "avb_pkmd.bin", "bin");
        if (path is not null)
        {
            await RunOperationAsync(() => Model.ExportAvbPublicKeyAsync(path));
        }
    }

    private void OnManageKeysClick(object? sender, RoutedEventArgs e) =>
        UiWindowService.Shared.ShowHostWindow(HostWindowKind.KeyManager);

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

    private async void OnAddFooterClick(object? sender, RoutedEventArgs e) => await RunOperationAsync(Model.AddFooterAsync);

    private async void OnVerifyImageClick(object? sender, RoutedEventArgs e) => await RunOperationAsync(Model.VerifyImageAsync);

    private async void OnInspectImageClick(object? sender, RoutedEventArgs e) => await RunOperationAsync(Model.InspectImageAsync);

    private async void OnRemoveFooterClick(object? sender, RoutedEventArgs e) => await RunOperationAsync(Model.RemoveFooterAsync);

    private async void OnOtaInputBrowseClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenFileAsync("Avb_OtaSelectInputTitle", "*.zip", "*.bin");
        if (path is null)
        {
            return;
        }

        Model.OtaInputPath = path;
        await RunOperationAsync(Model.LoadOtaPartitionsAsync);
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
        await RunOperationAsync(Model.LoadOtaPartitionsAsync);

    private async void OnPatchOtaClick(object? sender, RoutedEventArgs e) => await RunOperationAsync(Model.PatchOtaAsync);

    private async void OnVerifyOtaClick(object? sender, RoutedEventArgs e) => await RunOperationAsync(Model.VerifyOtaAsync);

    /// <summary>
    /// Opens the disable-AVB tab and sets the target image file.
    /// <para>打开禁用 AVB 标签页并设置目标镜像文件。</para>
    /// </summary>
    /// <param name="filePath">The image file path. <para>镜像文件路径。</para></param>
    public void OpenDisableAvbTab(string filePath) => Model.LoadDisableAvbTarget(filePath);

    private async void OnDisableAvbFileBrowseClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenFileAsync("Avb_DisableSelectFile", "*.img", "*.bin", "*.raw");
        if (path is not null)
        {
            Model.LoadDisableAvbTarget(path);
        }
    }

    private async void OnDisableAvbOutputBrowseClick(object? sender, RoutedEventArgs e)
    {
        var suggested = Model.DisableAvbOutputPath is { Length: > 0 } output
            ? Path.GetFileName(output)
            : (Model.DisableAvbFilePath is { Length: > 0 } input
                ? $"{Path.GetFileNameWithoutExtension(input)}_{AvbToolViewModel.GetDisableCommandSuffix(Model.SelectedDisableCommand)}{Path.GetExtension(input)}"
                : null);
        var ext = Model.DisableAvbFilePath is { Length: > 0 } img ? Path.GetExtension(img).TrimStart('.') : "img";
        if (string.IsNullOrEmpty(ext))
        {
            ext = "img";
        }

        var path = await PickSaveFileAsync("Avb_FooterSelectOutputTitle", suggested, ext);
        if (path is not null)
        {
            Model.DisableAvbOutputPath = path;
        }
    }

    private async void OnExecuteDisableClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync(Model.DisableVerificationAsync);

    /// <summary>
    /// Picks one file with an optional extension filter; picker failures are shown in a dialog.
    /// <para>选择单个文件，可选按扩展名过滤；选择器失败以弹窗提示。</para>
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
            await ShowErrorAsync(ex);
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
            await ShowErrorAsync(ex);
            return [];
        }
    }

    /// <summary>
    /// Picks one destination file; picker failures are shown in a dialog.
    /// <para>选择单个目标文件；选择器失败以弹窗提示。</para>
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
            await ShowErrorAsync(ex);
            return null;
        }
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        var model = Model;
        if (model.IsBusy)
        {
            return;
        }

        model.Status = "";
        model.OtaSummary = "";
        try
        {
            await operation();
            var messages = new[] { model.Status, model.OtaSummary }
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal);
            var message = string.Join(Environment.NewLine + Environment.NewLine, messages);
            model.Status = "";
            model.OtaSummary = "";
            if (!string.IsNullOrWhiteSpace(message))
            {
                await DialogService.ShowAsync(this, LanguageService.Instance.GetString("Avb_Title"),
                    message, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex);
        }
    }

    private async void OnZipKeySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var model = Model;
        if (model.IsBusy || string.IsNullOrWhiteSpace(model.Status))
        {
            return;
        }

        var message = model.Status;
        model.Status = "";
        await DialogService.ShowAsync(this, LanguageService.Instance.GetString("Avb_Title"),
            message, MessageBoxIcon.Error);
    }

    private async Task ShowErrorAsync(Exception exception)
    {
        await DialogService.ShowAsync(this, LanguageService.Instance.GetString("Avb_Title"),
            UserError.Describe(exception), MessageBoxIcon.Error);
    }

    private static FilePickerFileType BuildFileType(string[] patterns) =>
        new(string.Join(" / ", patterns)) { Patterns = patterns };
}