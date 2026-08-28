using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// <para>镜像管理面板视图，处理镜像条目的右键菜单命令（提取/合成/删除）。</para>
/// Image manager panel view, handling the right-click commands on image entries
/// (extract / synthesize / delete).
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

    /// <summary><para>删除右键菜单选中的镜像。</para>Delete the image from the context menu.</summary>
    private void OnDeleteMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is not null && DataContext is ImageViewModel vm)
        {
            vm.DeleteSelectedCommand.Execute(null);
        }
    }
}
