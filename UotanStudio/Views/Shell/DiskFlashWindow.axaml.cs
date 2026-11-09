using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using UotanStudio.Composition;
using UotanStudio.Services;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Disk-image flashing window: image selection, the single target drive, safety switches and progress.
/// <para>磁盘镜像烧录窗口：镜像选择、唯一目标驱动器、安全开关与进度。</para>
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
        var diagnostics = ServiceHub.TryGet<IDiskFlashDialogs>();
        DataContext = ServiceHub.TryGet<DiskFlashViewModel>() ?? new DiskFlashViewModel(dialogs: diagnostics);
        Opened += OnOpened;
    }

    private DiskFlashViewModel ViewModel => (DiskFlashViewModel)DataContext!;

    private void OnOpened(object? sender, EventArgs e) => ViewModel.RefreshDrivesCommand.Execute(null);

    private void OnDriveCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed ||
            sender is not Border { DataContext: DiskFlashDriveItem item } ||
            item.IsBlocked)
        {
            return;
        }

        if (e.Source is CheckBox || (e.Source as Visual)?.FindAncestorOfType<CheckBox>() is not null)
        {
            return;
        }

        ViewModel.SelectDrive(item);
        e.Handled = true;
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ViewModel.SetImage(null);
            StatusError(ex.Message);
            LogService.Instance.Write(LogLevel.Error, "DiskFlash", $"Choose image failed: {ex.Message}");
        }
    }

    private void StatusError(string message)
    {
        ViewModel.StatusMessage = message;
        ViewModel.StatusIsError = true;
    }
}