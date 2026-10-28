using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Composition;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Scheme editor window: metadata, declared inputs, assets and ordered stages with capture from the active
/// node project, the current canvas or the current selection.
/// <para>Scheme 编辑器窗口：元数据、声明输入、资产与有序阶段，并支持从活动节点项目、当前画布或当前选区捕获。</para>
/// </summary>
public partial class SchemeEditorWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// Creates an empty editor window.
    /// <para>创建空白编辑器窗口。</para>
    /// </summary>
    public SchemeEditorWindow()
    {
        InitializeComponent();
        var model = ServiceHub.TryGet<SchemeEditorViewModel>() ?? new SchemeEditorViewModel();
        model.RefreshPublisherKeys();
        DataContext = model;
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    /// <summary>
    /// The editor view model bound to this window.
    /// <para>绑定到本窗口的编辑器视图模型。</para>
    /// </summary>
    public SchemeEditorViewModel ViewModel => (SchemeEditorViewModel)DataContext!;

    /// <summary>
    /// Loads a scheme file into the editor.
    /// <para>把 Scheme 文件加载到编辑器。</para>
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
            LogService.Instance.Write(LogLevel.Error, "Scheme", $"Open scheme editor failed: {ex.Message}");
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = ViewModel.FilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = LanguageService.Instance.GetString("Scheme_Save"),
                    DefaultExtension = "uotanscheme",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("UotanStudio Scheme") { Patterns = ["*.uotanscheme"] }
                    ]
                });
                path = file?.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }
            }

            await ViewModel.SaveAsync(path, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Scheme", $"Save scheme failed: {ex.Message}");
        }
    }

    private async void OnSignClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ViewModel.FilePath))
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = LanguageService.Instance.GetString("Scheme_Save"),
                    DefaultExtension = "uotanscheme",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("UotanStudio Scheme") { Patterns = ["*.uotanscheme"] }
                    ]
                });
                if (file?.TryGetLocalPath() is not { } picked
                    || !await ViewModel.SaveAsync(picked, _lifetime.Token))
                {
                    return;
                }
            }

            await ViewModel.SignAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Scheme", $"Sign scheme failed: {ex.Message}");
        }
    }

    private async void OnImportAssetClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Scheme_Assets"),
                AllowMultiple = true,
                FileTypeFilter = [FilePickerFileTypes.All]
            });
            foreach (var file in files)
            {
                if (file.TryGetLocalPath() is { } path)
                {
                    await ViewModel.ImportAssetAsync(path, _lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Scheme", $"Import scheme asset failed: {ex.Message}");
        }
    }

    private void OnNewClick(object? sender, RoutedEventArgs e) => ViewModel.NewScheme();

    private void OnAddInputClick(object? sender, RoutedEventArgs e) => ViewModel.AddInput();

    private void OnRemoveInputClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is SchemeInputEditorViewModel input)
        {
            ViewModel.RemoveInput(input);
        }
    }

    private void OnAddAssetClick(object? sender, RoutedEventArgs e) => ViewModel.AddAsset();

    private void OnRemoveAssetClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is SchemeAssetEditorViewModel asset)
        {
            ViewModel.RemoveAsset(asset);
        }
    }

    private void OnAddStageClick(object? sender, RoutedEventArgs e) => ViewModel.AddStage();

    private void OnRemoveStageClick(object? sender, RoutedEventArgs e) => ViewModel.RemoveStage(ViewModel.SelectedStage);

    private void OnMoveStageUpClick(object? sender, RoutedEventArgs e) => ViewModel.MoveStageUp(ViewModel.SelectedStage);

    private void OnMoveStageDownClick(object? sender, RoutedEventArgs e) => ViewModel.MoveStageDown(ViewModel.SelectedStage);

    private async void OnEmbedWorkflowSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not string workflowName
            || string.IsNullOrWhiteSpace(workflowName))
        {
            return;
        }

        try
        {
            await ViewModel.EmbedSelectedStageAsync(workflowName, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Scheme", $"Embed workflow failed: {ex.Message}");
        }
        finally
        {
            combo.SelectedIndex = -1;
        }
    }

    private async void OnEmbedWorkflowClick(object? sender, RoutedEventArgs e)
    {
        if (EmbedWorkflowCombo.SelectedItem is not string workflowName || string.IsNullOrWhiteSpace(workflowName))
        {
            return;
        }

        try
        {
            await ViewModel.EmbedSelectedStageAsync(workflowName, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Scheme", $"Embed workflow failed: {ex.Message}");
        }
    }

    private void OnCanvasStageClick(object? sender, RoutedEventArgs e) => ViewModel.CreateSingleStageFromCurrentCanvas(false);

    private void OnSelectionStageClick(object? sender, RoutedEventArgs e) => ViewModel.CreateSingleStageFromCurrentCanvas(true);
}