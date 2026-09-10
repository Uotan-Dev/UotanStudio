using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.ViewModels.Shell;
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

    private void OnImageMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || DataContext is not ImageViewModel vm) return;

        foreach (var item in menu.Items.OfType<Control>())
        {
            if (item.Classes.Contains("unpack-action")) item.IsVisible = vm.IsUnpackMode;
            else if (item.Classes.Contains("pack-action")) item.IsVisible = vm.IsPackMode;
            else if (item.Classes.Contains("flash-action")) item.IsVisible = !vm.IsPackMode;
        }
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
        => ExecuteForMenuImage(sender, static vm => vm.SynthesizeImgCommand);

    /// <summary><para>合成右键菜单选中的镜像为 br。</para>Synthesize the context-menu image into a br.</summary>
    private void OnSynthesizeBrMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuImage(sender, static vm => vm.SynthesizeBrCommand);

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
        => ExecuteForMenuImage(sender, static vm => vm.SynthesizeDatCommand);

    /// <summary><para>删除输出文件或右键条目对应的解包目录。</para>Delete the output file or the entry's unpacked directory.</summary>
    private async void OnDeleteMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is { } image && DataContext is ImageViewModel { IsOperating: false } vm)
        {
            vm.IsOperating = true;
            try
            {
                if (vm.IsOutputMode) await vm.DeleteImageAsync(image);
                else await vm.DeleteUnpackedDirectoryAsync(image);
            }
            finally { vm.IsOperating = false; }
        }
    }

    /// <summary><para>把右键条目的镜像刷入选中设备。</para>Flash the context-menu image to the selected device.</summary>
    private void OnFlashSelectedMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuImage(sender, static vm => vm.FlashSelectedCommand);

    private void OnPackRomClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImageViewModel { IsOperating: false }) return;
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Delivery));
    }

    private void ExecuteForMenuImage(object? sender,
        Func<ImageViewModel, System.Windows.Input.ICommand> getCommand)
    {
        if (SelectMenuImage(sender) is not { } image || DataContext is not ImageViewModel vm) return;
        var command = getCommand(vm);
        if (command.CanExecute(image)) command.Execute(image);
    }
}