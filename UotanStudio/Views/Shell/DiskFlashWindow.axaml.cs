using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using UotanStudio.Composition;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Disk-image flashing window: image selection, target drive list, safety switches and progress.
/// <para>磁盘镜像烧录窗口：镜像选择、目标驱动器列表、安全开关与进度。</para>
/// </summary>
public partial class DiskFlashWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DiskFlashWindow"/> class.
    /// <para>初始化 <see cref="DiskFlashWindow"/> 类的新实例。</para>
    /// </summary>
    public DiskFlashWindow()
    {
        InitializeComponent();
        DataContext = ServiceHub.TryGet<DiskFlashViewModel>() ?? new DiskFlashViewModel();
        Opened += OnOpened;
    }

    private DiskFlashViewModel ViewModel => (DiskFlashViewModel)DataContext!;

    private void OnOpened(object? sender, EventArgs e) => ViewModel.RefreshDrivesCommand.Execute(null);

    private void OnDriveCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed &&
            sender is Border { DataContext: DiskFlashDriveItem item } &&
            !item.IsBlocked)
        {
            if (e.Source is not CheckBox && (e.Source as Visual)?.FindAncestorOfType<CheckBox>() is null)
            {
                item.IsSelected = !item.IsSelected;
                e.Handled = true;
            }
        }
    }

    private async void OnChooseImageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                ViewModel.SetImage(path);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "DiskFlash", $"Choose image failed: {ex.Message}");
        }
    }
}