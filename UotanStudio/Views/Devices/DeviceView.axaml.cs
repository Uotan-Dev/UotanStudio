using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
        e.Confirmation.TrySetResult(result);
    }
}