using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace UotanStudio.Pages.DockControl;

/// <summary>
/// <para>可编辑且支持大文件虚拟化的 16 进制查看器控件。以经典三段式布局（偏移量 / 十六进制字节 / ASCII）
/// 显示二进制数据，支持逐字节点击定位、十六进制或 ASCII 编辑、键盘导航，并通过行级虚拟化实现超大文件
/// 的流畅滚动。</para>
/// An editable, large-file-virtualized hex viewer control. Displays binary data in the classic three-pane
/// layout (offset / hex bytes / ASCII), supports per-byte click positioning, hex or ASCII editing, keyboard
/// navigation, and smooth scrolling for very large files via row-level virtualization.
/// </summary>
public class HexViewer : ScrollViewer
{
    /// <summary>
    /// <para>绑定源数据（原始字节数组）。编辑会就地写回该数组。</para>
    /// The bound source data (raw byte array). Edits are written back to this array in place.
    /// </summary>
    public static readonly DirectProperty<HexViewer, byte[]?> DataProperty =
        AvaloniaProperty.RegisterDirect<HexViewer, byte[]?>(nameof(Data), o => o.Data, (o, v) => o.Data = v);

    /// <summary>
    /// <para>发生一次字节编辑后触发，供宿主（如打开的文档视图）标记为已修改。</para>
    /// Raised after a single byte edit, so a host (e.g. the document view) can mark itself dirty.
    /// </summary>
    public event EventHandler? BytesEdited;

    private const int BytesPerLine = 16;
    private const int LineHeight = 20;
    private const int HeaderHeight = 20;
    private const int BufferLines = 4;

    private const int OffsetWidth = 84;
    private const int HexCellWidth = 22;
    private const int AsciiCellWidth = 13;

    private static readonly int ContentWidth = OffsetWidth + (BytesPerLine * HexCellWidth) + (BytesPerLine * AsciiCellWidth);

    private byte[]? _data = Array.Empty<byte>();
    private readonly Canvas _header;
    private readonly Canvas _body;
    private readonly List<HexRow> _pool = [];
    private readonly List<HexRow> _activeRows = [];

    private int _totalLines;
    private int _startLine = -1;
    private int _viewportLines;
    private long _caretOffset;
    private bool _pendingHighNibble;
    private bool _hexEditZone = true;
    private int _lastRenderedCaretLine = -1;

    // 渲染请求去抖：_rebuildQueued 表示已有一个延后回调在途，_pendingRebuild 表示视口就绪前仍需重建。
    // Debounce for rebuild requests: _rebuildQueued means a deferred callback is already in flight,
    // _pendingRebuild means a rebuild is still required until the viewport becomes valid.
    private bool _rebuildQueued;
    private bool _pendingRebuild;

    /// <summary>
    /// <para>获取原始数据数组的引用（内部编辑会就地修改它）。</para>
    /// Gets the reference to the raw data array (internal edits mutate it in place).
    /// </summary>
    public byte[]? Data
    {
        get => _data;
        set
        {
            if (SetAndRaise(DataProperty, ref _data, value))
            {
                _totalLines = (_data is { Length: > 0 }) ? (int)Math.Ceiling((double)_data.Length / BytesPerLine) : 0;
                _caretOffset = 0;
                _pendingHighNibble = false;
                _startLine = -1;

                // 立即校正内容高度，使滚动范围（Extent）在首帧渲染前就正确，避免“无法滚动”。
                // Fix the content height immediately so the scroll extent is correct before the first
                // render pass — this is what makes vertical scrolling work reliably.
                _body.Height = HeaderHeight + ((long)_totalLines * LineHeight);

                if (Offset.Y > 0)
                {
                    ScrollToVertical(0);
                }

                RequestRebuild();
            }
        }
    }

    /// <summary>
    /// <para>构造 16 进制查看器：初始化画布与滚动策略，并订阅滚动、布局、指针与键盘事件。</para>
    /// Constructs the hex viewer: initializes the canvases and scrolling policy, and subscribes to scroll,
    /// layout, pointer and keyboard events.
    /// </summary>
    public HexViewer()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        Content = new StackPanel
        {
            Children =
            {
                (_header = new Canvas { Width = ContentWidth, Height = HeaderHeight, IsHitTestVisible = false }),
                (_body = new Canvas { Width = ContentWidth })
            }
        };

        _body.Height = HeaderHeight; // 占位，数据设置后由 Data setter / UpdateVisibleRows 校正。
        ScrollChanged += OnScrollChanged;
        LayoutUpdated += OnLayoutUpdated;
        AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);
        AddHandler(TextInputEvent, OnTextInput, handledEventsToo: true);
        Focusable = true;
    }

    /// <summary>
    /// <para>请求一次可见行重建。所有对 <c>Children</c> 的修改一律延后到布局空闲，避免在布局期间
    /// 修改视觉树抛异常；若视口尚未就绪（高度为 0），则保留待重建标记，由 <see cref="OnLayoutUpdated"/>
    /// 在布局完成后重试。</para>
    /// Requests a visible-row rebuild. All <c>Children</c> mutations are deferred until layout idle to avoid
    /// exceptions from mutating the visual tree during layout; if the viewport is not ready yet (zero height),
    /// the pending flag is kept so <see cref="OnLayoutUpdated"/> retries after the layout pass.
    /// </summary>
    private void RequestRebuild()
    {
        _pendingRebuild = true;
        if (_rebuildQueued)
        {
            return;
        }

        _rebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            if (!_pendingRebuild)
            {
                return;
            }

            if (Viewport.Height > 0)
            {
                _pendingRebuild = false;
                UpdateVisibleRows();
            }
            // 视口未就绪：保持 _pendingRebuild，等待 OnLayoutUpdated 重试。
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// <para>布局完成后若仍有待重建标记且视口有效，则执行重建。这是首次挂载/首次布局后渲染的兜底触发点。</para>
    /// After a layout pass, rebuilds if a rebuild is still pending and the viewport is valid. This is the
    /// fallback trigger for rendering after the first attach/layout.
    /// </summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pendingRebuild && Viewport.Height > 0)
        {
            _pendingRebuild = false;
            UpdateVisibleRows();
        }
    }

    /// <summary>
    /// <para>指针按下：计算并定位光标字节，同时设置编辑区域（十六进制或 ASCII）。</para>
    /// Pointer press: computes and positions the caret byte and sets the edit zone (hex or ASCII).
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        if (_totalLines == 0)
        {
            return;
        }

        var position = e.GetPosition(_body);
        var line = (int)((position.Y - HeaderHeight) / LineHeight);
        if (line < 0 || line >= _totalLines)
        {
            return;
        }

        var absoluteOffset = GetAbsoluteByteIndex(line, position.X);
        if (absoluteOffset < 0)
        {
            return;
        }

        var hexStart = (int)OffsetWidth;
        var hexEnd = (int)(OffsetWidth + (BytesPerLine * HexCellWidth));
        _hexEditZone = position.X >= hexStart && position.X < hexEnd;

        SetCaret(absoluteOffset);
    }

    /// <summary>
    /// <para>根据行内指针相对坐标计算所在字节索引，未命中有效列时返回 -1。</para>
    /// Computes the byte index from a row-relative pointer coordinate; returns -1 when outside a data column.
    /// </summary>
    private static int GetByteIndexInLine(double x, bool hexZone)
    {
        if (hexZone)
        {
            if (x < OffsetWidth || x >= OffsetWidth + (BytesPerLine * HexCellWidth))
            {
                return -1;
            }

            var hexIndex = (int)((x - OffsetWidth) / HexCellWidth);
            return Math.Clamp(hexIndex, 0, BytesPerLine - 1);
        }

        var asciiStart = OffsetWidth + (BytesPerLine * HexCellWidth);
        if (x < asciiStart || x >= asciiStart + (BytesPerLine * AsciiCellWidth))
        {
            return -1;
        }

        var asciiIndex = (int)((x - asciiStart) / AsciiCellWidth);
        return Math.Clamp(asciiIndex, 0, BytesPerLine - 1);
    }

    private long GetAbsoluteByteIndex(int line, double x)
    {
        var hexZone = x < OffsetWidth + (BytesPerLine * HexCellWidth);
        var inLine = GetByteIndexInLine(x, hexZone);
        if (inLine < 0)
        {
            return -1;
        }

        return (line * BytesPerLine) + inLine;
    }

    /// <summary>
    /// <para>控件挂载到视觉树后请求重建可见行（延后执行，避免布局期修改 Children 抛异常）。</para>
    /// After the control is attached, requests a visible-row rebuild (deferred to avoid mutating Children
    /// during layout).
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestRebuild();
    }

    /// <summary>
    /// <para>滚动变化时按需重建可见行窗口。</para>
    /// Rebuilds the visible row window when scrolling changes.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => RequestRebuild();

    /// <summary>
    /// <para>尺寸变化时重建可见窗口（延后到布局空闲，避免布局期修改 Children 抛异常）。</para>
    /// Rebuilds the visible window when the size changes (deferred to layout idle).
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RequestRebuild();
    }

    private void UpdateVisibleRows()
    {
        _body.Height = HeaderHeight + ((long)_totalLines * LineHeight);

        if (_totalLines == 0)
        {
            ClearActiveRows();
            _startLine = -1;
            return;
        }

        // 视口高度：布局完成后取 Viewport.Height，否则回退到自身 Bounds 高度，避免为 0 导致可见行极少。
        // Viewport height: prefer Viewport.Height after layout, otherwise fall back to Bounds.Height so that
        // a zero viewport never collapses the visible row count.
        var viewportHeight = Viewport.Height > 0 ? Viewport.Height : Bounds.Height;
        if (viewportHeight <= 0)
        {
            return;
        }

        var scrollY = Offset.Y;
        var firstLine = Math.Max(0, (int)Math.Floor((scrollY - HeaderHeight) / LineHeight) - BufferLines);
        var visible = (int)Math.Ceiling((scrollY + viewportHeight - HeaderHeight) / LineHeight) + BufferLines;
        firstLine = Math.Clamp(firstLine, 0, _totalLines - 1);
        visible = Math.Max(1, Math.Min(visible, _totalLines - firstLine));

        if (firstLine == _startLine && visible == _viewportLines)
        {
            return;
        }

        _startLine = firstLine;
        _viewportLines = visible;

        ClearActiveRows();
        for (var offset = 0; offset < visible; offset++)
        {
            var line = firstLine + offset;
            var row = GetOrCreateRow();
            row.SetLine(line, _data!, _hexEditZone);
            Canvas.SetTop(row.Root, HeaderHeight + (line * LineHeight));
            Canvas.SetLeft(row.Root, 0);
            _body.Children.Add(row.Root);
            _activeRows.Add(row);
        }

        UpdateCaretVisual();
    }

    private void ClearActiveRows()
    {
        foreach (var row in _activeRows)
        {
            _body.Children.Remove(row.Root);
            _pool.Add(row);
        }

        _activeRows.Clear();
        _lastRenderedCaretLine = -1;
    }

    private HexRow GetOrCreateRow()
    {
        if (_pool.Count > 0)
        {
            var last = _pool[^1];
            _pool.RemoveAt(_pool.Count - 1);
            return last;
        }

        return new HexRow(BytesPerLine, HexCellWidth, AsciiCellWidth);
    }

    private void SetCaret(long offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, (_data?.Length ?? 0) - 1));
        if (_caretOffset == offset)
        {
            return;
        }

        _caretOffset = offset;
        _pendingHighNibble = false;
        EnsureCaretVisible();
        UpdateCaretVisual();
    }

    private void EnsureCaretVisible()
    {
        var caretLine = (int)(_caretOffset / BytesPerLine);
        var scrollY = Offset.Y;
        var firstVisible = (int)((scrollY - HeaderHeight) / LineHeight);
        var lastVisible = firstVisible + (int)Math.Ceiling(Viewport.Height / LineHeight);

        if (caretLine < firstVisible)
        {
            ScrollToVertical(Math.Max(0, (caretLine * LineHeight) + HeaderHeight - (BufferLines * LineHeight)));
        }
        else if (caretLine > lastVisible - 1)
        {
            ScrollToVertical((caretLine * LineHeight) + HeaderHeight - Math.Max(0, Viewport.Height - (2 * LineHeight)));
        }
    }

    /// <summary>
    /// <para>设置垂直滚动偏移（钳制到非负），用于按行定位光标。</para>
    /// Sets the vertical scroll offset (clamped to non-negative), used to bring the caret line into view.
    /// </summary>
    private void ScrollToVertical(double y)
    {
        y = Math.Max(0, y);
        if (Math.Abs(Offset.Y - y) > 0.001)
        {
            Offset = new Vector(Offset.X, y);
        }
    }

    private void UpdateCaretVisual()
    {
        var caretLine = (int)(_caretOffset / BytesPerLine);
        var byteInLine = (int)(_caretOffset % BytesPerLine);

        var caretRow = FindRow(caretLine);
        if (caretRow is null)
        {
            _lastRenderedCaretLine = -1;
            return;
        }

        if (_lastRenderedCaretLine != caretLine)
        {
            foreach (var row in _activeRows)
            {
                row.SetSelection(clear: true);
            }

            _lastRenderedCaretLine = caretLine;
        }

        caretRow.SetSelection(clear: false, byteIndex: byteInLine);
    }

    private HexRow? FindRow(int line)
    {
        foreach (var row in _activeRows)
        {
            if (row.Line == line)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// <para>键盘导航与功能键处理（方向键、翻页、Home/End、退格、删除）。</para>
    /// Key-down handling for navigation and function keys (arrows, page up/down, Home/End, backspace, delete).
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_data is null || _data.Length == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var handled = true;
        switch (e.Key)
        {
            case Key.Left:
                SetCaret(_caretOffset - 1);
                break;
            case Key.Right:
                SetCaret(_caretOffset + 1);
                break;
            case Key.Up:
                MoveByLine(-1);
                break;
            case Key.Down:
                MoveByLine(1);
                break;
            case Key.PageUp:
                MoveByLine(-Math.Max(1, (int)Math.Ceiling(Viewport.Height / LineHeight)));
                break;
            case Key.PageDown:
                MoveByLine(Math.Max(1, (int)Math.Ceiling(Viewport.Height / LineHeight)));
                break;
            case Key.Home:
                SetCaret(_caretOffset - (_caretOffset % BytesPerLine));
                break;
            case Key.End:
                SetCaret((_caretOffset - (_caretOffset % BytesPerLine)) + (BytesPerLine - 1));
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
            case Key.Tab:
                handled = false;
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// <para>文本输入处理：十六进制区域输入两位半字节，ASCII 区域写入可打印字符。</para>
    /// Text-input handling: the hex zone takes two nibbles per byte, and the ASCII zone writes printable chars.
    /// </summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_data is null || _data.Length == 0 || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        var text = e.Text;
        if (_hexEditZone)
        {
            foreach (var ch in text)
            {
                var nibble = HexNibble(ch);
                if (nibble < 0)
                {
                    continue;
                }

                InsertNibble((byte)nibble);
            }
        }
        else
        {
            if (text.Length == 1 && text[0] >= 0x20 && text[0] <= 0x7E)
            {
                InsertAscii(text[0]);
            }
        }

        e.Handled = true;
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

    private void MoveByLine(int deltaLines) => SetCaret(_caretOffset + (deltaLines * BytesPerLine));

    private void InsertNibble(byte nibble)
    {
        if (!_pendingHighNibble)
        {
            WriteByteAtCaret((byte)((_data![_caretOffset] & 0x0F) | (nibble << 4)));
            _pendingHighNibble = true;
        }
        else
        {
            WriteByteAtCaret((byte)((_data![_caretOffset] & 0xF0) | nibble));
            _pendingHighNibble = false;
            AdvanceCaret();
        }

        UpdateCaretVisual();
    }

    private void InsertAscii(char c)
    {
        WriteByteAtCaret((byte)c);
        AdvanceCaret();
    }

    private void WriteByteAtCaret(byte value)
    {
        if (_data is null || _caretOffset >= _data.Length)
        {
            return;
        }

        _data[_caretOffset] = value;
        UpdateByteVisuals();
        BytesEdited?.Invoke(this, EventArgs.Empty);
    }

    private void AdvanceCaret()
    {
        if (_caretOffset < (_data?.Length ?? 0) - 1)
        {
            SetCaret(_caretOffset + 1);
        }
    }

    private void UpdateByteVisuals()
    {
        var line = (int)(_caretOffset / BytesPerLine);
        var row = FindRow(line);
        row?.RefreshFrom(_data!);
    }

    /// <summary>
    /// <para>单个可复用的行渲染者，容纳偏移量、16 个十六进制单元格与 16 个 ASCII 单元格。</para>
    /// A single reusable row renderer holding the offset and 16 hex plus 16 ASCII cell controls.
    /// </summary>
    private sealed class HexRow
    {
        private readonly TextBlock _offset;
        private readonly TextBlock[] _hex;
        private readonly TextBlock[] _ascii;

        public Grid Root { get; }
        public int Line { get; private set; } = -1;

        public HexRow(int bytesPerLine, int hexCellWidth, int asciiCellWidth)
        {
            Root = new Grid();
            var cols = new ColumnDefinitions();
            cols.Add(new ColumnDefinition(OffsetWidth, GridUnitType.Pixel));
            for (var i = 0; i < bytesPerLine; i++)
            {
                cols.Add(new ColumnDefinition(hexCellWidth, GridUnitType.Pixel));
            }

            for (var i = 0; i < bytesPerLine; i++)
            {
                cols.Add(new ColumnDefinition(asciiCellWidth, GridUnitType.Pixel));
            }

            Root.ColumnDefinitions = cols;

            _offset = CreateCell(isAscii: false);
            Grid.SetColumn(_offset, 0);
            Root.Children.Add(_offset);

            _hex = new TextBlock[bytesPerLine];
            for (var i = 0; i < bytesPerLine; i++)
            {
                _hex[i] = CreateCell(isAscii: false);
                Grid.SetColumn(_hex[i], 1 + i);
                Root.Children.Add(_hex[i]);
            }

            _ascii = new TextBlock[bytesPerLine];
            for (var i = 0; i < bytesPerLine; i++)
            {
                _ascii[i] = CreateCell(isAscii: true);
                Grid.SetColumn(_ascii[i], 1 + bytesPerLine + i);
                Root.Children.Add(_ascii[i]);
            }
        }

        private static TextBlock CreateCell(bool isAscii)
        {
            return new TextBlock
            {
                FontFamily = FontFamily.Parse("Consolas"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isAscii ? Brushes.Gray : null,
                TextTrimming = TextTrimming.None
            };
        }

        public void SetLine(int line, byte[] data, bool _)
        {
            Line = line;
            var start = line * BytesPerLine;
            var available = Math.Min(BytesPerLine, data.Length - start);

            _offset.Text = ((uint)start).ToString("X8", CultureInfo.InvariantCulture);
            _offset.Foreground = Brushes.Gray;

            for (var i = 0; i < BytesPerLine; i++)
            {
                var b = i < available ? data[start + i] : (byte)0;
                if (i < available)
                {
                    _hex[i].Text = b.ToString("X2", CultureInfo.InvariantCulture);
                    _ascii[i].Text = b >= 0x20 && b < 0x7F ? ((char)b).ToString() : ".";
                    _ascii[i].Opacity = b >= 0x20 && b < 0x7F ? 1.0 : 0.5;
                }
                else
                {
                    _hex[i].Text = "  ";
                    _ascii[i].Text = " ";
                    _ascii[i].Opacity = 1.0;
                }
            }
        }

        public void RefreshFrom(byte[] data)
        {
            var start = Line * BytesPerLine;
            var available = Math.Min(BytesPerLine, data.Length - start);
            for (var i = 0; i < available; i++)
            {
                var b = data[start + i];
                _hex[i].Text = b.ToString("X2", CultureInfo.InvariantCulture);
                _ascii[i].Text = b >= 0x20 && b < 0x7F ? ((char)b).ToString() : ".";
                _ascii[i].Opacity = b >= 0x20 && b < 0x7F ? 1.0 : 0.5;
            }
        }

        public void SetSelection(bool clear, int byteIndex = -1)
        {
            for (var i = 0; i < _hex.Length; i++)
            {
                var selected = !clear && i == byteIndex;
                _hex[i].Background = selected ? SelectionBrush : null;
                _ascii[i].Background = selected ? SelectionBrush : null;
            }
        }

        private static readonly IBrush SelectionBrush =
            new SolidColorBrush(Color.FromArgb(0x66, 0x59, 0xC2, 0xFF));
    }
}
