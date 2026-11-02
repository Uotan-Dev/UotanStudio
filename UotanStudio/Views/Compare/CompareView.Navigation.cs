using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using Ursa.Controls;

namespace UotanStudio.Views.Compare;

public partial class CompareView
{
    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WeakReferenceMessenger.Default.Register<CompareView, CompareNavigationNoticeMessage>(this,
            static (view, message) => Dispatcher.UIThread.Post(() => view.ShowNavigationNotice(message)));
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<CompareNavigationNoticeMessage>(this);
        base.OnDetachedFromVisualTree(e);
    }

    private void ShowNavigationNotice(CompareNavigationNoticeMessage message)
    {
        if (!ReferenceEquals(DataContext, message.Source) || !IsEffectivelyVisible
            || TopLevel.GetTopLevel(this) is not { } owner)
        {
            return;
        }
        var manager = WindowToastManager.TryGetToastManager(owner, out var existing)
            ? existing : new WindowToastManager(owner) { MaxItems = 1 };
        manager?.Show(message.Text, NotificationType.Information,
            TimeSpan.FromSeconds(2), showIcon: true, showClose: false);
    }
}