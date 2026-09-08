using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Ursa.Controls;

namespace UotanStudio.Services;

/// <summary>
/// Shows localized Ursa message windows without an overlay or owner mask.
/// </summary>
internal static class DialogService
{
    internal static Task<MessageBoxResult> ShowAsync(
        Window owner, string title, string message, MessageBoxIcon icon) =>
        CreateWindow(title, message, icon, MessageBoxButton.OK)
            .ShowDialog<MessageBoxResult>(owner);

    internal static async Task<bool> ConfirmAsync(
        Window owner, string title, string message, string? confirmText = null, string? cancelText = null)
    {
        var dialog = CreateWindow(title, message, MessageBoxIcon.Warning,
            MessageBoxButton.OKCancel, confirmText, cancelText);
        return await dialog.ShowDialog<MessageBoxResult>(owner) == MessageBoxResult.OK;
    }

    internal static async Task<string?> InputAsync(
        Window owner, string title, string defaultText = "")
    {
        var input = new TextBox { Text = defaultText, MinWidth = 240 };
        var dialog = CreateWindow(title, input, MessageBoxIcon.Information, MessageBoxButton.OKCancel);
        // Ursa initially focuses Cancel; move focus after its Loaded handler has run.
        dialog.Loaded += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!dialog.IsVisible) return;
            input.Focus();
            input.SelectAll();
        }, DispatcherPriority.Input);
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (!string.IsNullOrWhiteSpace(input.Text)) dialog.Close(MessageBoxResult.OK);
            e.Handled = true;
        };

        var result = await dialog.ShowDialog<MessageBoxResult>(owner);
        return result == MessageBoxResult.OK && !string.IsNullOrWhiteSpace(input.Text) ? input.Text : null;
    }

    private static MessageBoxWindow CreateWindow(
        string title, object content, MessageBoxIcon icon, MessageBoxButton buttons,
        string? confirmText = null, string? cancelText = null)
    {
        var dialog = new MessageBoxWindow(buttons)
        {
            Title = title,
            Content = content,
            MessageIcon = icon,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        dialog.Resources["STRING_MENU_DIALOG_OK"] = confirmText ?? LanguageService.Instance.GetString("Dialog_OK");
        dialog.Resources["STRING_MENU_DIALOG_CANCEL"] = cancelText ?? LanguageService.Instance.GetString("Editor_Cancel");
        return dialog;
    }
}
