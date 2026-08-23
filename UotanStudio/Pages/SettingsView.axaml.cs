using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
                {
                    b.Click += (_, _) => { ok = (bool)(b.Tag ?? false); dialog.Close(); };
                }
            }

            await dialog.ShowDialog(owner);
            tcs.TrySetResult(ok);
        }
        catch
        {
            tcs.TrySetResult(false);
        }
    }
}