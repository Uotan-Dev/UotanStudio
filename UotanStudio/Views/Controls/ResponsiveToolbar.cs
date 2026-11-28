using System.Collections.Specialized;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Metadata;
using Avalonia.Styling;
using Td = Irihi.Iconica.TDesign.Icons;

namespace UotanStudio.Views.Controls;

/// <summary>
/// A single-row responsive toolbar that shrinks flexible items down to their minimum width before
/// collapsing lower-priority items into an overflow flyout button.
/// <para>单行响应式工具栏：在空间不足时先将可伸缩项压缩至最小宽度，再按优先级将低优先级项收纳进溢出弹出菜单。</para>
/// </summary>
public sealed class ResponsiveToolbar : Panel
{
    /// <summary>
    /// Defines the <see cref="Spacing"/> property.
    /// <para>定义 <see cref="Spacing"/> 属性。</para>
    /// </summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ResponsiveToolbar, double>(nameof(Spacing), 8d);

    /// <summary>
    /// Defines the Priority attached property. Higher values stay on the main bar longer; ties overflow right-to-left.
    /// <para>定义 Priority 附加属性。数值越高越晚进入溢出菜单；同优先级按从右到左顺序收纳。</para>
    /// </summary>
    public static readonly AttachedProperty<int> PriorityProperty =
        AvaloniaProperty.RegisterAttached<ResponsiveToolbar, Control, int>("Priority", 0);

    /// <summary>
    /// Defines the IsFlexible attached property. Flexible items can shrink down to their <see cref="Layoutable.MinWidth"/> before overflowing.
    /// <para>定义 IsFlexible 附加属性。可伸缩项在溢出前可压缩至其 <see cref="Layoutable.MinWidth"/>。</para>
    /// </summary>
    public static readonly AttachedProperty<bool> IsFlexibleProperty =
        AvaloniaProperty.RegisterAttached<ResponsiveToolbar, Control, bool>("IsFlexible", false);

    /// <summary>
    /// Defines the PreferredWidth attached property for flexible items when no overflow occurs.
    /// <para>定义可伸缩项在未发生溢出时的首选宽度。</para>
    /// </summary>
    public static readonly AttachedProperty<double> PreferredWidthProperty =
        AvaloniaProperty.RegisterAttached<ResponsiveToolbar, Control, double>("PreferredWidth", double.NaN);

    private readonly WrapPanel _overflowPanel;
    private readonly Flyout _overflowFlyout;
    private readonly Button _moreButton;
    private readonly List<Control> _keptItems = [];
    private readonly List<Control> _overflowItems = [];
    private readonly Dictionary<Control, double> _assignedWidths = [];
    private readonly HashSet<Control> _keptSet = [];
    private double _lastLayoutWidth = double.NaN;
    private double _flyoutOpenWidth = double.NaN;
    private bool _flyoutOpen;

    static ResponsiveToolbar()
    {
        AffectsMeasure<ResponsiveToolbar>(SpacingProperty);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResponsiveToolbar"/> class.
    /// <para>初始化 <see cref="ResponsiveToolbar"/> 实例。</para>
    /// </summary>
    public ResponsiveToolbar()
    {
        ClipToBounds = true;

        _overflowPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 4,
            MaxWidth = 320
        };
        _overflowPanel.Bind(DataContextProperty, this.GetObservable(DataContextProperty));
        _overflowPanel.AttachedToVisualTree += OnOverflowPanelAttachedToVisualTree;

        _overflowFlyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = _overflowPanel
        };
        _overflowFlyout.FlyoutPresenterClasses.Add("ResponsiveToolbarOverflow");
        _overflowFlyout.Opening += OnFlyoutOpening;
        _overflowFlyout.Closed += OnFlyoutClosed;

        var flyoutStyle = new Style(x => x.OfType<FlyoutPresenter>().Class("ResponsiveToolbarOverflow"));
        flyoutStyle.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 6)));
        flyoutStyle.Setters.Add(new Setter(MinHeightProperty, 0d));
        flyoutStyle.Setters.Add(new Setter(MinWidthProperty, 0d));
        Styles.Add(flyoutStyle);

        _moreButton = new Button
        {
            Width = 20,
            Height = 20,
            MinHeight = 0,
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0d,
            IsHitTestVisible = false,
            IsTabStop = false,
            Flyout = _overflowFlyout,
            Content = new Td.Ellipsis { Width = 12, Height = 12 }
        };
        _moreButton.Classes.Add("Small");
        _moreButton.Bind(ToolTip.TipProperty, new DynamicResourceExtension("Toolbar_More"));
        _moreButton.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new DynamicResourceExtension("Toolbar_More"));

        Children.Add(_moreButton);

        Items.CollectionChanged += OnItemsCollectionChanged;
    }

    /// <summary>
    /// Gets the collection of toolbar items.
    /// <para>获取工具栏子项集合。</para>
    /// </summary>
    [Content]
    public AvaloniaList<Control> Items { get; } = [];

    /// <summary>
    /// Gets or sets the horizontal spacing between adjacent toolbar items.
    /// <para>获取或设置相邻工具栏子项之间的水平间距。</para>
    /// </summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    internal IReadOnlyList<Control> MainItems => _keptItems;

    internal IReadOnlyList<Control> OverflowItems => _overflowItems;

    internal bool HasOverflow => _overflowItems.Count > 0;

    internal Button MoreButton => _moreButton;

    internal WrapPanel OverflowPanel => _overflowPanel;

    internal double GetAssignedWidth(Control item) =>
        _assignedWidths.TryGetValue(item, out var width) ? width : item.Bounds.Width;

    /// <summary>
    /// Gets the overflow priority for the specified control.
    /// <para>获取指定控件的溢出优先级。</para>
    /// </summary>
    /// <param name="control">The target control.<para>目标控件。</para></param>
    /// <returns>The priority value.<para>优先级数值。</para></returns>
    public static int GetPriority(Control control) => control.GetValue(PriorityProperty);

    /// <summary>
    /// Sets the overflow priority for the specified control.
    /// <para>设置指定控件的溢出优先级。</para>
    /// </summary>
    /// <param name="control">The target control.<para>目标控件。</para></param>
    /// <param name="value">The priority value.<para>优先级数值。</para></param>
    public static void SetPriority(Control control, int value) => control.SetValue(PriorityProperty, value);

    /// <summary>
    /// Gets whether the specified control can shrink down to its minimum width before overflowing.
    /// <para>获取指定控件在溢出前是否可压缩至最小宽度。</para>
    /// </summary>
    /// <param name="control">The target control.<para>目标控件。</para></param>
    /// <returns><see langword="true"/> if flexible; otherwise <see langword="false"/>.<para>若可伸缩则为 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    public static bool GetIsFlexible(Control control) => control.GetValue(IsFlexibleProperty);

    /// <summary>
    /// Sets whether the specified control can shrink down to its minimum width before overflowing.
    /// <para>设置指定控件在溢出前是否可压缩至最小宽度。</para>
    /// </summary>
    /// <param name="control">The target control.<para>目标控件。</para></param>
    /// <param name="value">Whether the control is flexible.<para>是否可伸缩。</para></param>
    public static void SetIsFlexible(Control control, bool value) => control.SetValue(IsFlexibleProperty, value);

    /// <summary>
    /// Gets the preferred width for the specified flexible control.
    /// <para>获取指定可伸缩控件的首选宽度。</para>
    /// </summary>
    /// <param name="control">The target control.<para>目标控件。</para></param>
    /// <returns>The preferred width, or <see cref="double.NaN"/> when unset.<para>首选宽度；未设置时返回 <see cref="double.NaN"/>。</para></returns>
    public static double GetPreferredWidth(Control control) => control.GetValue(PreferredWidthProperty);

    /// <summary>
    /// Sets the preferred width for the specified flexible control.
    /// <para>设置指定可伸缩控件的首选宽度。</para>
    /// </summary>
    /// <param name="control">The target control.<para>目标控件。</para></param>
    /// <param name="value">The preferred width.<para>首选宽度。</para></param>
    public static void SetPreferredWidth(Control control, double value) => control.SetValue(PreferredWidthProperty, value);

    internal void OpenOverflowForTesting()
    {
        _flyoutOpen = true;
        _flyoutOpenWidth = _lastLayoutWidth;
        MoveOverflowItemsToFlyout();
    }

    internal void CloseOverflowForTesting()
    {
        _flyoutOpen = false;
        ReturnOverflowItemsToMainBar();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_flyoutOpen && double.IsFinite(availableSize.Width) && double.IsFinite(_flyoutOpenWidth)
            && Math.Abs(availableSize.Width - _flyoutOpenWidth) > 0.5)
        {
            _overflowFlyout.Hide();
        }

        if (!_flyoutOpen)
        {
            ComputeLayout(availableSize.Width);
        }

        var spacing = Math.Max(0d, Spacing);
        var totalWidth = 0d;
        var maxHeight = 0d;

        for (var i = 0; i < _keptItems.Count; i++)
        {
            var item = _keptItems[i];
            if (i > 0)
            {
                totalWidth += spacing;
            }

            totalWidth += _assignedWidths[item];
            maxHeight = Math.Max(maxHeight, item.DesiredSize.Height);
        }

        _moreButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (_overflowItems.Count > 0)
        {
            if (_keptItems.Count > 0)
            {
                totalWidth += spacing;
            }

            totalWidth += Math.Max(20d, _moreButton.DesiredSize.Width);
            maxHeight = Math.Max(maxHeight, _moreButton.DesiredSize.Height);
        }

        return new Size(totalWidth, maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_flyoutOpen)
        {
            ComputeLayout(finalSize.Width);
        }

        var spacing = Math.Max(0d, Spacing);
        var x = 0d;

        for (var i = 0; i < _keptItems.Count; i++)
        {
            var item = _keptItems[i];
            if (i > 0)
            {
                x += spacing;
            }

            var w = _assignedWidths[item];
            var h = item.DesiredSize.Height;
            var y = item.VerticalAlignment switch
            {
                VerticalAlignment.Top => 0d,
                VerticalAlignment.Bottom => Math.Max(0d, finalSize.Height - h),
                VerticalAlignment.Stretch => 0d,
                _ => Math.Max(0d, (finalSize.Height - h) / 2d)
            };
            var arrangeH = item.VerticalAlignment == VerticalAlignment.Stretch ? finalSize.Height : h;

            if (ReferenceEquals(item.Parent, this))
            {
                item.Arrange(new Rect(x, y, w, arrangeH));
            }

            x += w;
        }

        foreach (var child in Children)
        {
            if (!ReferenceEquals(child, _moreButton) && !_keptSet.Contains(child))
            {
                child.Arrange(new Rect(-10000, -10000, 0, 0));
            }
        }

        var hasOverflow = _overflowItems.Count > 0;
        _moreButton.Opacity = hasOverflow ? 1d : 0d;
        _moreButton.IsHitTestVisible = hasOverflow;
        _moreButton.IsTabStop = hasOverflow;

        if (hasOverflow)
        {
            var moreW = Math.Max(20d, _moreButton.DesiredSize.Width);
            var moreH = Math.Max(20d, _moreButton.DesiredSize.Height);
            var moreX = Math.Max(x + (_keptItems.Count > 0 ? spacing : 0d), finalSize.Width - moreW);
            var moreY = Math.Max(0d, (finalSize.Height - moreH) / 2d);
            _moreButton.Arrange(new Rect(moreX, moreY, moreW, moreH));
        }
        else
        {
            _moreButton.Arrange(new Rect(-10000, -10000, 0, 0));
        }

        return finalSize;
    }

    private void OnOverflowPanelAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_overflowPanel.Parent is FlyoutPresenter presenter)
        {
            presenter.Padding = new Thickness(8, 6);
            presenter.MinHeight = 0d;
            presenter.MinWidth = 0d;
        }
    }

    private void OnFlyoutOpening(object? sender, EventArgs e)
    {
        _flyoutOpen = true;
        _flyoutOpenWidth = _lastLayoutWidth;
        MoveOverflowItemsToFlyout();
    }

    private void OnFlyoutClosed(object? sender, EventArgs e)
    {
        _flyoutOpen = false;
        ReturnOverflowItemsToMainBar();
    }

    private void MoveOverflowItemsToFlyout()
    {
        _overflowPanel.Children.Clear();
        foreach (var item in _overflowItems)
        {
            Children.Remove(item);
            _overflowPanel.Children.Add(item);
        }
    }

    private void ReturnOverflowItemsToMainBar()
    {
        if (_overflowPanel.Children.Count == 0)
        {
            return;
        }

        for (var i = _overflowPanel.Children.Count - 1; i >= 0; i--)
        {
            _overflowPanel.Children.RemoveAt(i);
        }

        EnsureAllItemsInMainChildren();
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void EnsureAllItemsInMainChildren()
    {
        var insertIndex = 0;
        foreach (var item in Items)
        {
            if (_overflowPanel.Children.Contains(item))
            {
                continue;
            }

            if (Children.Count <= insertIndex || !ReferenceEquals(Children[insertIndex], item))
            {
                Children.Remove(item);
                Children.Insert(insertIndex, item);
            }

            insertIndex++;
        }

        if (!Children.Contains(_moreButton))
        {
            Children.Add(_moreButton);
        }
        else if (!ReferenceEquals(Children[^1], _moreButton))
        {
            Children.Remove(_moreButton);
            Children.Add(_moreButton);
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (Control item in e.OldItems)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
                Children.Remove(item);
                _overflowPanel.Children.Remove(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (Control item in e.NewItems)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }

        EnsureAllItemsInMainChildren();
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void OnItemPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty
            || e.Property == MinWidthProperty
            || e.Property == MaxWidthProperty
            || e.Property == PriorityProperty
            || e.Property == IsFlexibleProperty
            || e.Property == HorizontalAlignmentProperty)
        {
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    private void ComputeLayout(double availableWidth)
    {
        _lastLayoutWidth = availableWidth;
        _keptItems.Clear();
        _overflowItems.Clear();
        _assignedWidths.Clear();
        _keptSet.Clear();

        var visibleItems = new List<Control>(Items.Count);
        foreach (var item in Items)
        {
            if (item.IsVisible)
            {
                visibleItems.Add(item);
            }
        }

        if (visibleItems.Count == 0)
        {
            return;
        }

        var spacing = Math.Max(0d, Spacing);
        var count = visibleItems.Count;
        var preferredWidths = new double[count];
        var minWidths = new double[count];
        var isFlexible = new bool[count];

        var totalPreferred = 0d;
        var totalMin = 0d;

        for (var i = 0; i < count; i++)
        {
            var item = visibleItems[i];
            item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var flexible = GetIsFlexible(item);
            isFlexible[i] = flexible;

            var maxW = double.IsFinite(item.MaxWidth) ? Math.Max(0d, item.MaxWidth) : double.PositiveInfinity;
            var naturalW = item.DesiredSize.Width;
            var explicitPrefW = GetPreferredWidth(item);

            double prefW;
            if (flexible && double.IsFinite(explicitPrefW))
            {
                prefW = Math.Clamp(Math.Max(Math.Max(0d, item.MinWidth), explicitPrefW), 0d, maxW);
            }
            else if (flexible && item.HorizontalAlignment == HorizontalAlignment.Stretch && double.IsFinite(maxW))
            {
                prefW = Math.Max(Math.Max(0d, item.MinWidth), maxW);
            }
            else if (double.IsFinite(maxW))
            {
                prefW = Math.Min(naturalW, maxW);
            }
            else
            {
                prefW = naturalW;
            }

            var minW = flexible ? Math.Min(prefW, Math.Max(0d, item.MinWidth)) : prefW;

            preferredWidths[i] = prefW;
            minWidths[i] = minW;
            totalPreferred += prefW;
            totalMin += minW;
        }

        var totalSpacing = spacing * Math.Max(0, count - 1);

        if (!double.IsFinite(availableWidth) || totalPreferred + totalSpacing <= availableWidth + 0.5)
        {
            for (var i = 0; i < count; i++)
            {
                var item = visibleItems[i];
                _keptItems.Add(item);
                _keptSet.Add(item);
                _assignedWidths[item] = preferredWidths[i];
                if (isFlexible[i])
                {
                    item.Measure(new Size(preferredWidths[i], double.PositiveInfinity));
                }
            }

            return;
        }

        if (totalMin + totalSpacing <= availableWidth + 0.5)
        {
            var extra = Math.Max(0d, availableWidth - totalMin - totalSpacing);
            DistributeExtraWidth(visibleItems, minWidths, preferredWidths, isFlexible, extra);
            for (var i = 0; i < count; i++)
            {
                var item = visibleItems[i];
                _keptItems.Add(item);
                _keptSet.Add(item);
                if (isFlexible[i])
                {
                    item.Measure(new Size(_assignedWidths[item], double.PositiveInfinity));
                }
            }

            return;
        }

        _moreButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var moreW = Math.Max(20d, _moreButton.DesiredSize.Width);

        var keptMask = new bool[count];
        Array.Fill(keptMask, true);

        var evictionOrder = Enumerable.Range(0, count)
            .OrderBy(i => GetPriority(visibleItems[i]))
            .ThenByDescending(i => i)
            .ToArray();

        var keptCount = count;
        var keptMinSum = totalMin;

        foreach (var evictIndex in evictionOrder)
        {
            var budget = keptCount > 0
                ? Math.Max(0d, availableWidth - moreW - spacing)
                : Math.Max(0d, availableWidth - moreW);

            var currentSpacing = spacing * Math.Max(0, keptCount - 1);
            if (keptMinSum + currentSpacing <= budget + 0.5)
            {
                break;
            }

            keptMask[evictIndex] = false;
            keptCount--;
            keptMinSum -= minWidths[evictIndex];
        }

        var finalKeptIndices = new List<int>(keptCount);
        for (var i = 0; i < count; i++)
        {
            if (keptMask[i])
            {
                finalKeptIndices.Add(i);
                _keptItems.Add(visibleItems[i]);
                _keptSet.Add(visibleItems[i]);
            }
            else
            {
                _overflowItems.Add(visibleItems[i]);
            }
        }

        if (finalKeptIndices.Count > 0)
        {
            var finalBudget = Math.Max(0d, availableWidth - moreW - spacing - spacing * Math.Max(0, finalKeptIndices.Count - 1));
            var extra = Math.Max(0d, finalBudget - keptMinSum);

            var keptMin = new double[finalKeptIndices.Count];
            var keptPref = new double[finalKeptIndices.Count];
            var keptFlex = new bool[finalKeptIndices.Count];
            var keptList = new List<Control>(finalKeptIndices.Count);
            for (var k = 0; k < finalKeptIndices.Count; k++)
            {
                var idx = finalKeptIndices[k];
                keptList.Add(visibleItems[idx]);
                keptMin[k] = minWidths[idx];
                keptPref[k] = preferredWidths[idx];
                keptFlex[k] = isFlexible[idx];
            }

            DistributeExtraWidth(keptList, keptMin, keptPref, keptFlex, extra, allowGrowBeyondPreferred: true);
            for (var k = 0; k < keptList.Count; k++)
            {
                if (keptFlex[k])
                {
                    keptList[k].Measure(new Size(_assignedWidths[keptList[k]], double.PositiveInfinity));
                }
            }
        }
    }

    private void DistributeExtraWidth(
        IReadOnlyList<Control> items,
        double[] minWidths,
        double[] preferredWidths,
        bool[] isFlexible,
        double extra,
        bool allowGrowBeyondPreferred = false)
    {
        var totalFlexSlack = 0d;
        for (var i = 0; i < items.Count; i++)
        {
            if (isFlexible[i])
            {
                totalFlexSlack += Math.Max(0d, preferredWidths[i] - minWidths[i]);
            }
        }

        var usedExtra = 0d;
        for (var i = 0; i < items.Count; i++)
        {
            var width = minWidths[i];
            if (isFlexible[i] && totalFlexSlack > 0d && extra > 0d)
            {
                var slack = Math.Max(0d, preferredWidths[i] - minWidths[i]);
                var share = extra * (slack / totalFlexSlack);
                var added = Math.Min(slack, share);
                width = minWidths[i] + added;
                usedExtra += added;
            }

            _assignedWidths[items[i]] = width;
        }

        var remaining = extra - usedExtra;
        if (allowGrowBeyondPreferred && remaining > 0.5)
        {
            var stretchCount = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (isFlexible[i] && items[i].HorizontalAlignment == HorizontalAlignment.Stretch)
                {
                    stretchCount++;
                }
            }

            if (stretchCount > 0)
            {
                var bonus = remaining / stretchCount;
                for (var i = 0; i < items.Count; i++)
                {
                    if (isFlexible[i] && items[i].HorizontalAlignment == HorizontalAlignment.Stretch)
                    {
                        _assignedWidths[items[i]] += bonus;
                    }
                }
            }
        }
    }
}