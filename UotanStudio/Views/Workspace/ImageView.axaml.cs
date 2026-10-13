using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Shell;
using Ursa.Controls;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// Image manager panel view, handling the context-menu commands on image entries
/// (extract / synthesize / delete / flash).
/// <para>镜像管理面板视图，处理镜像条目的右键菜单命令（提取/合成/删除/刷写）。</para>
/// </summary>
public partial class ImageView : UserControl
{
    private readonly HashSet<ImageEntryViewModel> _observedImages = [];
    private ImageViewModel? _observedModel;
    private bool _isAttached;
    private bool _columnResizeQueued;
    private ImageEntryViewModel? _contextMenuImage;

    /// <summary>
    /// Creates the image manager panel view and initializes its components.
    /// <para>创建镜像管理面板视图并初始化组件。</para>
    /// </summary>
    public ImageView()
    {
        InitializeComponent();

        ImagesGrid.AddHandler(PointerPressedEvent, OnImagesGridPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        ObserveImages(DataContext as ImageViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        ObserveImages(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_isAttached) ObserveImages(DataContext as ImageViewModel);
    }

    private void ObserveImages(ImageViewModel? model)
    {
        if (ReferenceEquals(_observedModel, model)) return;
        if (_observedModel is not null) _observedModel.Images.CollectionChanged -= OnImagesChanged;
        foreach (var image in _observedImages) image.PropertyChanged -= OnImageTextChanged;
        _observedImages.Clear();
        _observedModel = model;
        if (model is not null)
        {
            model.Images.CollectionChanged += OnImagesChanged;
            foreach (var image in model.Images) ObserveImage(image);
        }
        QueueColumnResize();
    }

    private void ObserveImage(ImageEntryViewModel image)
    {
        if (_observedImages.Add(image)) image.PropertyChanged += OnImageTextChanged;
    }

    private void OnImagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var image in _observedImages) image.PropertyChanged -= OnImageTextChanged;
            _observedImages.Clear();
            if (_observedModel is not null)
                foreach (var image in _observedModel.Images) ObserveImage(image);
        }
        else
        {
            if (e.OldItems is not null)
                foreach (ImageEntryViewModel image in e.OldItems)
                    if (_observedImages.Remove(image)) image.PropertyChanged -= OnImageTextChanged;
            if (e.NewItems is not null)
                foreach (ImageEntryViewModel image in e.NewItems) ObserveImage(image);
        }
        QueueColumnResize();
    }

    private void OnImageTextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(ImageEntryViewModel.DisplayName)
            or nameof(ImageEntryViewModel.Format) or nameof(ImageEntryViewModel.DisplaySize)
            or nameof(ImageEntryViewModel.IsVisible))
            QueueColumnResize();
    }

    private void QueueColumnResize()
    {
        if (!_isAttached || _columnResizeQueued) return;
        _columnResizeQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _columnResizeQueued = false;
            if (!_isAttached) return;

            foreach (var column in ImagesGrid.Columns)
            {

                if (column.Width.IsAuto)
                    column.Width = new DataGridLength(1, DataGridLengthUnitType.Auto, 0, 0);
            }
            foreach (var cell in ImagesGrid.GetVisualDescendants().OfType<DataGridCell>())
                cell.InvalidateMeasure();
        }, DispatcherPriority.Loaded);
    }

    private void OnImageMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || DataContext is not ImageViewModel vm) return;

        menu.Opened -= OnImageMenuOpened;
        menu.Opened += OnImageMenuOpened;
        menu.Closed -= OnImageMenuClosed;
        menu.Closed += OnImageMenuClosed;
        UpdateImageMenu(menu, vm);

        Dispatcher.UIThread.Post(() =>
        {
            if (menu.IsOpen && DataContext is ImageViewModel current)
                UpdateImageMenu(menu, current);
        }, DispatcherPriority.Loaded);
    }

    private void OnImageMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && DataContext is ImageViewModel vm)
            UpdateImageMenu(menu, vm);
    }

    private void OnImageMenuClosed(object? sender, RoutedEventArgs e)
    {

        _contextMenuImage = null;
    }

    private void UpdateImageMenu(ContextMenu menu, ImageViewModel vm)
    {

        var targetImage = _contextMenuImage
            ?? menu.PlacementTarget?.DataContext as ImageEntryViewModel
            ?? menu.DataContext as ImageEntryViewModel;

        foreach (var item in menu.Items.OfType<Control>())
        {
            if (item.Classes.Contains("unpack-action")) item.IsVisible = vm.IsUnpackMode;
            else if (item.Classes.Contains("ext4-action"))
                item.IsVisible = !vm.IsPackMode && targetImage is { IsDirectory: false, Format: "ext4" or "sparse" };
            else if (item.Classes.Contains("pack-action")) item.IsVisible = vm.IsPackMode;
            else if (item.Classes.Contains("flash-action")) item.IsVisible = !vm.IsPackMode;
            else if (item.Classes.Contains("boot-action"))
            {

                item.IsVisible = vm.IsUnpackMode || vm.IsOutputMode;
                item.IsEnabled = targetImage is { IsDirectory: false }
                    && targetImage.Format is "boot" or "init_boot" or "recovery" or "vendor_boot";
            }
        }
    }

    private void OnImagesGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ImagesGrid).Properties.IsRightButtonPressed)
            return;

        var source = e.Source as Visual;
        var row = source as DataGridRow ?? source?.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is not ImageEntryViewModel image || DataContext is not ImageViewModel vm)
            return;

        _contextMenuImage = image;
        ImagesGrid.SelectedItem = image;
        vm.SelectedImage = image;
    }

    /// <summary>
    /// Gets the image entry behind a context-menu item and selects it in the view model.
    /// <para>取出右键菜单项对应的镜像条目并在视图模型中选中它。</para>
    /// </summary>
    private ImageEntryViewModel? SelectMenuImage(object? sender)
    {
        var image = _contextMenuImage
            ?? (sender as MenuItem)?.DataContext as ImageEntryViewModel;
        if (image is null) return null;

        if (DataContext is ImageViewModel vm)
        {
            vm.SelectedImage = image;
        }

        return image;
    }

    /// <summary>Extract the image from the context menu. <para>分解右键菜单选中的镜像。</para></summary>
    private void OnExtractMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is { IsDirectory: false } image && DataContext is ImageViewModel vm)
        {
            if (vm.ExtractSelectedCommand.CanExecute(image)) vm.ExtractSelectedCommand.Execute(image);
        }
    }

    private async void OnExt4MinimizeMenuClick(object? sender, RoutedEventArgs e)
        => await MaintainExt4Async(sender, repair: false);

    private async void OnExt4RepairMinimizeMenuClick(object? sender, RoutedEventArgs e)
        => await MaintainExt4Async(sender, repair: true);

    private async Task MaintainExt4Async(object? sender, bool repair)
    {
        if (SelectMenuImage(sender) is not { IsDirectory: false } image
            || DataContext is not ImageViewModel { IsOperating: false } vm
            || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var language = LanguageService.Instance;
        var confirmed = await DialogService.ConfirmAsync(owner,
            language.GetString(repair ? "ImageView_Ext4RepairMinimize" : "ImageView_Ext4Minimize"),
            language.GetString(repair ? "Image_Ext4RepairWarning" : "Image_Ext4MinimizeConfirm"),
            language.GetString("ImageView_Ext4Minimize"));
        if (!confirmed) return;
        try
        {
            await vm.MinimizeExistingExt4Async(image, repair);
        }
        catch (Exception ex)
        {
            await DialogService.ShowAsync(owner, language.GetString("Image_Ext4MaintenanceFailed"),
                UserError.Describe(ex), MessageBoxIcon.Error);
        }
    }

    /// <summary>Synthesize the context-menu image into an img. <para>合成右键菜单选中的镜像为 img。</para></summary>
    private void OnSynthesizeImgMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuImage(sender, static vm => vm.SynthesizeImgCommand);

    /// <summary>Synthesize the context-menu image into a br. <para>合成右键菜单选中的镜像为 br。</para></summary>
    private void OnSynthesizeBrMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuImage(sender, static vm => vm.SynthesizeBrCommand);

    /// <summary>Synthesize a super image from the project images. <para>从工程镜像合成 super 镜像。</para></summary>
    private void OnSynthesizeSuperMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImageViewModel vm)
        {
            vm.SynthesizeSuperCommand.Execute(null);
        }
    }

    /// <summary>Synthesize a dat from the context-menu image. <para>从右键菜单选中的镜像合成 dat。</para></summary>
    private void OnSynthesizeDatMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuImage(sender, static vm => vm.SynthesizeDatCommand);

    /// <summary>Delete the output file or the entry's unpacked directory. <para>删除输出文件或右键条目对应的解包目录。</para></summary>
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

    /// <summary>Flash the context-menu image to the selected device. <para>把右键条目的镜像刷入选中设备。</para></summary>
    private void OnFlashSelectedMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuImage(sender, static vm => vm.FlashSelectedCommand);

    private async void OnBootPatchMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuImage(sender) is not { IsDirectory: false } image
            || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var imagePath = ProjectContextService.Instance.ResolvePath(image.RelativePath);
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            try { imagePath = Path.GetFullPath(image.RelativePath); }
            catch (Exception) { imagePath = image.RelativePath; }
        }

        var window = new BootPatchWindow(imagePath);
        await window.ShowDialog(owner);
    }

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