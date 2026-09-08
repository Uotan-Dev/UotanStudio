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
/// <para>设置页面视图，提供应用程序设置的交互界面。</para>
/// Settings page view, providing the interactive interface for application settings.
/// </summary>
public partial class SettingsView : UserControl
{
    private WindowToastManager? _toastManager;

    /// <summary>
    /// <para>初始化设置页面视图的新实例。</para>
    /// Initializes a new instance of the SettingsView.
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
    /// <para>在系统默认浏览器中打开链接（开源致谢区块的仓库/作者链接按钮）。</para>
    /// Opens a link in the system default browser (repository/author link buttons in the
    /// open-source credits section).
    /// </summary>
    /// <param name="sender"><para>事件发送者。</para>The event sender.</param>
    /// <param name="e"><para>事件参数。</para>The event arguments.</param>
    private void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = uri.ToString(),
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (System.Exception)
            {
                // 打开浏览器失败时静默忽略，避免打断设置页操作。
                // Ignore failures opening the browser silently so the settings page keeps working.
            }
        }
    }
}
