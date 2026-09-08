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

    /// <summary>
    /// <para>创建镜像管理面板视图并初始化组件。</para>
    /// Creates the image manager panel view and initializes its components.
    /// </summary>
    public ImageView()
    {
        InitializeComponent();
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
        if (SelectMenuImage(sender) is { IsDirectory: false } image && DataContext is ImageViewModel vm)
        {
            if (vm.ExtractSelectedCommand.CanExecute(image)) vm.ExtractSelectedCommand.Execute(image);
        }
    }

    /// <summary><para>合成右键菜单选中的镜像为 img。</para>Synthesize the context-menu image into an img.</summary>
    private void OnSynthesizeImgMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is { } image && DataContext is ImageViewModel vm)
        {
            if (vm.SynthesizeImgCommand.CanExecute(image)) vm.SynthesizeImgCommand.Execute(image);
        }
    }

    /// <summary><para>合成右键菜单选中的镜像为 br。</para>Synthesize the context-menu image into a br.</summary>
    private void OnSynthesizeBrMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is { } image && DataContext is ImageViewModel vm)
        {
            if (vm.SynthesizeBrCommand.CanExecute(image)) vm.SynthesizeBrCommand.Execute(image);
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
        if (SelectMenuImage(sender) is { } image && DataContext is ImageViewModel vm)
        {
            if (vm.SynthesizeDatCommand.CanExecute(image)) vm.SynthesizeDatCommand.Execute(image);
        }
    }

    /// <summary><para>删除右键条目对应的解包目录。</para>Delete the context-menu entry's unpacked directory.</summary>
    private async void OnDeleteMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is { } image && DataContext is ImageViewModel { IsOperating: false } vm)
        {
            vm.IsOperating = true;
            try { await vm.DeleteUnpackedDirectoryAsync(image); }
            finally { vm.IsOperating = false; }
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