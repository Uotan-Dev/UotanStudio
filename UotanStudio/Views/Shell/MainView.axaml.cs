using UotanStudio.ViewModels.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.ViewModels;

namespace UotanStudio.Views.Shell;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        // 默认选中 Workspace Tab（移除设备/日志底栏后：Workspace=3, NodeEditor=4, Settings=5）
        MainTabs.SelectedIndex = 3;

        WeakReferenceMessenger.Default.Register<NavigateToTabMessage>(this, OnNavigateToTab);
        MainTabs.SelectionChanged += OnSelectionChanged;
        // 初始同步：告知订阅者当前激活面板为 Workspace（与默认 SelectedIndex=4 对应）
        SendActiveTabChanged(MainTabs.SelectedIndex);
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
                _ => MainTabs.SelectedIndex
            };
            MainTabs.SelectedIndex = index;
        });
    }

    /// <summary>
    /// <para>TabbedPage 切换事件：将新激活的 SelectedIndex 映射为 <see cref="ActiveTab"/> 并广播，使菜单命令据此刻画可用性。</para>
    /// TabbedPage switch event: maps the newly active SelectedIndex to <see cref="ActiveTab"/> and broadcasts it so menu commands can re-evaluate availability.
    /// </summary>
    /// <param name="sender"><para>事件源。</para>Event source.</param>
    /// <param name="e"><para>切换事件参数。</para>The selection changed args.</param>
    private void OnSelectionChanged(object? sender, PageSelectionChangedEventArgs e)
    {
        SendActiveTabChanged(MainTabs.SelectedIndex);
    }

    /// <summary>
    /// <para>将 TabbedPage 的 SelectedIndex 映射为 <see cref="ActiveTab"/> 并发送 <see cref="ActiveTabChangedMessage"/>。</para>
    /// Maps the TabbedPage's SelectedIndex to <see cref="ActiveTab"/> and sends an <see cref="ActiveTabChangedMessage"/>.
    /// </summary>
    /// <param name="selectedIndex"><para>当前激活的底栏 Tab 索引。</para>The currently active bottom tab index.</param>
    private static void SendActiveTabChanged(int selectedIndex)
    {
        var tab = selectedIndex switch
        {
            3 => ActiveTab.Workspace,
            4 => ActiveTab.NodeEditor,
            5 => ActiveTab.Settings,
            _ => ActiveTab.Workspace
        };
        WeakReferenceMessenger.Default.Send(new ActiveTabChangedMessage(tab));
    }

    /// <summary>
    /// <para>视图从可视化树分离时取消消息订阅与事件挂载，防止内存泄漏。</para>
    /// Unregisters message subscriptions and event handlers when the view detaches from the visual tree.
    /// </summary>
    /// <param name="e"><para>分离事件参数。</para>The detach event args.</param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<NavigateToTabMessage>(this);
        MainTabs.SelectionChanged -= OnSelectionChanged;
        base.OnDetachedFromVisualTree(e);
    }
}