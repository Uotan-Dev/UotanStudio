using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>Workspace document for offline Qualcomm image inspection.
/// <para>用于离线 Qualcomm 镜像检查的工作区文档。</para></summary>
public partial class QualcommImageView : UserControl
{
    /// <summary>Initializes the document view. <para>初始化文档视图。</para></summary>
    public QualcommImageView() => InitializeComponent();

    private async void OnSelectImage(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage ||
            DataContext is not QualcommImageViewModel document)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = LanguageService.Instance.GetString("Dock_Qualcomm_ChooseImage")
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            await document.LoadAsync(path);
        }
    }

    private async void OnSelectRoot(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage ||
            DataContext is not QualcommImageViewModel document)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = LanguageService.Instance.GetString("Dock_Qualcomm_ChooseRoot")
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            document.TrustedRootPath = path;
        }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage ||
            DataContext is not QualcommImageViewModel document || !document.HasResults)
        {
            return;
        }

        var target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageService.Instance.GetString("Dock_Qualcomm_Export"),
            SuggestedFileName = Path.GetFileNameWithoutExtension(document.FilePath) + ".qualcomm.json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });
        if (target?.TryGetLocalPath() is { } path)
        {
            await document.ExportAsync(path);
        }
    }
}