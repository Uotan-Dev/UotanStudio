using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// APK metadata editor view: the main view shows the app icon, package name, version, SDK, ABIs
/// and permission details.
/// <para>APK 元数据编辑器视图：主视图展示应用图标、包名、版本、SDK、ABI 与权限等详细信息。</para>
/// </summary>
public partial class ApkEditorView : UserControl
{
    /// <summary>
    /// Creates the APK metadata editor view and initializes its components.
    /// <para>创建 APK 元数据编辑器视图并初始化组件。</para>
    /// </summary>
    public ApkEditorView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private DeviceViewModel? _deviceViewModel;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _deviceViewModel = DeviceViewModel.EnsureInstance();
        _deviceViewModel.PropertyChanged += OnDevicePropertyChanged;
        (DataContext as ApkEditorViewModel)?.RefreshInstallAvailability();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_deviceViewModel is not null)
        {
            _deviceViewModel.PropertyChanged -= OnDevicePropertyChanged;
            _deviceViewModel = null;
        }
    }

    private void OnDevicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceViewModel.SelectedDevice)
            or nameof(DeviceViewModel.HasDevices)
            or nameof(DeviceViewModel.HasSelectedDevice))
        {
            (DataContext as ApkEditorViewModel)?.RefreshInstallAvailability();
        }
    }

    private async void OnExtractClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ApkEditorViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        try
        {
            await vm.ExtractToContainingDirectoryAsync();
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Apk_Extract"),
                Path.GetDirectoryName(Path.GetFullPath(vm.FilePath!)) ?? string.Empty,
                Ursa.Controls.MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Apk_Extract"),
                UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    private async void OnViewClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ApkEditorViewModel vm || string.IsNullOrWhiteSpace(vm.FilePath))
        {
            return;
        }

        var workspace = ProjectOperationsService.Instance.WorkspaceVM;
        if (workspace is not null)
        {
            await workspace.OpenFileSystemAsync(vm.FilePath);
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Apk_View"),
                "The workspace is not available.", Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    private async void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ApkEditorViewModel vm || TopLevel.GetTopLevel(this) is not Window owner || !vm.CanInstall)
        {
            return;
        }

        try
        {
            var result = await vm.InstallToSelectedDeviceAsync();
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Apk_Install"),
                result.Summary, result.Success ? Ursa.Controls.MessageBoxIcon.Information : Ursa.Controls.MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Apk_Install"),
                UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    private async void OnSignClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ApkEditorViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var window = new Shell.ApkSigningWindow(vm);
        await window.ShowDialog(owner);
    }

}