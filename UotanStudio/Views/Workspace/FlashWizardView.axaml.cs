using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// <para>一键刷写向导窗口：承载 <see cref="FlashWizardViewModel"/>，负责刷写确认对话框
/// （经 <see cref="FlashWizardConfirmRequestMessage"/>）与关闭处理。</para>
/// One-click flash wizard window: hosts the <see cref="FlashWizardViewModel"/>, handles the
/// flash confirmation dialog (via <see cref="FlashWizardConfirmRequestMessage"/>) and closing.
/// </summary>
public partial class FlashWizardView : Window
{
    /// <summary>
    /// <para>创建向导窗口并绑定新的向导视图模型；注册刷写确认消息接收者，关闭时释放。</para>
    /// Creates the wizard window with a new wizard view model; registers the flash-confirmation
    /// recipient and disposes the view model on close.
    /// </summary>
    public FlashWizardView()
    {
        InitializeComponent();
        DataContext = new FlashWizardViewModel();
        WeakReferenceMessenger.Default.Register<FlashWizardConfirmRequestMessage>(this, OnConfirmRequested);
        Closed += (_, _) =>
        {
            WeakReferenceMessenger.Default.Unregister<FlashWizardConfirmRequestMessage>(this);
            (DataContext as FlashWizardViewModel)?.Dispose();
        };
    }

    /// <summary>
    /// <para>响应刷写确认请求：弹出是/否确认对话框并把结果写回消息中的 TaskCompletionSource。</para>
    /// Responds to a flash-confirmation request: shows a yes/no dialog and writes the result back
    /// to the TaskCompletionSource carried by the message.
    /// </summary>
    private async void OnConfirmRequested(object recipient, FlashWizardConfirmRequestMessage message)
    {
        message.Confirmation.TrySetResult(await ShowConfirmDialogAsync(this, message.Title, message.Message));
    }

    /// <summary>
    /// <para>以向导窗口为所有者弹出是/否确认对话框。</para>
    /// Shows a modal yes/no confirmation dialog owned by the wizard window.
    /// </summary>
    private static async Task<bool> ShowConfirmDialogAsync(Window owner, string title, string message)
    {
        var yes = LanguageService.Instance.GetString("Editor_Yes");
        var cancel = LanguageService.Instance.GetString("Editor_Cancel");

        var dialog = new Window
        {
            Title = title,
            Width = 420,
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

    /// <summary><para>关闭向导窗口。</para>Closes the wizard window.</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}