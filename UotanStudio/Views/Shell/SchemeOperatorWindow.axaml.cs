using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Read-only operator window for a <c>.uotanscheme</c> package: generated input form, assets, stage status,
/// resume selection, result and logs.
/// <para><c>.uotanscheme</c> 软件包的只读操作员窗口：生成的输入表单、资产、阶段状态、续跑选择、结果与日志。</para>
/// </summary>
public partial class SchemeOperatorWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// Creates an empty operator window.
    /// <para>创建空白操作员窗口。</para>
    /// </summary>
    public SchemeOperatorWindow()
    {
        InitializeComponent();
        DataContext = new SchemeOperatorViewModel(
            (title, message, accept) => DialogService.ConfirmAsync(this, title, message, accept));
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    /// <summary>
    /// The operator view model bound to this window.
    /// <para>绑定到本窗口的操作员视图模型。</para>
    /// </summary>
    public SchemeOperatorViewModel ViewModel => (SchemeOperatorViewModel)DataContext!;

    /// <summary>
    /// Loads a scheme file into the window.
    /// <para>把 Scheme 文件加载到窗口。</para>
    /// </summary>
    /// <param name="path">The scheme file to read. <para>要读取的 Scheme 文件。</para></param>
    /// <returns><see langword="true"/> when the package was loaded. <para>成功加载软件包时返回 <see langword="true"/>。</para></returns>
    public Task<bool> LoadAsync(string path) => ViewModel.LoadAsync(path, _lifetime.Token);

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Scheme_Open"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("UotanStudio Scheme") { Patterns = ["*.uotanscheme"] },
                    FilePickerFileTypes.All
                ]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                await ViewModel.LoadAsync(path, _lifetime.Token);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Scheme", $"Open scheme failed: {ex.Message}");
        }
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RunAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Scheme", $"Run scheme failed: {ex.Message}");
        }
    }

    private void OnStopClick(object? sender, RoutedEventArgs e) => ViewModel.Stop();

    private async void OnTrustPublisherClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ConfirmAndTrustPublisherAsync();
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Scheme", $"Trust scheme publisher failed: {ex.Message}");
        }
    }

    private async void OnBrowseInputClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not SchemeInputViewModel input)
        {
            return;
        }

        try
        {
            if (input.IsDirectory)
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = input.Name,
                    AllowMultiple = false
                });
                input.ApplyPath(folders.FirstOrDefault()?.TryGetLocalPath());
                return;
            }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = input.Name,
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.All]
            });
            input.ApplyPath(files.FirstOrDefault()?.TryGetLocalPath());
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Scheme", $"Browse input failed: {ex.Message}");
        }
    }
}