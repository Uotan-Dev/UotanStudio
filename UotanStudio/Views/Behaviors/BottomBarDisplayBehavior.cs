using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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
        private bool _recenterPosted;

        internal State(TabbedPage owner)
        {
            _owner = owner;
            CaptureManagedPages();
            _owner.AttachedToVisualTree += OnAttachedToVisualTree;
            _owner.SizeChanged += OnOwnerSizeChanged;
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
            _tabControl.SizeChanged -= OnTabControlSizeChanged;
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
                _tabControl.SizeChanged -= OnTabControlSizeChanged;
            }

            _tabControl = tabControl;
            if (_tabControl is not null)
            {
                _tabControl.ContainerPrepared += OnContainerPrepared;
                _tabControl.SizeChanged += OnTabControlSizeChanged;
            }

            ApplyContainerVisibility();
        }

        private void OnOwnerSizeChanged(object? sender, SizeChangedEventArgs e) =>
            PostRecenter();

        private void OnTabControlSizeChanged(object? sender, SizeChangedEventArgs e) =>
            PostRecenter();

        private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (e.Container is TabItem { Content: Page page } tabItem && KeyForPage(page) is { } key)
            {
                var visible = _items.Any(item => item.Key == key && item.IsVisible);
                ConfigureTabItem(tabItem, visible);
                tabItem.IsVisible = visible;
            }

            UpdateTabStripVisibility();
            PostRecenter();
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
            PostRecenter();
        }

        private static void ConfigureTabItem(TabItem tabItem, bool visible)
        {
            var width = visible ? TabWidth : 0;
            tabItem.Width = width;
            tabItem.MinWidth = width;
            tabItem.MaxWidth = width;
            tabItem.Opacity = visible ? 1 : 0;
            tabItem.IsHitTestVisible = visible;
            tabItem.HorizontalAlignment = HorizontalAlignment.Center;
            tabItem.HorizontalContentAlignment = HorizontalAlignment.Center;
            tabItem.VerticalContentAlignment = VerticalAlignment.Center;
        }

        private void PostRecenter()
        {
            if (_recenterPosted)
            {
                return;
            }

            _recenterPosted = true;
            if (_tabStrip is not null)
            {
                _tabStrip.Opacity = 0;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _recenterPosted = false;
                RecenterVisibleTabs();
            }, DispatcherPriority.Loaded);
        }

        private void RecenterVisibleTabs()
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
                    tabItem.RenderTransform = null;
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
            var availableWidth = TopLevel.GetTopLevel(_owner)?.ClientSize.Width ?? _owner.Bounds.Width;
            var left = Math.Max(0, (availableWidth - stripWidth) / 2);
            for (var index = 0; index < visibleTabs.Length; index++)
            {
                var tab = visibleTabs[index];
                if (tab.TranslatePoint(default, _tabControl) is not { } origin)
                {
                    continue;
                }

                var desiredX = left + (index * (TabWidth + TabSpacing));
                var offset = desiredX - origin.X;
                if (Math.Abs(offset) > 0.1)
                {
                    tab.RenderTransform = new TranslateTransform(offset, 0);
                }
            }

            if (_tabStrip is not null)
            {
                _tabStrip.Opacity = 1;
            }
        }

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