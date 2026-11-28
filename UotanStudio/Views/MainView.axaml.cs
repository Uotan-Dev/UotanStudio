using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Delivery;

namespace UotanStudio.Views;

public partial class MainView : UserControl
{
    private IReadOnlyDictionary<ViewModelViewRegistry.TopLevelPage, Control>? _prebuiltPages;
    private bool _pagesPrebuilt;

    public MainView()
    {
        InitializeComponent();

        SelectPage(WorkspaceTab);
        MainTabs.PropertyChanged += (_, change) =>
        {
            if (change.Property == SelectingMultiPage.SelectedIndexProperty)
            {
                EnsureSelectedPage();
            }
        };
        MainTabs.SelectionChanged += OnSelectionChanged;

        SendActiveTabChanged(MainTabs.SelectedPage);
        Dispatcher.UIThread.Post(EnsureSelectedPage, DispatcherPriority.Background);
    }

    /// <summary>
    /// Installs pages created earlier on the UI thread (during the splash sequence) so the first switch to any
    /// bottom tab only has to realize an existing control instead of parsing XAML and constructing it. Safe to
    /// call more than once; later calls are ignored.
    /// <para>装上此前在 UI 线程（启动窗阶段）预先创建的页面，使首次切换到任意底栏页签只需呈现已有控件，
    /// 不再解析 XAML 与构造控件。可重复调用，后续调用会被忽略。</para>
    /// </summary>
    /// <param name="pages">The pre-created controls keyed by tab. <para>按页签索引的预创建控件。</para></param>
    internal void InstallPrebuiltPages(IReadOnlyDictionary<ViewModelViewRegistry.TopLevelPage, Control> pages)
    {
        if (_pagesPrebuilt)
        {
            return;
        }
        _pagesPrebuilt = true;
        _prebuiltPages = pages;
        FlashTab.Content = TakePrebuilt(ViewModelViewRegistry.TopLevelPage.Flash);
        DeliveryTab.Content = TakePrebuilt(ViewModelViewRegistry.TopLevelPage.Delivery);
        NodeEditorTab.Content = TakePrebuilt(ViewModelViewRegistry.TopLevelPage.NodeEditor);
        SettingsTab.Content = TakePrebuilt(ViewModelViewRegistry.TopLevelPage.Settings);
    }

    private Control TakePrebuilt(ViewModelViewRegistry.TopLevelPage page)
    {
        if (_prebuiltPages is not null && _prebuiltPages.TryGetValue(page, out var control))
        {
            return control;
        }
        return ViewModelViewRegistry.CreateTopLevelPage(page);
    }

    private Control CreatePage(ContentPage page)
    {
        if (ReferenceEquals(page, FlashTab))
        {
            return TakePrebuilt(ViewModelViewRegistry.TopLevelPage.Flash);
        }

        if (ReferenceEquals(page, DeliveryTab))
        {
            return TakePrebuilt(ViewModelViewRegistry.TopLevelPage.Delivery);
        }

        if (ReferenceEquals(page, NodeEditorTab))
        {
            return TakePrebuilt(ViewModelViewRegistry.TopLevelPage.NodeEditor);
        }

        if (ReferenceEquals(page, SettingsTab))
        {
            return TakePrebuilt(ViewModelViewRegistry.TopLevelPage.Settings);
        }

        throw new ArgumentOutOfRangeException(nameof(page));
    }

    private void EnsureSelectedPage()
    {
        if (MainTabs.SelectedPage is ContentPage { Content: null } page && IsBottomPage(page))
        {
            page.Content = CreatePage(page);
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
            var page = message.Target switch
            {
                NavigationTarget.Workspace => WorkspaceTab,
                NavigationTarget.Flash => FlashTab,
                NavigationTarget.Delivery => DeliveryTab,
                NavigationTarget.NodeEditor => NodeEditorTab,
                NavigationTarget.Settings => SettingsTab,
                _ => MainTabs.SelectedPage
            };
            if (ReferenceEquals(MainTabs.SelectedPage, page) && message.Target == NavigationTarget.Delivery)
            {
                RefreshDelivery();
            }
            else if (page is not null)
            {
                SelectPage(page);
            }
        });
    }

    /// <summary>
    /// TabbedPage switch event: maps the newly active page to <see cref="ActiveTab"/> and broadcasts it so menu commands can re-evaluate availability.
    /// <para>TabbedPage 切换事件：将新激活页面映射为 <see cref="ActiveTab"/> 并广播，使菜单命令据此刻画可用性。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件源。</para></param>
    /// <param name="e">The selection changed args. <para>切换事件参数。</para></param>
    private void OnSelectionChanged(object? sender, PageSelectionChangedEventArgs e)
    {
        EnsureSelectedPage();
        SendActiveTabChanged(MainTabs.SelectedPage);
        if (ReferenceEquals(MainTabs.SelectedPage, DeliveryTab))
        {
            RefreshDelivery();
        }
    }

    private void SendActiveTabChanged(Page? selectedPage)
    {
        var tab = selectedPage switch
        {
            _ when ReferenceEquals(selectedPage, FlashTab) => ActiveTab.Flash,
            _ when ReferenceEquals(selectedPage, DeliveryTab) => ActiveTab.Delivery,
            _ when ReferenceEquals(selectedPage, NodeEditorTab) => ActiveTab.NodeEditor,
            _ when ReferenceEquals(selectedPage, SettingsTab) => ActiveTab.Settings,
            _ => ActiveTab.Workspace
        };
        WeakReferenceMessenger.Default.Send(new ActiveTabChangedMessage(tab));
    }

    private void SelectPage(Page page)
    {
        if (MainTabs.Pages is IList<Page> pages)
        {
            var index = pages.IndexOf(page);
            if (index >= 0)
            {
                MainTabs.SelectedIndex = index;
            }
        }
    }

    private bool IsBottomPage(Page page) =>
        ReferenceEquals(page, WorkspaceTab) ||
        ReferenceEquals(page, FlashTab) ||
        ReferenceEquals(page, NodeEditorTab) ||
        ReferenceEquals(page, DeliveryTab) ||
        ReferenceEquals(page, SettingsTab);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WeakReferenceMessenger.Default.Register<NavigateToTabMessage>(this, OnNavigateToTab);
        WeakReferenceMessenger.Default.Register<MainView, OpenDocumentSearchMessage>(this, static (view, message) =>
        {
            view.SelectPage(view.WorkspaceTab);

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