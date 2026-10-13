using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using Ursa.Controls;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Settings page view, providing the interactive interface for application settings.
/// <para>设置页面视图，提供应用程序设置的交互界面。</para>
/// </summary>
public partial class SettingsView : UserControl
{
    private WindowToastManager? _toastManager;

    /// <summary>
    /// Initializes a new instance of the SettingsView.
    /// <para>初始化设置页面视图的新实例。</para>
    /// </summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WeakReferenceMessenger.Default.Register<SettingsView, SettingsSaveCompletedMessage>(this,
            static (view, message) => Dispatcher.UIThread.Post(() => view.ShowSaveResult(message)));

        if (DataContext is ViewModels.Shell.SettingsViewModel viewModel)
        {
            viewModel.RefreshCommunicationStatusCommand.Execute(null);
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<SettingsSaveCompletedMessage>(this);
        _toastManager?.CloseAll();
        _toastManager = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ShowSaveResult(SettingsSaveCompletedMessage message)
    {
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } owner) return;
        if (_toastManager is null)
        {
            _toastManager = WindowToastManager.TryGetToastManager(owner, out var existing)
                ? existing
                : new WindowToastManager(owner) { MaxItems = 1 };
        }

        var content = message.Succeeded
            ? LanguageService.Instance.GetString("Settings_SettingsSaved")
            : string.Format(LanguageService.Instance.GetString("Settings_SaveFailed"), message.Error);
        _toastManager?.Show(content,
            message.Succeeded ? NotificationType.Success : NotificationType.Error,
            TimeSpan.FromSeconds(message.Succeeded ? 2 : 6), showIcon: true, showClose: !message.Succeeded);
    }

    /// <summary>
    /// Opens the link carried by the button (developer GitHub / Coolapk profiles) in the default browser.
    /// <para>用系统默认浏览器打开按钮携带的链接（开发成员的 GitHub / 酷安主页）。</para>
    /// </summary>
    /// <param name="sender">The event sender. <para>事件发送者。</para></param>
    /// <param name="e">The event arguments. <para>事件参数。</para></param>
    private void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            ExternalLinkService.TryOpen(url);
        }
    }
}