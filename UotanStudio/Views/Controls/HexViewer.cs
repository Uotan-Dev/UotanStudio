using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using UotanStudio.Services;

namespace UotanStudio.Views.Controls;

/// <summary>
/// A custom-rendered hex editor for binary files: draws the offset gutter, hex and ASCII columns in a single
/// render pass, and supports range selection, clipboard copy/paste, goto-offset, keyboard navigation and
/// edited-byte highlighting. Theme-aware via <c>Hex*</c> resource brushes.
/// <para>面向二进制文件的自定义绘制十六进制编辑器：单次渲染绘制偏移列、十六进制与 ASCII 列，支持范围选择、
/// 剪贴板复制/粘贴、跳转偏移、键盘导航与已编辑字节高亮。通过 <c>Hex*</c> 资源画刷适配主题。</para>
/// </summary>
public class HexViewer : Grid
{
    /// <summary>
    /// The bound source data (raw byte array). Edits are written back to this array in place.
    /// <para>绑定源数据（原始字节数组）。编辑会就地写回该数组。</para>
    /// </summary>
    public static readonly DirectProperty<HexViewer, byte[]?> DataProperty =
        AvaloniaProperty.RegisterDirect<HexViewer, byte[]?>(nameof(Data), o => o.Data, (o, v) => o.Data = v);

    /// <summary>
    /// Raised after one or more byte edits, so a host (e.g. the document view) can mark itself dirty.
    /// <para>发生一次或多次字节编辑后触发，供宿主（如文档视图）标记为已修改。</para>
    /// </summary>
    public event EventHandler? BytesEdited;

    /// <summary>
    /// Encoding name used to decode the text column and encode text input; <c>null</c> keeps the
    /// legacy single-byte ASCII rendering.
    /// <para>文本列解码与文本输入使用的编码名称；<c>null</c> 保持旧的单字节 ASCII 渲染。</para>
    /// </summary>
    public static readonly StyledProperty<string?> EncodingNameProperty =
        AvaloniaProperty.Register<HexViewer, string?>(nameof(EncodingName));

    /// <summary>
    /// Gets or sets the encoding name for the text column and text input (e.g. "ASCII", "UTF-8",
    /// "GB18030"); <c>null</c> means plain ASCII.
    /// <para>获取或设置文本列与文本输入的编码名称（如 "ASCII"、"UTF-8"、"GB18030"）；<c>null</c> 表示纯 ASCII。</para>
    /// </summary>
    public string? EncodingName
    {
        get => GetValue(EncodingNameProperty);
        set => SetValue(EncodingNameProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EncodingNameProperty)
        {
            _viewport.SetEncodingName(change.NewValue as string);
        }
    }

    private byte[]? _data = [];
    private readonly ScrollViewer _scroll;
    private readonly HexViewport _viewport;
    private readonly TextBox _gotoBox;

    /// <summary>
    /// Gets or sets the raw data array (edits mutate it in place).
    /// <para>获取或设置原始数据数组（编辑就地修改它）。</para>
    /// </summary>
    public byte[]? Data
    {
        get => _data;
        set
        {
            if (SetAndRaise(DataProperty, ref _data, value))
            {
                _viewport.SetData(_data);
            }
        }
    }

    /// <summary>
    /// Constructs the hex viewer: hosts the scrolling viewport and the goto-offset overlay.
    /// <para>构造十六进制查看器：承载滚动视口与跳转偏移浮层。</para>
    /// </summary>
    public HexViewer()
    {
        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        RowDefinitions.Add(new RowDefinition(GridLength.Star));

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        _viewport = new HexViewport(_scroll);
        _scroll.Content = _viewport;

        _gotoBox = new TextBox
        {
            Width = 200,
            Height = 26,
            FontFamily = FontFamily.Parse("Consolas"),
            FontSize = 13,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(8, 8, 0, 0),
            IsVisible = false
        };
        _gotoBox.KeyDown += OnGotoKeyDown;
        _scroll.SizeChanged += (_, _) =>
        {
            _viewport.InvalidateMeasure();
            _viewport.InvalidateVisual();
        };

        Children.Add(_scroll);
        Children.Add(_gotoBox);

        _viewport.BytesEdited += (_, _) => BytesEdited?.Invoke(this, EventArgs.Empty);
        _viewport.GotoRequested += (_, _) => ShowGoto();
        _viewport.SetData(_data);
    }

    private void ShowGoto()
    {
        _gotoBox.Text = string.Empty;
        _gotoBox.IsVisible = true;
        _gotoBox.Focus();
    }

    private void OnGotoKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (TryParseOffset(_gotoBox.Text, out var offset))
            {
                _viewport.Goto(offset);
            }

            _gotoBox.IsVisible = false;
            _viewport.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _gotoBox.IsVisible = false;
            _viewport.Focus();
            e.Handled = true;
        }
    }

    private static bool TryParseOffset(string? text, out long offset)
    {
        if (string.IsNullOrEmpty(text))
        {
            offset = 0;
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
    }

    /// <summary>
    /// The custom-rendered hex viewport hosted inside the scroll viewer.
    /// <para>承载在滚动查看器内的自定义绘制十六进制视口。</para>
    /// </summary>
    private sealed class HexViewport : Control
    {
        private const int BufferLines = 4;
        private const double FontSize = 13;
        private static readonly Typeface MonospaceTypeface = new("Consolas");
        private static readonly IBrush DefaultSelectionBrush =
            new SolidColorBrush(Color.FromArgb(0x66, 0x59, 0xC2, 0xFF));
        private static readonly string[] AsciiCharText = CreateAsciiCharText();
        private static readonly string[] AsciiDefaultText = CreateAsciiByteText(System.Text.Encoding.ASCII);
        private static readonly string[] HexByteText = CreateHexByteText();

        private readonly ScrollViewer _scroll;
        private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.Ordinal);
        private readonly CellGlyph[] _hexCells = new CellGlyph[256];
        private readonly CellGlyph[] _hexEditedCells = new CellGlyph[256];
        private readonly CellGlyph[] _asciiCells = new CellGlyph[256];
        private readonly CellGlyph[] _headerCells = new CellGlyph[HexMetrics.MaximumBytesPerLine];
        private string[] _asciiByteText = AsciiDefaultText;
        private byte[]? _data = [];
        private System.Text.Encoding _textEncoding = System.Text.Encoding.ASCII;

        /// <summary>
        /// Encoding label drawn in the text-column header, kept in sync with the encoding chosen in the
        /// properties panel; plain ASCII by default.
        /// <para>文本列表头显示的编码标签，与属性面板选择的编码保持一致；默认纯 ASCII。</para>
        /// </summary>
        private string _encodingLabel = "ASCII";

        private long _caretOffset;
        private long _selectionAnchor = -1;
        private bool _hasSelection;
        private bool _hexEditZone = true;
        private bool _pendingHighNibble;
        private bool _dragging;
        private readonly HashSet<long> _editedOffsets = [];

        /// <summary>
        /// Raised when the user requests a goto-offset dialog (Ctrl+G).
        /// <para>用户请求跳转偏移对话框（Ctrl+G）时触发。</para>
        /// </summary>
        public event EventHandler? GotoRequested;

        /// <summary>
        /// Raised after one or more byte edits.
        /// <para>发生一次或多次字节编辑后触发。</para>
        /// </summary>
        public event EventHandler? BytesEdited;

        public HexViewport(ScrollViewer scroll)
        {
            _scroll = scroll;
            Focusable = true;
            _scroll.ScrollChanged += (_, _) => InvalidateVisual();
            AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
            AddHandler(PointerMovedEvent, OnPointerMoved, handledEventsToo: true);
            AddHandler(PointerReleasedEvent, OnPointerReleased, handledEventsToo: true);
            AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);
            AddHandler(TextInputEvent, OnTextInput, handledEventsToo: true);
        }

        /// <summary>
        /// Subscribes to theme changes while attached to the visual tree so the custom-rendered
        /// brushes (resolved inside Render) refresh after LightColors/DarkColors dictionary swaps.
        /// <para>进入可视树时订阅主题变更：LightColors/DarkColors 字典替换后强制重绘，
        /// 否则 Render 内解析的 Hex* 画刷不会自动刷新。</para>
        /// </summary>
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            InvalidateCachedGlyphs();
            LanguageService.ThemeChanged += OnThemeChanged;
        }

        /// <summary>
        /// Unsubscribes from theme changes when detached, so the static event does not keep
        /// unloaded controls (and their data) alive.
        /// <para>离开可视树时退订主题变更，避免静态事件持有已卸载控件导致内存泄漏。</para>
        /// </summary>
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            LanguageService.ThemeChanged -= OnThemeChanged;
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            InvalidateCachedGlyphs();
            InvalidateVisual();
        }

        public void SetData(byte[]? data)
        {
            _data = data ?? [];
            _caretOffset = 0;
            _selectionAnchor = -1;
            _hasSelection = false;
            _pendingHighNibble = false;
            _editedOffsets.Clear();
            InvalidateMeasure();
            InvalidateVisual();
        }

        /// <summary>
        /// Sets the encoding for the text column and text input and updates the column-header label;
        /// unknown names fall back to ASCII.
        /// <para>设置文本列与文本输入的编码，并同步列表头标签；未知名称回落为 ASCII。</para>
        /// </summary>
        public void SetEncodingName(string? name)
        {
            var (encoding, label) = ResolveTextEncoding(name);
            if (_textEncoding.CodePage == encoding.CodePage && string.Equals(_encodingLabel, label, StringComparison.Ordinal))
            {
                return;
            }

            _textEncoding = encoding;
            _encodingLabel = label;
            _asciiByteText = CreateAsciiByteText(encoding);
            Array.Clear(_asciiCells);
            InvalidateVisual();
        }

        /// <summary>
        /// Resolves a properties-panel encoding name (the same set as <c>FileViewModel.SupportedEncodings</c>)
        /// into the effective .NET encoding plus the label shown in the column header, falling back to ASCII
        /// when the name cannot be resolved.
        /// <para>解析属性面板使用的编码名称（与 <c>FileViewModel.SupportedEncodings</c> 同一套取值），
        /// 返回实际生效的 .NET 编码与应显示在列表头上的标签；无法解析时回落为 ASCII。</para>
        /// </summary>
        private static (System.Text.Encoding Encoding, string Label) ResolveTextEncoding(string? name)
        {
            switch (name)
            {
                case null or "" or "ASCII":
                    return (System.Text.Encoding.ASCII, "ASCII");
                case "UTF-8":
                    return (new System.Text.UTF8Encoding(false), "UTF-8");
                case "UTF-8 (BOM)":
                    return (new System.Text.UTF8Encoding(true), "UTF-8 (BOM)");
                case "UTF-16 LE":
                    return (new System.Text.UnicodeEncoding(false, true), "UTF-16 LE");
                case "UTF-16 BE":
                    return (new System.Text.UnicodeEncoding(true, true), "UTF-16 BE");
                case "GB18030":

                    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                    return TryGetEncoding("GB18030", out var gb18030)
                        ? (gb18030, "GB18030")
                        : (System.Text.Encoding.ASCII, "ASCII");
                default:
                    return TryGetEncoding(name, out var other) ? (other, name) : (System.Text.Encoding.ASCII, "ASCII");
            }
        }

        private static bool TryGetEncoding(string name, out System.Text.Encoding encoding)
        {
            try
            {
                encoding = System.Text.Encoding.GetEncoding(name);
                return true;
            }
            catch (ArgumentException)
            {
                encoding = System.Text.Encoding.ASCII;
                return false;
            }
        }

        public void Goto(long offset)
        {
            if (_data is null || _data.Length == 0)
            {
                return;
            }

            _hasSelection = false;
            _selectionAnchor = -1;
            SetCaret(Math.Clamp(offset, 0, _data.Length - 1));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var availableWidth = double.IsFinite(availableSize.Width)
                ? availableSize.Width
                : _scroll.Viewport.Width;
            var layout = HexMetrics.CreateLayout(availableWidth);
            var totalLines = HexMetrics.TotalLines(_data?.Length ?? 0, layout.BytesPerLine);
            var height = HexMetrics.HeaderHeight + ((long)totalLines * HexMetrics.LineHeight);
            return new Size(layout.ContentWidth, height);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var layout = HexMetrics.CreateLayout(Bounds.Width);
            var scrollY = _scroll.Offset.Y;
            var viewportHeight = _scroll.Viewport.Height > 0 ? _scroll.Viewport.Height : Bounds.Height;

            var hitTestHeight = Math.Max(0, Math.Min(viewportHeight, Bounds.Height - scrollY));
            if (hitTestHeight > 0)
            {
                context.FillRectangle(
                    Brushes.Transparent,
                    new Rect(0, scrollY, layout.ContentWidth, hitTestHeight));
            }

            var foreground = ResolveCachedBrush("HexForeground", Brushes.Black);
            var offsetBrush = ResolveCachedBrush("HexOffsetForeground", Brushes.Gray);
            var asciiBrush = ResolveCachedBrush("HexAsciiForeground", Brushes.Gray);
            var headerBrush = ResolveCachedBrush("HexHeaderForeground", Brushes.Gray);
            var headerBackground = ResolveCachedBrush("HexHeaderBackground", Brushes.Transparent);
            var editedBrush = ResolveCachedBrush("HexEditedBrush", Brushes.OrangeRed);
            var selectionBrush = ResolveCachedBrush("HexSelectionBrush", DefaultSelectionBrush);
            var caretBrush = ResolveCachedBrush("HexCaretBrush", Brushes.DeepSkyBlue);

            DrawHeader(context, headerBrush, headerBackground, layout, _encodingLabel);
            if (_data is null || _data.Length == 0)
            {
                return;
            }

            var totalLines = HexMetrics.TotalLines(_data.Length, layout.BytesPerLine);
            var firstLine = Math.Max(0, HexMetrics.LineFromY(scrollY) - BufferLines);
            var visible = (int)Math.Ceiling(viewportHeight / HexMetrics.LineHeight) + (2 * BufferLines);
            firstLine = Math.Clamp(firstLine, 0, totalLines - 1);
            visible = Math.Max(1, Math.Min(visible, totalLines - firstLine));

            for (var i = 0; i < visible; i++)
            {
                var line = firstLine + i;
                var y = HexMetrics.YFromLine(line);
                var start = line * layout.BytesPerLine;
                var available = Math.Min(layout.BytesPerLine, _data.Length - start);

                DrawSelectionRects(context, line, start, available, selectionBrush, layout);
                DrawCenteredText(
                    context,
                    start.ToString("X8", CultureInfo.InvariantCulture),
                    0,
                    layout.OffsetWidth,
                    y,
                    offsetBrush);

                for (var b = 0; b < available; b++)
                {
                    var value = _data[start + b];
                    var isEdited = _editedOffsets.Contains(start + b);
                    DrawHexCell(
                        context,
                        value,
                        isEdited,
                        isEdited ? editedBrush : foreground,
                        layout.XFromByteIndex(b, hexZone: true),
                        layout.HexCellWidth,
                        y);
                    DrawAsciiCell(
                        context,
                        value,
                        asciiBrush,
                        layout.XFromByteIndex(b, hexZone: false),
                        layout.AsciiCellWidth,
                        y);
                }
            }

            var separatorPen = new Pen(headerBrush, 0.5);
            var separatorBottom = Math.Min(Bounds.Height, scrollY + viewportHeight);
            context.DrawLine(separatorPen, new Point(layout.HexZoneStart, scrollY), new Point(layout.HexZoneStart, separatorBottom));
            context.DrawLine(separatorPen, new Point(layout.AsciiZoneStart, scrollY), new Point(layout.AsciiZoneStart, separatorBottom));

            DrawCaret(context, caretBrush, layout);
        }

        private static string[] CreateHexByteText()
        {
            var text = new string[256];
            for (var i = 0; i < text.Length; i++)
            {
                text[i] = i.ToString("X2", CultureInfo.InvariantCulture);
            }

            return text;
        }

        private static string[] CreateAsciiCharText()
        {
            var text = new string[256];
            for (var value = 0; value < text.Length; value++)
            {
                text[value] = string.Create(1, value, static (span, code) => span[0] = (char)code);
            }

            return text;
        }

        private static string[] CreateAsciiByteText(System.Text.Encoding encoding)
        {
            var text = new string[256];
            for (var value = 0; value < text.Length; value++)
            {
                text[value] = DecodeByte(encoding, (byte)value);
            }

            return text;
        }

        private void DrawHeader(
            DrawingContext context,
            IBrush headerBrush,
            IBrush background,
            HexColumnLayout layout,
            string textColumnLabel)
        {
            context.FillRectangle(background, new Rect(0, 0, layout.ContentWidth, HexMetrics.HeaderHeight));
            DrawCenteredText(context, "Offset", 0, layout.OffsetWidth, 0, headerBrush);

            for (var i = 0; i < layout.BytesPerLine; i++)
            {
                ref var glyph = ref _headerCells[i];
                if (glyph.Text is null)
                {
                    glyph.SetText(CreateFormattedText(HexByteText[i], headerBrush));
                }

                DrawMeasuredCell(
                    context,
                    ref glyph,
                    layout.XFromByteIndex(i, hexZone: true),
                    layout.HexCellWidth,
                    0);
            }

            DrawCenteredText(context, textColumnLabel, layout.AsciiZoneStart, layout.AsciiWidth, 0, headerBrush);
            context.DrawLine(
                new Pen(headerBrush, 1),
                new Point(0, HexMetrics.HeaderHeight),
                new Point(layout.ContentWidth, HexMetrics.HeaderHeight));
        }

        private void DrawSelectionRects(
            DrawingContext context,
            int line,
            int start,
            int available,
            IBrush brush,
            HexColumnLayout layout)
        {
            if (!_hasSelection || _selectionAnchor < 0)
            {
                return;
            }

            var selStart = Math.Min(_selectionAnchor, _caretOffset);
            var selEnd = Math.Max(_selectionAnchor, _caretOffset);
            var lineStart = start;
            var lineEnd = start + available - 1;
            if (selEnd < lineStart || selStart > lineEnd)
            {
                return;
            }

            var from = Math.Max(selStart, lineStart);
            var to = Math.Min(selEnd, lineEnd);
            var y = HexMetrics.YFromLine(line);
            for (var b = from; b <= to; b++)
            {
                var byteInLine = (int)(b - start);

                var hexX = layout.XFromByteIndex(byteInLine, hexZone: true);
                context.FillRectangle(brush, new Rect(hexX, y, layout.HexCellWidth, HexMetrics.LineHeight));
            }
        }

        private void DrawCaret(DrawingContext context, IBrush brush, HexColumnLayout layout)
        {
            if (_data is null || _data.Length == 0)
            {
                return;
            }

            var caretLine = (int)HexMetrics.LineFromOffset(_caretOffset, layout.BytesPerLine);
            var scrollY = _scroll.Offset.Y;
            var viewportHeight = _scroll.Viewport.Height > 0 ? _scroll.Viewport.Height : Bounds.Height;
            var firstVisible = HexMetrics.LineFromY(scrollY);
            var lastVisible = firstVisible + (int)Math.Ceiling(viewportHeight / HexMetrics.LineHeight) + 1;
            if (caretLine < firstVisible || caretLine > lastVisible)
            {
                return;
            }

            var byteInLine = HexMetrics.ByteInLineFromOffset(_caretOffset, layout.BytesPerLine);
            var x = layout.XFromByteIndex(byteInLine, _hexEditZone);
            var y = HexMetrics.YFromLine(caretLine);
            context.FillRectangle(brush, new Rect(x, y + 2, 2, HexMetrics.LineHeight - 4));
        }

        private void DrawHexCell(
            DrawingContext context,
            byte value,
            bool edited,
            IBrush brush,
            double cellX,
            double cellWidth,
            double y)
        {
            ref var glyph = ref (edited ? ref _hexEditedCells[value] : ref _hexCells[value]);
            if (glyph.Text is null)
            {
                glyph.SetText(CreateFormattedText(HexByteText[value], brush));
            }

            DrawMeasuredCell(context, ref glyph, cellX, cellWidth, y);
        }

        private void DrawAsciiCell(
            DrawingContext context,
            byte value,
            IBrush brush,
            double cellX,
            double cellWidth,
            double y)
        {
            ref var glyph = ref _asciiCells[value];
            if (glyph.Text is null)
            {
                glyph.SetText(CreateFormattedText(_asciiByteText[value], brush));
            }

            DrawMeasuredCell(context, ref glyph, cellX, cellWidth, y);
        }

        private static void DrawMeasuredCell(
            DrawingContext context,
            ref CellGlyph glyph,
            double cellX,
            double cellWidth,
            double y)
        {
            if (glyph.Text is not { } text)
            {
                return;
            }

            context.DrawText(text, new Point(cellX + glyph.GetHorizontalOffset(cellWidth), y + glyph.VerticalOffset));
        }

        private static FormattedText CreateFormattedText(string text, IBrush brush) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonospaceTypeface, FontSize, brush);

        private static void DrawCenteredText(
            DrawingContext context,
            string text,
            double cellX,
            double cellWidth,
            double y,
            IBrush brush)
        {
            var formatted = CreateFormattedText(text, brush);
            var x = cellX + Math.Max(0, (cellWidth - formatted.Width) / 2);
            var textY = y + Math.Max(0, (HexMetrics.LineHeight - formatted.Height) / 2);
            context.DrawText(formatted, new Point(x, textY));
        }

        /// <summary>
        /// Cached layout of one fixed cell glyph with its centered offsets for the current cell width.
        /// <para>单个固定单元字形的排版缓存，含当前单元宽度下的居中偏移。</para>
        /// </summary>
        private struct CellGlyph
        {
            internal FormattedText? Text;
            internal double VerticalOffset;
            private double _horizontalOffset;
            private double _cellWidth;

            internal void SetText(FormattedText text)
            {
                Text = text;
                VerticalOffset = Math.Max(0, (HexMetrics.LineHeight - text.Height) / 2);
                _cellWidth = double.NaN;
                _horizontalOffset = 0;
            }

            internal double GetHorizontalOffset(double cellWidth)
            {
                if (_cellWidth != cellWidth && Text is { } text)
                {
                    _cellWidth = cellWidth;
                    _horizontalOffset = Math.Max(0, (cellWidth - text.Width) / 2);
                }

                return _horizontalOffset;
            }
        }

        private IBrush ResolveCachedBrush(string key, IBrush fallback)
        {
            if (_brushCache.TryGetValue(key, out var brush))
            {
                return brush;
            }

            brush = ResolveBrush(key) ?? fallback;
            _brushCache[key] = brush;
            return brush;
        }

        /// <summary>
        /// Drops the cached brushes and glyph layouts after a theme or encoding change; the monospace typeface and
        /// the font size are fixed for this control.
        /// <para>主题或编码变化后丢弃缓存的画刷与字形排版；该控件使用固定的等宽字体与字号。</para>
        /// </summary>
        private void InvalidateCachedGlyphs()
        {
            _brushCache.Clear();
            Array.Clear(_hexCells);
            Array.Clear(_hexEditedCells);
            Array.Clear(_asciiCells);
            Array.Clear(_headerCells);
        }

        private static IBrush? ResolveBrush(string key)
        {
            if (Application.Current is not { } app)
            {
                return null;
            }

            if (app.TryGetResource(key, app.ActualThemeVariant, out var value))
            {
                return value switch
                {
                    IBrush brush => brush,
                    Color color => new SolidColorBrush(color),
                    _ => null
                };
            }

            return null;
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            Focus();
            if (_data is null || _data.Length == 0)
            {
                return;
            }

            var position = e.GetPosition(this);
            var layout = HexMetrics.CreateLayout(Bounds.Width);
            var offset = GetByteOffsetFromPosition(position, layout);
            if (offset < 0)
            {
                return;
            }

            _hexEditZone = layout.IsHexZone(position.X);
            _selectionAnchor = offset;
            _hasSelection = false;
            _pendingHighNibble = false;
            _dragging = true;
            e.Pointer.Capture(this);
            SetCaret(offset);
            e.Handled = true;
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_dragging || _data is null || _data.Length == 0)
            {
                return;
            }

            var position = e.GetPosition(this);
            var offset = GetByteOffsetFromPosition(position, HexMetrics.CreateLayout(Bounds.Width));
            if (offset < 0)
            {
                return;
            }

            if (offset == _caretOffset)
            {
                e.Handled = true;
                return;
            }

            _hasSelection = true;
            SetCaret(offset);
            e.Handled = true;
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            if (e.Pointer.Captured == this)
            {
                e.Pointer.Capture(null);
            }

            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _dragging = false;
        }

        private long GetByteOffsetFromPosition(Point position, HexColumnLayout layout)
        {
            var line = HexMetrics.LineFromY(position.Y);
            if (line < 0 || line >= HexMetrics.TotalLines(_data!.Length, layout.BytesPerLine))
            {
                return -1;
            }

            var hexZone = layout.IsHexZone(position.X);
            var byteInLine = layout.ByteIndexFromX(position.X, hexZone);
            if (byteInLine < 0)
            {
                return -1;
            }

            var offset = (line * layout.BytesPerLine) + byteInLine;
            return Math.Min(offset, _data.Length - 1);
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (_data is null || _data.Length == 0)
            {
                return;
            }

            var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            var pageLines = Math.Max(1, (int)Math.Ceiling(_scroll.Viewport.Height / HexMetrics.LineHeight));
            var bytesPerLine = HexMetrics.CreateLayout(Bounds.Width).BytesPerLine;

            switch (e.Key)
            {
                case Key.Left:
                    MoveCaret(-1, extend);
                    break;
                case Key.Right:
                    MoveCaret(1, extend);
                    break;
                case Key.Up:
                    MoveCaret(-bytesPerLine, extend);
                    break;
                case Key.Down:
                    MoveCaret(bytesPerLine, extend);
                    break;
                case Key.PageUp:
                    MoveCaret(-(pageLines * bytesPerLine), extend);
                    break;
                case Key.PageDown:
                    MoveCaret(pageLines * bytesPerLine, extend);
                    break;
                case Key.Home:
                    SetCaret(_caretOffset - (_caretOffset % bytesPerLine));
                    if (extend)
                    {
                        _hasSelection = true;
                    }

                    break;
                case Key.End:
                    SetCaret((_caretOffset - (_caretOffset % bytesPerLine)) + (bytesPerLine - 1));
                    if (extend)
                    {
                        _hasSelection = true;
                    }

                    break;
                case Key.Back:
                    if (_pendingHighNibble)
                    {
                        _pendingHighNibble = false;
                    }
                    else
                    {
                        SetCaret(_caretOffset - 1);
                    }

                    break;
                case Key.Delete:
                    WriteByteAtCaret(0);
                    break;
                case Key.C when ctrl:
                    CopySelection();
                    break;
                case Key.V when ctrl:
                    PasteFromClipboard();
                    break;
                case Key.G when ctrl:
                    GotoRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case Key.Tab:
                    return;
                default:
                    return;
            }

            e.Handled = true;
        }

        private void OnTextInput(object? sender, TextInputEventArgs e)
        {
            if (_data is null || _data.Length == 0 || string.IsNullOrEmpty(e.Text))
            {
                return;
            }

            if (_hexEditZone)
            {
                foreach (var ch in e.Text)
                {
                    var nibble = HexNibble(ch);
                    if (nibble < 0)
                    {
                        continue;
                    }

                    InsertNibble((byte)nibble);
                }
            }
            else if (e.Text.Length == 1
                && (e.Text[0] is >= ' ' and <= '~'
                    || (!_textEncoding.IsSingleByte && e.Text[0] >= ' ')))
            {
                InsertAscii(e.Text[0]);
            }

            e.Handled = true;
        }

        private void MoveCaret(long delta, bool extend)
        {
            if (extend)
            {
                if (!_hasSelection)
                {
                    _selectionAnchor = _caretOffset;
                }

                _hasSelection = true;
            }
            else
            {
                _hasSelection = false;
                _selectionAnchor = -1;
            }

            SetCaret(_caretOffset + delta);
        }

        private void SetCaret(long offset)
        {
            if (_data is null)
            {
                return;
            }

            offset = Math.Clamp(offset, 0, Math.Max(0, _data.Length - 1));
            if (_caretOffset == offset)
            {
                InvalidateVisual();
                return;
            }

            _caretOffset = offset;
            _pendingHighNibble = false;
            EnsureCaretVisible();
            InvalidateVisual();
        }

        private void EnsureCaretVisible()
        {
            var bytesPerLine = HexMetrics.CreateLayout(Bounds.Width).BytesPerLine;
            var caretLine = (int)HexMetrics.LineFromOffset(_caretOffset, bytesPerLine);
            var scrollY = _scroll.Offset.Y;
            var viewportHeight = _scroll.Viewport.Height;
            var firstVisible = HexMetrics.LineFromY(scrollY);
            var lastVisible = firstVisible + (int)Math.Ceiling(viewportHeight / HexMetrics.LineHeight);

            if (caretLine < firstVisible)
            {
                _scroll.Offset = new Vector(
                    _scroll.Offset.X,
                    Math.Max(0, HexMetrics.YFromLine(caretLine) - (BufferLines * HexMetrics.LineHeight)));
            }
            else if (caretLine > lastVisible - 1)
            {
                _scroll.Offset = new Vector(
                    _scroll.Offset.X,
                    HexMetrics.YFromLine(caretLine) - Math.Max(0, viewportHeight - (2 * HexMetrics.LineHeight)));
            }
        }

        private void InsertNibble(byte nibble)
        {
            if (_data is null)
            {
                return;
            }

            if (!_pendingHighNibble)
            {
                WriteByteAtCaret((byte)((_data[_caretOffset] & 0x0F) | (nibble << 4)));
                _pendingHighNibble = true;
            }
            else
            {
                WriteByteAtCaret((byte)((_data[_caretOffset] & 0xF0) | nibble));
                _pendingHighNibble = false;
                AdvanceCaret();
            }
        }

        private void InsertAscii(char c)
        {

            var bytes = _textEncoding.GetBytes(c.ToString());
            WriteByteAtCaret(bytes.Length > 0 ? bytes[0] : (byte)c);
            AdvanceCaret();
        }

        private void WriteByteAtCaret(byte value)
        {
            if (_data is null || _caretOffset >= _data.Length)
            {
                return;
            }

            _data[_caretOffset] = value;
            _editedOffsets.Add(_caretOffset);
            BytesEdited?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        private void AdvanceCaret()
        {
            if (_caretOffset < (_data?.Length ?? 0) - 1)
            {
                SetCaret(_caretOffset + 1);
            }
        }

        private static int HexNibble(char c)
        {
            if (c is >= '0' and <= '9')
            {
                return c - '0';
            }

            if (c is >= 'a' and <= 'f')
            {
                return 10 + c - 'a';
            }

            if (c is >= 'A' and <= 'F')
            {
                return 10 + c - 'A';
            }

            return -1;
        }

        /// <summary>
        /// Decodes one byte to a text-column character using the given encoding; non-printable or
        /// undecodable bytes render as ".".
        /// <para>按指定编码把单字节解码为文本列显示字符；不可打印/无法解码时显示 "."。</para>
        /// </summary>
        private static string DecodeByte(System.Text.Encoding encoding, byte value)
        {
            if (value is >= 0x20 and < 0x7F)
            {
                return AsciiCharText[value];
            }

            if (!encoding.IsSingleByte && encoding != System.Text.Encoding.ASCII)
            {
                return ".";
            }

            var decoded = encoding.GetString([value]);
            return decoded.Length == 1 && decoded[0] is >= ' ' and <= '~' ? decoded : ".";
        }

        private async void CopySelection()
        {
            if (!_hasSelection || _selectionAnchor < 0 || _data is null)
            {
                return;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            var start = Math.Min(_selectionAnchor, _caretOffset);
            var end = Math.Max(_selectionAnchor, _caretOffset);
            var data = _data;
            var count = (int)(end - start + 1);
            var text = string.Create((count * 3) - 1, (Bytes: data, Start: start, Count: count), static (span, state) =>
            {
                for (var i = 0; i < state.Count; i++)
                {
                    if (i > 0)
                    {
                        span[(3 * i) - 1] = ' ';
                    }

                    state.Bytes[state.Start + i].TryFormat(span[(3 * i)..], out _, "X2", CultureInfo.InvariantCulture);
                }
            });

            await clipboard.SetTextAsync(text);
            await clipboard.FlushAsync();
        }

        private async void PasteFromClipboard()
        {
            if (_data is null)
            {
                return;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var bytes = ParseHexBytes(text);
            if (bytes.Count == 0)
            {
                return;
            }

            var start = _hasSelection && _selectionAnchor >= 0 ? Math.Min(_selectionAnchor, _caretOffset) : _caretOffset;
            var count = Math.Min(bytes.Count, _data.Length - start);
            for (var i = 0; i < count; i++)
            {
                _data[start + i] = bytes[i];
                _editedOffsets.Add(start + i);
            }

            if (count > 0)
            {
                _hasSelection = false;
                _selectionAnchor = -1;
                BytesEdited?.Invoke(this, EventArgs.Empty);
                SetCaret(start + count - 1);
            }
        }

        private static List<byte> ParseHexBytes(string text)
        {
            Span<char> digits = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
            var count = 0;
            foreach (var ch in text)
            {
                if (Uri.IsHexDigit(ch))
                {
                    digits[count++] = ch;
                }
            }

            var result = new List<byte>(count / 2);
            for (var i = 0; i + 1 < count; i += 2)
            {
                if (byte.TryParse(digits.Slice(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                {
                    result.Add(value);
                }
            }

            return result;
        }
    }
}