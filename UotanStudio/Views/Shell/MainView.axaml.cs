using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Nodes;

namespace UotanStudio.Views.Shell;

public partial class MainView : UserControl
{
    // 底栏页面索引：前三个与最后三个 ContentPage 是撑开居中留白的占位页。
    // Bottom-bar page indexes: the first and last three ContentPages are spacers that keep the group centred.
    private const int FlashTabIndex = 3;
    private const int WorkspaceTabIndex = 4;
    private const int DeliveryTabIndex = 5;
    private const int NodeEditorTabIndex = 6;
    private const int SettingsTabIndex = 7;

    private readonly Func<int, Control> _createPage;

    public MainView() : this(CreatePage) { }

    internal MainView(Func<int, Control> createPage)
    {
        _createPage = createPage;
        InitializeComponent();
        // 默认选中 Workspace Tab（Flash=3, Workspace=4, Delivery=5, NodeEditor=6, Settings=7）。
        MainTabs.SelectedIndex = WorkspaceTabIndex;
        MainTabs.PropertyChanged += (_, change) =>
        {
            if (change.Property == TabbedPage.SelectedIndexProperty)
            {
                EnsureSelectedPage();
            }
        };
        MainTabs.SelectionChanged += OnSelectionChanged;

        // 初始同步：告知订阅者当前激活面板为 Workspace（与默认 SelectedIndex 对应）
        SendActiveTabChanged(MainTabs.SelectedIndex);
    }

    private static Control CreatePage(int index) => index switch
    {
        FlashTabIndex => new FlashView(),
        DeliveryTabIndex => new DeliveryView(),
        NodeEditorTabIndex => new NodeEditorView(),
        SettingsTabIndex => new SettingsView(),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private void EnsureSelectedPage()
    {
        var page = MainTabs.SelectedIndex switch
        {
            FlashTabIndex => FlashTab,
            DeliveryTabIndex => DeliveryTab,
            NodeEditorTabIndex => NodeEditorTab,
            SettingsTabIndex => SettingsTab,
            _ => null
        };
        if (page is not null && page.Content is null)
        {
            page.Content = _createPage(MainTabs.SelectedIndex);
        }
    }

    private void RefreshDelivery()
    {
        EnsureSelectedPage();
        if (DeliveryTab.Content is DeliveryView delivery)
        {
            delivery.Refresh();
        }
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
                NavigationTarget.Workspace => WorkspaceTabIndex,
                NavigationTarget.Delivery => DeliveryTabIndex,
                NavigationTarget.NodeEditor => NodeEditorTabIndex,
                NavigationTarget.Settings => SettingsTabIndex,
                _ => MainTabs.SelectedIndex
            };
            if (MainTabs.SelectedIndex == index && message.Target == NavigationTarget.Delivery)
                RefreshDelivery();
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
        EnsureSelectedPage();
        SendActiveTabChanged(MainTabs.SelectedIndex);
        if (MainTabs.SelectedIndex == DeliveryTabIndex)
        {
            RefreshDelivery();
        }
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
            FlashTabIndex => ActiveTab.Flash,
            WorkspaceTabIndex => ActiveTab.Workspace,
            DeliveryTabIndex => ActiveTab.Delivery,
            NodeEditorTabIndex => ActiveTab.NodeEditor,
            SettingsTabIndex => ActiveTab.Settings,
            _ => ActiveTab.Workspace
        };
        WeakReferenceMessenger.Default.Send(new ActiveTabChangedMessage(tab));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WeakReferenceMessenger.Default.Register<NavigateToTabMessage>(this, OnNavigateToTab);
        WeakReferenceMessenger.Default.Register<MainView, OpenDocumentSearchMessage>(this, static (view, message) =>
        {
            view.MainTabs.SelectedIndex = WorkspaceTabIndex;
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
        base.OnDetachedFromVisualTree(e);
    }
}