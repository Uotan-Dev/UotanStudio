using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        // 默认选中 Workspace Tab（Workspace=3, Delivery=4, NodeEditor=5, Settings=6）。
        MainTabs.SelectedIndex = 3;

        // 初始同步：告知订阅者当前激活面板为 Workspace（与默认 SelectedIndex=3 对应）
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
                NavigationTarget.Delivery => 4,
                NavigationTarget.NodeEditor => 5,
                NavigationTarget.Settings => 6,
                _ => MainTabs.SelectedIndex
            };
            if (MainTabs.SelectedIndex == index && message.Target == NavigationTarget.Delivery)
                DeliveryPage.Refresh();
            else MainTabs.SelectedIndex = index;
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
        if (MainTabs.SelectedIndex == 4) DeliveryPage.Refresh();
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
            4 => ActiveTab.Delivery,
            5 => ActiveTab.NodeEditor,
            6 => ActiveTab.Settings,
            _ => ActiveTab.Workspace
        };
        WeakReferenceMessenger.Default.Send(new ActiveTabChangedMessage(tab));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WeakReferenceMessenger.Default.Register<NavigateToTabMessage>(this, OnNavigateToTab);
        MainTabs.SelectionChanged += OnSelectionChanged;
        WeakReferenceMessenger.Default.Register<MainView, OpenDocumentSearchMessage>(this, static (view, message) =>
        {
            view.MainTabs.SelectedIndex = 3;
            // Wait for the selected page's document dock to complete layout before positioning search.
            Dispatcher.UIThread.Post(() => view.WorkspaceDocuments.ShowDocumentSearch(message.ShowReplace), DispatcherPriority.Loaded);
        });
    }

    /// <summary>
    /// <para>视图从可视化树分离时取消消息订阅与事件挂载，防止内存泄漏。</para>
    /// Unregisters message subscriptions and event handlers when the view detaches from the visual tree.
    /// </summary>
    /// <param name="e"><para>分离事件参数。</para>The detach event args.</param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<NavigateToTabMessage>(this);
        WeakReferenceMessenger.Default.Unregister<OpenDocumentSearchMessage>(this);
        MainTabs.SelectionChanged -= OnSelectionChanged;
        base.OnDetachedFromVisualTree(e);
    }
}