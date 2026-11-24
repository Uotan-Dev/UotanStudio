using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Models;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Behaviors;

/// <summary>
/// Applies the view model-managed bottom-bar order and visibility to a <see cref="TabbedPage"/> without putting that state in code-behind.
/// <para>把视图模型管理的底栏顺序与可见性应用到 <see cref="TabbedPage"/>，避免把状态逻辑写进 code-behind。</para>
/// </summary>
public static partial class BottomBarDisplayBehavior
{
    private static readonly ConditionalWeakTable<TabbedPage, State> States = new();

    static BottomBarDisplayBehavior() =>
        DisplayProperty.Changed.AddClassHandler<TabbedPage>(OnDisplayChanged);

    /// <summary>Gets the ordered bottom-bar display source.<para>获取有序底栏显示数据源。</para></summary>
    /// <param name="page">The target tabbed page.<para>目标分页控件。</para></param>
    /// <returns>The ordered display items.<para>有序的显示项。</para></returns>
    [GeneratedAttachedProperty]
    public static partial IEnumerable<DisplayPreferenceItem>? GetDisplay(TabbedPage page);

    private static void OnDisplayChanged(TabbedPage page, AvaloniaPropertyChangedEventArgs args) =>
        States.GetValue(page, static owner => new State(owner))
            .SetSource(args.GetNewValue<IEnumerable<DisplayPreferenceItem>?>());

    private sealed class State
    {
        private const double TabWidth = 132;
        private const double TabSpacing = 4;

        private readonly TabbedPage _owner;
        private readonly List<DisplayPreferenceItem> _items = [];
        private Page[] _managedPages = [];
        private INotifyCollectionChanged? _collection;
        private TabControl? _tabControl;
        private Control? _tabStrip;
        private bool _applying;
        private bool _recentering;
        private bool _recenterPosted;

        internal State(TabbedPage owner)
        {
            _owner = owner;
            CaptureManagedPages();
            _owner.AttachedToVisualTree += OnAttachedToVisualTree;
            _owner.DetachedFromVisualTree += OnDetachedFromVisualTree;
        }

        internal void SetSource(IEnumerable<DisplayPreferenceItem>? source)
        {
            if (_collection is not null)
            {
                _collection.CollectionChanged -= OnCollectionChanged;
            }

            _collection = source as INotifyCollectionChanged;
            if (_collection is not null)
            {
                _collection.CollectionChanged += OnCollectionChanged;
            }

            RefreshItems(source);
            Apply(chooseInitialPage: true);
        }

        private void CaptureManagedPages()
        {
            var pages = _owner.Pages?.ToArray() ?? [];
            _managedPages = [.. pages.Where(page => KeyForPage(page) is not null)];
        }

        private void RefreshItems(IEnumerable<DisplayPreferenceItem>? source)
        {
            foreach (var item in _items)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }

            _items.Clear();
            if (source is not null)
            {
                _items.AddRange(source);
            }

            foreach (var item in _items)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshItems(_owner.GetValue(DisplayProperty));
            Apply(chooseInitialPage: false);
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DisplayPreferenceItem.IsVisible))
            {
                Apply(chooseInitialPage: false);
            }
        }

        private void Apply(bool chooseInitialPage)
        {
            if (_applying || _items.Count == 0)
            {
                return;
            }

            var selectedPage = _owner.SelectedPage;
            var orderedPages = new List<Page>(_items.Count);
            foreach (var item in _items)
            {
                if (PageForKey(item.Key) is { } page)
                {
                    orderedPages.Add(page);
                }
            }

            _applying = true;
            try
            {
                if (_owner.Pages is not AvaloniaList<Page> pages)
                {
                    return;
                }

                var orderChanged = orderedPages
                    .Select((page, index) => (Page: page, Index: index))
                    .Any(entry => pages.IndexOf(entry.Page) != entry.Index);
                if (orderChanged)
                {
                    ReleaseTabItemMetadata();
                }

                for (var index = 0; index < orderedPages.Count; index++)
                {
                    var current = pages.IndexOf(orderedPages[index]);
                    if (current >= 0 && current != index)
                    {
                        pages.Move(current, index);
                    }
                }

                if (orderChanged)
                {
                    RestoreTabItemMetadata();
                }

                if (chooseInitialPage)
                {
                    var selectedIsVisible = selectedPage is not null && _items.Any(item =>
                        item.IsVisible && ReferenceEquals(PageForKey(item.Key), selectedPage));
                    if (!selectedIsVisible)
                    {
                        var initial = _items.FirstOrDefault(item => item.IsVisible) ?? _items.FirstOrDefault();
                        selectedPage = initial is null ? null : PageForKey(initial.Key);
                    }
                }

                if (selectedPage is not null && orderedPages.Contains(selectedPage))
                {
                    SelectPage(selectedPage);
                }

                ApplyContainerVisibility();
            }
            finally
            {
                _applying = false;
            }

            Dispatcher.UIThread.Post(AttachTabControl, DispatcherPriority.Loaded);
        }

        private void ReleaseTabItemMetadata()
        {
            if (_tabControl is null)
            {
                return;
            }

            for (var index = 0; index < _tabControl.ItemCount; index++)
            {
                if (_tabControl.ContainerFromIndex(index) is TabItem tabItem)
                {
                    tabItem.Header = null;
                    tabItem.Icon = null;
                    tabItem.IconTemplate = null;
                }
            }
        }

        private void RestoreTabItemMetadata()
        {
            if (_tabControl is null)
            {
                return;
            }

            for (var index = 0; index < _tabControl.ItemCount; index++)
            {
                if (_tabControl.ContainerFromIndex(index) is TabItem { Content: Page page } tabItem)
                {
                    tabItem.IsEnabled = TabbedPage.GetIsTabEnabled(page);
                    tabItem.Header = page.Header;
                    tabItem.Icon = page.Icon;
                    tabItem.IconTemplate = page.IconTemplate;
                }
            }
        }

        private void SelectPage(Page page)
        {
            if (_owner.Pages is IList<Page> pages)
            {
                var index = pages.IndexOf(page);
                if (index >= 0)
                {
                    _owner.SelectedIndex = index;
                }
            }
        }

        private Page? PageForKey(string key) =>
            _managedPages.FirstOrDefault(page =>
                string.Equals(page.Name, ItemPageName(key), StringComparison.Ordinal));

        private static string ItemPageName(string key) => key switch
        {
            BottomBarCatalog.Workspace => "WorkspaceTab",
            BottomBarCatalog.Flash => "FlashTab",
            BottomBarCatalog.Node => "NodeEditorTab",
            BottomBarCatalog.Delivery => "DeliveryTab",
            BottomBarCatalog.Settings => "SettingsTab",
            _ => string.Empty
        };

        private static string? KeyForPage(Page page) => page.Name switch
        {
            "WorkspaceTab" => BottomBarCatalog.Workspace,
            "FlashTab" => BottomBarCatalog.Flash,
            "NodeEditorTab" => BottomBarCatalog.Node,
            "DeliveryTab" => BottomBarCatalog.Delivery,
            "SettingsTab" => BottomBarCatalog.Settings,
            _ => null
        };

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
            Dispatcher.UIThread.Post(AttachTabControl, DispatcherPriority.Loaded);

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (_tabControl is null)
            {
                return;
            }

            _tabControl.ContainerPrepared -= OnContainerPrepared;
            _tabControl.LayoutUpdated -= OnTabControlLayoutUpdated;
            _tabControl = null;
            _tabStrip = null;
        }

        private void AttachTabControl()
        {
            var tabControl = _owner.GetVisualDescendants().OfType<TabControl>().FirstOrDefault(item => item.Name == "PART_TabControl")
                ?? _owner.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
            if (ReferenceEquals(tabControl, _tabControl))
            {
                ApplyContainerVisibility();
                return;
            }

            if (_tabControl is not null)
            {
                _tabControl.ContainerPrepared -= OnContainerPrepared;
                _tabControl.LayoutUpdated -= OnTabControlLayoutUpdated;
            }

            _tabControl = tabControl;
            if (_tabControl is not null)
            {
                _tabControl.ContainerPrepared += OnContainerPrepared;
                _tabControl.LayoutUpdated += OnTabControlLayoutUpdated;
            }

            ApplyContainerVisibility();
        }

        // LayoutUpdated runs inside the layout pass. Writing RenderTransform or Width there re-enters the
        // layout system while it is still arranging, which some backends (X11/Wayland compositing) sample
        // as a half-applied frame. Only record the request and run the pass afterwards.
        // <para>LayoutUpdated 在布局过程中运行。此时写入 RenderTransform 或 Width 会在排布尚未结束时重入
        // 布局系统，某些后端（X11/Wayland 合成）会把它当作半应用的帧采样出来。这里只记录请求，事后再执行。</para>
        private void OnTabControlLayoutUpdated(object? sender, EventArgs e) =>
            RequestRecenter();

        private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (e.Container is TabItem { Content: Page page } tabItem && KeyForPage(page) is { } key)
            {
                var visible = _items.Any(item => item.Key == key && item.IsVisible);
                ConfigureTabItem(tabItem, visible);
                tabItem.IsVisible = visible;
            }

            UpdateTabStripVisibility();
            RequestRecenter();
        }

        private void ApplyContainerVisibility()
        {
            if (_tabControl is null)
            {
                return;
            }

            for (var index = 0; index < _tabControl.ItemCount; index++)
            {
                if (_tabControl.ContainerFromIndex(index) is TabItem { Content: Page page } tabItem
                    && KeyForPage(page) is { } key)
                {
                    var visible = _items.Any(item => item.Key == key && item.IsVisible);
                    ConfigureTabItem(tabItem, visible);
                    tabItem.IsVisible = visible;
                }
            }

            UpdateTabStripVisibility();
            RequestRecenter();
        }

        // Idempotent by construction: ContainerPrepared and ApplyContainerVisibility both land here, and on
        // Linux a prepared container can be re-prepared after a reveal. Assigning an unchanged value still
        // invalidates the layout, so compare first to keep the strip from re-measuring on every pass.
        // <para>天然幂等：ContainerPrepared 与 ApplyContainerVisibility 都会走到这里，而 Linux 上容器可能在
        // 揭示后被重新准备。写入相同值同样会让布局失效，因此先比较，避免页签条每趟都重新测量。</para>
        private static void ConfigureTabItem(TabItem tabItem, bool visible)
        {
            var width = visible ? TabWidth : 0;
            SetIfChanged(tabItem, Control.WidthProperty, width);
            SetIfChanged(tabItem, Control.MinWidthProperty, width);
            SetIfChanged(tabItem, Control.MaxWidthProperty, width);
            SetIfChanged(tabItem, Visual.OpacityProperty, visible ? 1d : 0d);
            SetIfChanged(tabItem, InputElement.IsHitTestVisibleProperty, visible);
            SetIfChanged(tabItem, Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            SetIfChanged(tabItem, ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center);
            SetIfChanged(tabItem, ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center);
        }

        private static void SetIfChanged<T>(AvaloniaObject target, StyledProperty<T> property, T value)
        {
            if (!EqualityComparer<T>.Default.Equals(target.GetValue(property), value))
            {
                target.SetValue(property, value);
            }
        }

        // Coalesces every recenter request raised during one layout/apply cycle into a single deferred pass.
        // The pass runs at Loaded priority, i.e. after the layout that asked for it has been committed, so the
        // tab strip is never observed with an offset that a later pass is about to replace.
        // <para>把一次布局/应用周期内所有重排请求合并成一趟延迟执行。该趟在 Loaded 优先级运行，也就是在
        // 请求它的那次布局提交之后，因此不会把页签条暴露在一个随后会被替换的偏移上。</para>
        private void RequestRecenter()
        {
            if (_tabControl is null || _recentering || _recenterPosted)
            {
                return;
            }

            _recenterPosted = true;
            Dispatcher.UIThread.Post(() =>
            {
                _recenterPosted = false;
                RecenterVisibleTabs();
            }, DispatcherPriority.Loaded);
        }

        private void RecenterVisibleTabs()
        {
            if (_tabControl is null || _recentering)
            {
                return;
            }

            _recentering = true;
            try
            {
                RecenterVisibleTabsCore();
            }
            finally
            {
                _recentering = false;
            }
        }

        private void RecenterVisibleTabsCore()
        {
            if (_tabControl is null)
            {
                return;
            }

            var tabs = new Dictionary<string, TabItem>(StringComparer.Ordinal);
            for (var index = 0; index < _tabControl.ItemCount; index++)
            {
                if (_tabControl.ContainerFromIndex(index) is TabItem { Content: Page page } tabItem
                    && KeyForPage(page) is { } key)
                {
                    tabs[key] = tabItem;
                }
            }

            var visibleTabs = _items
                .Where(item => item.IsVisible)
                .Select(item => tabs.GetValueOrDefault(item.Key))
                .OfType<TabItem>()
                .ToArray();
            if (visibleTabs.Length == 0)
            {
                return;
            }

            var stripWidth = (visibleTabs.Length * TabWidth) + ((visibleTabs.Length - 1) * TabSpacing);

            // Measure the pure layout position for each tab. Bounds is a layout result and therefore never
            // contains the RenderTransform applied below, so every pass computes the same absolute offset from
            // the same input. Translating first and then compensating for the stored transform made the result
            // depend on whether the point mapping already folded that transform in; any mismatch there turns
            // each pass into a small extra shift, so the strip drifts instead of settling and the drawn
            // items separate from the layout-anchored selection pipe.
            // <para>为每个页签测量纯布局位置。Bounds 是布局结果，永远不含下面写入的 RenderTransform，
            // 因此每趟都由同一输入算出同一绝对偏移。此前先平移再补偿已存变换，使结果取决于点映射是否
            // 已把该变换折入；两者不一致时每趟都会多平移一点，页签条于是漂移而不收敛，绘制内容也随之
            // 与按布局位置锚定的选中指示条分离。</para>
            var stripPanel = visibleTabs[0].GetVisualParent() as Control ?? _tabControl;
            var availableWidth = stripPanel.Bounds.Width;
            if (availableWidth <= 0)
            {
                availableWidth = _tabControl.Bounds.Width;
            }

            var left = Math.Max(0, (availableWidth - stripWidth) / 2);
            for (var index = 0; index < visibleTabs.Length; index++)
            {
                var tab = visibleTabs[index];
                var desiredX = left + (index * (TabWidth + TabSpacing));
                var offset = desiredX - tab.Bounds.X;

                // A recenter pass must be idempotent: assigning an equal Transform still creates a new instance
                // and invalidates the visual, which schedules another layout and repeats the cycle.
                // <para>重排趟必须幂等：写入相等的 Transform 同样是新实例，会让视觉失效并再次排布，
                // 从而把这一趟无限重复下去。</para>
                if (Math.Abs(offset - CurrentOffset(tab)) <= 0.1)
                {
                    continue;
                }

                tab.RenderTransform = Math.Abs(offset) <= 0.1 ? null : new TranslateTransform(offset, 0);
            }
        }

        private static double CurrentOffset(TabItem tab) =>
            tab.RenderTransform is TranslateTransform { X: var x } ? x : 0;

        private void UpdateTabStripVisibility()
        {
            var visibleCount = _items.Count(item => item.IsVisible);
            var presenter = _tabControl?
                .GetVisualDescendants()
                .OfType<ItemsPresenter>()
                .FirstOrDefault(item => item.Name == "PART_ItemsPresenter");
            _tabStrip = presenter?.GetVisualParent() as Control;
            if (_tabStrip is not null)
            {
                _tabStrip.IsVisible = visibleCount > 1;
            }
        }
    }
}