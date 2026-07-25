using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.ViewModels;

namespace UotanStudio.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        MainTabs.SelectedIndex = 3;

        WeakReferenceMessenger.Default.Register<NavigateToTabMessage>(this, OnNavigateToTab);
    }

    /// <summary>
    /// <para>响应导航消息，切换到对应的主选项卡。</para>
    /// Responds to navigation messages by switching to the corresponding main tab.
    /// </summary>
    /// <param name="recipient"><para>消息接收者。</para>The message recipient.</param>
    /// <param name="message"><para>包含导航目标的消息。</para>The message containing the navigation target.</param>
    private void OnNavigateToTab(object recipient, NavigateToTabMessage message)
    {
        Dispatcher.Post(() =>
        {
            var index = message.Target switch
            {
                NavigationTarget.Workspace => 3,
                NavigationTarget.NodeEditor => 4,
                NavigationTarget.Settings => 5,
                // Device 与 Plugin 已移至顶栏对话框入口，不再对应底栏 Tab
                NavigationTarget.DeviceManager or NavigationTarget.PluginManager => MainTabs.SelectedIndex,
                _ => MainTabs.SelectedIndex
            };
            MainTabs.SelectedIndex = index;
        });
    }

    /// <summary>
    /// <para>视图从可视化树分离时取消消息订阅，防止内存泄漏。</para>
    /// Unregisters message subscriptions when the view detaches from the visual tree.
    /// </summary>
    /// <param name="e"><para>分离事件参数。</para>The detach event args.</param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<NavigateToTabMessage>(this);
        base.OnDetachedFromVisualTree(e);
    }
}
