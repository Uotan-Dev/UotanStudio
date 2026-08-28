using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using UotanStudio.Services;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Views.Devices;

/// <summary>
/// Device page view, providing ADB/Fastboot device list and connection management interface.
/// <para>设备页面视图，提供 ADB/Fastboot 设备列表与连接管理界面。</para>
/// </summary>
public partial class DeviceView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceView"/> class.
    /// <para>初始化 <see cref="DeviceView"/> 类的新实例。</para>
    /// </summary>
    public DeviceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is DeviceViewModel vm)
        {
            vm.RebootConfirmationRequested -= OnRebootConfirmationRequested;
            vm.RebootConfirmationRequested += OnRebootConfirmationRequested;
            vm.FlashConfirmationRequested -= OnFlashConfirmationRequested;
            vm.FlashConfirmationRequested += OnFlashConfirmationRequested;
        }
    }

    private async void OnRebootConfirmationRequested(object? sender, (string Mode, string ModeDisplayName, TaskCompletionSource<bool> Confirmation) e)
    {
        var owner = this.FindAncestorOfType<Window>();
        if (owner is null)
        {
            e.Confirmation.TrySetResult(false);
            return;
        }

        var title = LanguageService.Instance.GetString("Device_RebootConfirmTitle");
        var message = string.Format(LanguageService.Instance.GetString("Device_RebootConfirmMessage"), e.ModeDisplayName);
        e.Confirmation.TrySetResult(await ShowConfirmDialogAsync(owner, title, message));
    }

    private async void OnFlashConfirmationRequested(object? sender, (string Title, string Message, TaskCompletionSource<bool> Confirmation) e)
    {
        var owner = this.FindAncestorOfType<Window>();
        if (owner is null)
        {
            e.Confirmation.TrySetResult(false);
            return;
        }

        e.Confirmation.TrySetResult(await ShowConfirmDialogAsync(owner, e.Title, e.Message));
    }

    /// <summary>
    /// Shows a modal yes/no confirmation dialog bound to the given owner window.
    /// <para>以给定窗口为所有者弹出是/否确认对话框。</para>
    /// </summary>
    private static async Task<bool> ShowConfirmDialogAsync(Window owner, string title, string message)
    {
        var yes = LanguageService.Instance.GetString("Editor_Yes");
        var cancel = LanguageService.Instance.GetString("Editor_Cancel");

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = yes, Tag = true },
                            new Button { Content = cancel, Tag = false }
                        }
                    }
                }
            }
        };

        var result = false;
        foreach (var btn in ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children)
        {
            if (btn is Button b)
            {
                b.Click += (_, _) =>
                {
                    result = (bool)(b.Tag ?? false);
                    dialog.Close();
                };
            }
        }

        await dialog.ShowDialog(owner);
        return result;
    }

    // ===== 设备条目右键菜单 / Device item context menu =====

    /// <summary>
    /// Gets the device behind a context-menu item and selects it in the view model.
    /// <para>取出右键菜单项对应的设备并在视图模型中选中它。</para>
    /// </summary>
    private DeviceInfo? SelectMenuDevice(object? sender)
    {
        if (sender is not MenuItem { DataContext: DeviceInfo device })
        {
            return null;
        }

        if (DataContext is DeviceViewModel vm)
        {
            vm.SelectedDevice = device;
        }

        return device;
    }

    /// <summary><para>连接右键菜单设备。</para>Connect the device from the context menu.</summary>
    private void OnConnectMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuDevice(sender) is not null && DataContext is DeviceViewModel vm)
        {
            vm.ConnectCommand.Execute(null);
        }
    }

    /// <summary><para>断开右键菜单设备。</para>Disconnect the device from the context menu.</summary>
    private void OnDisconnectMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuDevice(sender) is not null && DataContext is DeviceViewModel vm)
        {
            vm.DisconnectCommand.Execute(null);
        }
    }

    /// <summary><para>将右键菜单设备重启到指定模式。</para>Reboot the context-menu device to the given mode.</summary>
    private void OnRebootMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuDevice(sender) is not null && DataContext is DeviceViewModel vm
            && sender is MenuItem { CommandParameter: string mode })
        {
            vm.RebootCommand.Execute(mode);
        }
    }

    /// <summary><para>为右键菜单设备打开 ADB Shell。</para>Open an ADB shell for the context-menu device.</summary>
    private void OnOpenAdbShellMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuDevice(sender) is not null && DataContext is DeviceViewModel vm)
        {
            vm.OpenAdbShellCommand.Execute(null);
        }
    }

    /// <summary><para>读取右键菜单 Fastboot 设备信息。</para>Read Fastboot info for the context-menu device.</summary>
    private void OnFastbootReadInfoMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuDevice(sender) is not null && DataContext is DeviceViewModel vm)
        {
            vm.FastbootReadInfoCommand.Execute(null);
        }
    }

    /// <summary><para>对右键菜单设备执行快速刷写（boot 分区）。</para>Quick-flash the context-menu device (boot partition).</summary>
    private void OnQuickFlashMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuDevice(sender) is not null && DataContext is DeviceViewModel vm)
        {
            vm.QuickFlashCommand.Execute("boot");
        }
    }
}