using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Workspace;
using Ursa.Controls;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// <para>镜像管理面板视图，处理镜像条目的右键菜单命令（提取/合成/删除/刷写）。</para>
/// Image manager panel view, handling the context-menu commands on image entries
/// (extract / synthesize / delete / flash).
/// </summary>
public partial class ImageView : UserControl
{
    private ImageViewModel? _subscribedViewModel;

    /// <summary>
    /// <para>创建镜像管理面板视图并初始化组件。</para>
    /// Creates the image manager panel view and initializes its components.
    /// </summary>
    public ImageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribedViewModel is { } previous)
        {
            previous.ExtractOverwriteRequested -= OnExtractOverwriteRequested;
            previous.DeleteDirectoryRequested -= DeleteUnpackedDirectoryAsync;
        }

        _subscribedViewModel = DataContext as ImageViewModel;
        if (DataContext is ImageViewModel vm)
        {
            vm.ExtractOverwriteRequested -= OnExtractOverwriteRequested;
            vm.ExtractOverwriteRequested += OnExtractOverwriteRequested;
            vm.DeleteDirectoryRequested += DeleteUnpackedDirectoryAsync;
        }
    }

    private async void OnExtractOverwriteRequested(object? sender, (string DirectoryName, TaskCompletionSource<bool> Confirmation) e)
    {
        var owner = this.FindAncestorOfType<Window>();
        if (owner is null)
        {
            e.Confirmation.TrySetResult(false);
            return;
        }

        var title = LanguageService.Instance.GetString("Image_ExtractOverwriteTitle");
        var message = string.Format(LanguageService.Instance.GetString("Image_ExtractOverwriteMessage"), e.DirectoryName);
        var overwrite = LanguageService.Instance.GetString("Image_ExtractOverwrite");
        var skip = LanguageService.Instance.GetString("Image_ExtractSkip");

        e.Confirmation.TrySetResult(await DialogService.ConfirmAsync(owner, title, message, overwrite, skip));
    }

    /// <summary>
    /// <para>取出右键菜单项对应的镜像条目并在视图模型中选中它。</para>
    /// Gets the image entry behind a context-menu item and selects it in the view model.
    /// </summary>
    private ImageEntryViewModel? SelectMenuImage(object? sender)
    {
        if (sender is not MenuItem { DataContext: ImageEntryViewModel image })
        {
            return null;
        }

        if (DataContext is ImageViewModel vm)
        {
            vm.SelectedImage = image;
        }

        return image;
    }

    /// <summary><para>分解右键菜单选中的镜像。</para>Extract the image from the context menu.</summary>
    private void OnExtractMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is not null && DataContext is ImageViewModel vm)
        {
            vm.ExtractSelectedCommand.Execute(null);
        }
    }

    /// <summary><para>合成右键菜单选中的镜像为 img。</para>Synthesize the context-menu image into an img.</summary>
    private void OnSynthesizeImgMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is not null && DataContext is ImageViewModel vm)
        {
            vm.SynthesizeImgCommand.Execute(null);
        }
    }

    /// <summary><para>合成右键菜单选中的镜像为 br。</para>Synthesize the context-menu image into a br.</summary>
    private void OnSynthesizeBrMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is not null && DataContext is ImageViewModel vm)
        {
            vm.SynthesizeBrCommand.Execute(null);
        }
    }

    /// <summary><para>从工程镜像合成 super 镜像。</para>Synthesize a super image from the project images.</summary>
    private void OnSynthesizeSuperMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImageViewModel vm)
        {
            vm.SynthesizeSuperCommand.Execute(null);
        }
    }

    /// <summary><para>从右键菜单选中的镜像合成 dat。</para>Synthesize a dat from the context-menu image.</summary>
    private void OnSynthesizeDatMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is not null && DataContext is ImageViewModel vm)
        {
            vm.SynthesizeDatCommand.Execute(null);
        }
    }

    /// <summary><para>删除右键条目对应的解包目录。</para>Delete the context-menu entry's unpacked directory.</summary>
    private async void OnDeleteMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is { } image)
        {
            await DeleteUnpackedDirectoryAsync(image);
        }
    }

    private async Task DeleteUnpackedDirectoryAsync(ImageEntryViewModel image)
    {
        var owner = this.FindAncestorOfType<Window>();
        var context = ProjectContextService.Instance;
        var root = context.ActiveSubProject?.RootPath;
        if (owner is null || root is null || DataContext is not ImageViewModel vm)
        {
            return;
        }

        var language = LanguageService.Instance;
        try
        {
            var directory = image.IsDirectory
                ? context.ResolvePath(image.RelativePath)
                : Path.GetFullPath(Path.Combine(root, ImageViewModel.GetEntryBaseName(image)));
            if (directory is null)
            {
                return;
            }
            if (!string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The unpacked directory must be a direct child of the sub-project root.");
            }

            if (!Directory.Exists(directory))
            {
                await DialogService.ShowAsync(owner, language.GetString("Image_UnpackedDirectoryMissingTitle"),
                    string.Format(language.GetString("Image_UnpackedDirectoryMissingMessage"), directory), MessageBoxIcon.Information);
                return;
            }

            if (!await DialogService.ConfirmAsync(owner, language.GetString("Dialog_ConfirmDelete"),
                    string.Format(language.GetString("Dialog_ConfirmDeleteMessage"), directory),
                    language.GetString("Menu_Delete")))
            {
                return;
            }

            await Task.Run(() => Directory.Delete(directory, recursive: true));
            image.IsUnpacked = false;
            if (image.IsDirectory)
            {
                vm.Images.Remove(image);
                if (ReferenceEquals(vm.SelectedImage, image)) vm.SelectedImage = null;
            }
            vm.StatusText = language.GetString("ImageView_OperationDone");
            LogService.Instance.Write(LogLevel.Info, "Images", $"Deleted unpacked directory: {directory}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Delete unpacked directory failed: {ex.Message}");
            await DialogService.ShowAsync(owner, language.GetString("Dialog_DeleteFailed"), ex.Message, MessageBoxIcon.Error);
        }
    }

    /// <summary><para>把用户勾选的可刷写镜像刷入选中设备。</para>Flash the selected images to the selected device.</summary>
    private void OnFlashSelectedMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImageViewModel vm)
        {
            vm.FlashSelectedCommand.Execute(null);
        }
    }
}
