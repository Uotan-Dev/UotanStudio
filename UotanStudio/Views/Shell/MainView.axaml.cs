using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

public partial class MainView : UserControl
{

    private const int WorkspaceTabIndex = 3;
    private const int FlashTabIndex = 4;
    private const int NodeEditorTabIndex = 5;
    private const int DeliveryTabIndex = 6;
    private const int SettingsTabIndex = 7;

    private readonly Func<int, Control> _createPage;

    public MainView() : this(CreatePage) { }

    internal MainView(Func<int, Control> createPage)
    {
        _createPage = createPage;
        InitializeComponent();

        MainTabs.SelectedIndex = WorkspaceTabIndex;
        MainTabs.PropertyChanged += (_, change) =>
        {
            if (change.Property == SelectingMultiPage.SelectedIndexProperty)
            {
                EnsureSelectedPage();
            }
        };
        MainTabs.SelectionChanged += OnSelectionChanged;

        SendActiveTabChanged(MainTabs.SelectedIndex);
    }

    private static Control CreatePage(int index) => index switch
    {
        FlashTabIndex => ViewModelViewRegistry.CreateTopLevelPage(ViewModelViewRegistry.TopLevelPage.Flash),
        DeliveryTabIndex => ViewModelViewRegistry.CreateTopLevelPage(ViewModelViewRegistry.TopLevelPage.Delivery),
        NodeEditorTabIndex => ViewModelViewRegistry.CreateTopLevelPage(ViewModelViewRegistry.TopLevelPage.NodeEditor),
        SettingsTabIndex => ViewModelViewRegistry.CreateTopLevelPage(ViewModelViewRegistry.TopLevelPage.Settings),
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
    /// Responds to navigation messages by switching to the corresponding main tab.
    /// <para>响应导航消息，切换到对应的主选项卡。</para>
    /// </summary>
    /// <param name="recipient">The message recipient. <para>消息接收者。</para></param>
    /// <param name="message">The message containing the navigation target. <para>包含导航目标的消息。</para></param>
    private void OnNavigateToTab(object recipient, NavigateToTabMessage message)
    {
        Dispatcher.Post(() =>
        {
            var index = message.Target switch
            {
                NavigationTarget.Workspace => WorkspaceTabIndex,
                NavigationTarget.Flash => FlashTabIndex,
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
    /// TabbedPage switch event: maps the newly active SelectedIndex to <see cref="ActiveTab"/> and broadcasts it so menu commands can re-evaluate availability.
    /// <para>TabbedPage 切换事件：将新激活的 SelectedIndex 映射为 <see cref="ActiveTab"/> 并广播，使菜单命令据此刻画可用性。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件源。</para></param>
    /// <param name="e">The selection changed args. <para>切换事件参数。</para></param>
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
    /// Maps the TabbedPage's SelectedIndex to <see cref="ActiveTab"/> and sends an <see cref="ActiveTabChangedMessage"/>.
    /// <para>将 TabbedPage 的 SelectedIndex 映射为 <see cref="ActiveTab"/> 并发送 <see cref="ActiveTabChangedMessage"/>。</para>
    /// </summary>
    /// <param name="selectedIndex">The currently active bottom tab index. <para>当前激活的底栏 Tab 索引。</para></param>
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

            Dispatcher.UIThread.Post(() => view.WorkspaceDocuments.ShowDocumentSearch(message.ShowReplace), DispatcherPriority.Loaded);
        });
    }

    /// <summary>
    /// Unregisters message subscriptions and event handlers when the view detaches from the visual tree.
    /// <para>视图从可视化树分离时取消消息订阅与事件挂载，防止内存泄漏。</para>
    /// </summary>
    /// <param name="e">The detach event args. <para>分离事件参数。</para></param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<NavigateToTabMessage>(this);
        WeakReferenceMessenger.Default.Unregister<OpenDocumentSearchMessage>(this);
        base.OnDetachedFromVisualTree(e);
    }
}