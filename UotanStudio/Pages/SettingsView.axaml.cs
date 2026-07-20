using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Linq;
using System.Threading.Tasks;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages;

/// <summary>
/// <para>设置页面视图，提供应用程序设置的交互界面。</para>
/// Settings page view, providing the interactive interface for application settings.
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>
    /// <para>初始化设置页面视图的新实例，订阅 VM 重置确认请求事件。</para>
    /// Initializes a new instance of the SettingsView, subscribing to the VM reset confirmation request event.
    /// </summary>
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>DataContext 变化时（重新）订阅 VM 的 ResetToDefaultsRequested 事件。</summary>
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.ResetToDefaultsRequested -= OnResetToDefaultsRequested;
            vm.ResetToDefaultsRequested += OnResetToDefaultsRequested;
        }
    }

    /// <summary>VM 触发重置确认请求时弹出确认对话框，结果写回 TaskCompletionSource。</summary>
    private async void OnResetToDefaultsRequested(object? sender, TaskCompletionSource<bool> tcs)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not Window owner)
            {
                tcs.TrySetResult(false);
                return;
            }

            var dialog = new Window
            {
                Title = "Confirm Reset",
                Width = 360,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Spacing = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Reset all settings to defaults? This cannot be undone.",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children =
                            {
                                new Button
                                {
                                    Content = "Cancel",
                                    Tag = false
                                },
                                new Button
                                {
                                    Content = "Reset",
                                    Tag = true
                                }
                            }
                        }
                    }
                }
            };

            // 简化：通过 DialogResult 标志
            var ok = false;
            foreach (var btn in ((StackPanel)((StackPanel)dialog.Content).Children[1]).Children)
            {
                if (btn is Button b)
                    b.Click += (_, _) => { ok = (bool)(b.Tag ?? false); dialog.Close(); };
            }

            await dialog.ShowDialog(owner);
            tcs.TrySetResult(ok);
        }
        catch
        {
            tcs.TrySetResult(false);
        }
    }

    /// <summary>弹出文件选择对话框，选取 ADB 可执行文件路径并写回 VM。</summary>
    private async void OnBrowseAdbClick(object? sender, RoutedEventArgs e)
        => await PickExecutableAsync(path =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.AdbPath = path;
        });

    /// <summary>弹出文件选择对话框，选取 Fastboot 可执行文件路径并写回 VM。</summary>
    private async void OnBrowseFastbootClick(object? sender, RoutedEventArgs e)
        => await PickExecutableAsync(path =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.FastbootPath = path;
        });

    /// <summary>弹出文件选择对话框，选取解包工具可执行文件路径并写回 VM。</summary>
    private async void OnBrowseUnpackToolClick(object? sender, RoutedEventArgs e)
        => await PickExecutableAsync(path =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.UnpackToolPath = path;
        });

    /// <summary>弹出文件夹选择对话框，选取临时目录并写回 VM。</summary>
    private async void OnBrowseTempDirClick(object? sender, RoutedEventArgs e)
        => await PickFolderAsync(path =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.TempDirectory = path;
        });

    /// <summary>
    /// <para>通用可执行文件选择器，选择成功后通过 apply 回调写回对应 VM 属性。</para>
    /// Generic executable picker; writes back to the corresponding VM property via the apply callback on success.
    /// </summary>
    /// <param name="apply">
    /// <para>接收选中文件本地路径的写回回调。</para>
    /// Callback receiving the selected file's local path for write-back.
    /// </param>
    private async Task PickExecutableAsync(System.Action<string> apply)
    {
        if (DataContext is not SettingsViewModel) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select executable",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executables")
                {
                    Patterns = new[] { "*.exe", "*" }
                }
            }
        });

        var file = files?.FirstOrDefault();
        if (file == null) return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        apply(path);
    }

    /// <summary>
    /// <para>通用文件夹选择器，选择成功后通过 apply 回调写回对应 VM 属性。</para>
    /// Generic folder picker; writes back to the corresponding VM property via the apply callback on success.
    /// </summary>
    /// <param name="apply">
    /// <para>接收选中文件夹本地路径的写回回调。</para>
    /// Callback receiving the selected folder's local path for write-back.
    /// </param>
    private async Task PickFolderAsync(System.Action<string> apply)
    {
        if (DataContext is not SettingsViewModel) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder",
            AllowMultiple = false
        });

        var folder = folders?.FirstOrDefault();
        if (folder == null) return;

        var path = folder.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        apply(path);
    }
}
