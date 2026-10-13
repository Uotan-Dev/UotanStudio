using Avalonia;
using Avalonia.Controls;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Places a label beside its editor when space permits and stacks them when the row becomes narrow.
/// <para>在空间足够时横向排列标签和输入控件，空间不足时自动切换为上下排列。</para>
/// </summary>
public sealed class ResponsiveFormRow : Panel
{
    public static readonly StyledProperty<double> MinLabelWidthProperty =
        AvaloniaProperty.Register<ResponsiveFormRow, double>(nameof(MinLabelWidth), 75d);

    public static readonly StyledProperty<double> MaxLabelWidthProperty =
        AvaloniaProperty.Register<ResponsiveFormRow, double>(nameof(MaxLabelWidth), 180d);

    public static readonly StyledProperty<double> MinEditorWidthProperty =
        AvaloniaProperty.Register<ResponsiveFormRow, double>(nameof(MinEditorWidth), 0d);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ResponsiveFormRow, double>(nameof(ColumnSpacing), 0d);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ResponsiveFormRow, double>(nameof(RowSpacing), 4d);

    private bool _isStacked;
    private double _labelWidth;
    private double _editorWidth;
    private double _labelHeight;
    private double _editorHeight;

    static ResponsiveFormRow()
    {
        AffectsMeasure<ResponsiveFormRow>(
            MinLabelWidthProperty,
            MaxLabelWidthProperty,
            MinEditorWidthProperty,
            ColumnSpacingProperty,
            RowSpacingProperty);
        AffectsArrange<ResponsiveFormRow>(
            MinLabelWidthProperty,
            MaxLabelWidthProperty,
            MinEditorWidthProperty,
            ColumnSpacingProperty,
            RowSpacingProperty);
    }

    /// <summary>
    /// Gets or sets the minimum label-column width.
    /// <para>获取或设置标签列的最小宽度。</para>
    /// </summary>
    public double MinLabelWidth
    {
        get => GetValue(MinLabelWidthProperty);
        set => SetValue(MinLabelWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the maximum label-column width in horizontal layout.
    /// <para>获取或设置横向布局时标签列允许增长到的最大宽度。</para>
    /// </summary>
    public double MaxLabelWidth
    {
        get => GetValue(MaxLabelWidthProperty);
        set => SetValue(MaxLabelWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the minimum editor width required to keep the horizontal layout; this value only
    /// controls when stacking occurs and does not constrain the editor width in the stacked layout.
    /// <para>获取或设置输入控件保持横向布局所需的最小宽度；该值只决定换行时机，不限制上下布局中的实际宽度。</para>
    /// </summary>
    public double MinEditorWidth
    {
        get => GetValue(MinEditorWidthProperty);
        set => SetValue(MinEditorWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the horizontal spacing between the label and editor.
    /// <para>获取或设置横向布局中标签与输入控件之间的距离。</para>
    /// </summary>
    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>
    /// Gets or sets the vertical spacing between the label and editor.
    /// <para>获取或设置上下布局中标签与输入控件之间的距离。</para>
    /// </summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return default;
        }

        if (Children.Count == 1)
        {
            Children[0].Measure(availableSize);
            return Children[0].DesiredSize;
        }

        var label = Children[0];
        var editor = Children[1];
        var minLabelWidth = Math.Max(0, MinLabelWidth);
        var maxLabelWidth = Math.Max(minLabelWidth, MaxLabelWidth);
        var columnSpacing = Math.Max(0, ColumnSpacing);
        var rowSpacing = Math.Max(0, RowSpacing);
        var hasFiniteWidth = double.IsFinite(availableSize.Width);
        var availableWidth = hasFiniteWidth ? Math.Max(0, availableSize.Width) : double.PositiveInfinity;

        label.Measure(new Size(maxLabelWidth, double.PositiveInfinity));
        _labelWidth = Math.Clamp(label.DesiredSize.Width, minLabelWidth, maxLabelWidth);

        var editorMinWidth = Math.Max(Math.Max(0, editor.MinWidth), Math.Max(0, MinEditorWidth));
        _isStacked = hasFiniteWidth && _labelWidth + columnSpacing + editorMinWidth > availableWidth;

        if (_isStacked)
        {
            _labelWidth = availableWidth;
            label.Measure(new Size(_labelWidth, double.PositiveInfinity));
            _editorWidth = ClampEditorWidth(editor, availableWidth);
            editor.Measure(new Size(_editorWidth, double.PositiveInfinity));
            _labelHeight = label.DesiredSize.Height;
            _editorHeight = editor.DesiredSize.Height;
            return new Size(
                Math.Max(label.DesiredSize.Width, editor.DesiredSize.Width),
                _labelHeight + rowSpacing + _editorHeight);
        }

        if (!hasFiniteWidth)
        {
            editor.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _editorWidth = ClampEditorWidth(editor, editor.DesiredSize.Width);
        }
        else
        {
            _editorWidth = ClampEditorWidth(editor, availableWidth - _labelWidth - columnSpacing);
        }

        label.Measure(new Size(_labelWidth, double.PositiveInfinity));
        editor.Measure(new Size(_editorWidth, double.PositiveInfinity));
        _labelHeight = label.DesiredSize.Height;
        _editorHeight = editor.DesiredSize.Height;

        return new Size(
            _labelWidth + columnSpacing + _editorWidth,
            Math.Max(_labelHeight, _editorHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }

        if (Children.Count == 1)
        {
            Children[0].Arrange(new Rect(finalSize));
            return finalSize;
        }

        var columnSpacing = Math.Max(0, ColumnSpacing);
        var rowSpacing = Math.Max(0, RowSpacing);

        if (_isStacked)
        {
            Children[0].Arrange(new Rect(0, 0, _labelWidth, _labelHeight));
            Children[1].Arrange(new Rect(0, _labelHeight + rowSpacing, _editorWidth, _editorHeight));
        }
        else
        {
            var rowHeight = Math.Max(_labelHeight, _editorHeight);
            Children[0].Arrange(new Rect(0, 0, _labelWidth, rowHeight));
            Children[1].Arrange(new Rect(_labelWidth + columnSpacing, 0, _editorWidth, rowHeight));
        }

        return finalSize;
    }

    private static double ClampEditorWidth(Control editor, double availableWidth)
    {
        var minWidth = Math.Max(0, editor.MinWidth);
        var maxWidth = double.IsFinite(editor.MaxWidth)
            ? Math.Max(minWidth, editor.MaxWidth)
            : double.PositiveInfinity;
        return Math.Clamp(Math.Max(0, availableWidth), minWidth, maxWidth);
    }
}