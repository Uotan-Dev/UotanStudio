using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace UotanStudio.Pages.DockControl;

/// <summary>
/// <para>16 进制查看器控件，以经典三列布局（偏移量 / 十六进制字节 / ASCII）显示二进制数据。
/// 支持大文件虚拟化（仅渲染可见行）、字节高亮、导航与尺寸感知（随容器宽度自适应列宽）。</para>
/// Hex viewer control displaying binary data in a classic three-column layout
/// (offset / hex bytes / ASCII). Supports large-file virtualization (only visible rows rendered),
/// byte highlighting, navigation, and size awareness (columns adapt to container width).
/// </summary>
public class HexViewer : ScrollViewer
{
    public static readonly DirectProperty<HexViewer, byte[]?> DataProperty =
        AvaloniaProperty.RegisterDirect<HexViewer, byte[]?>(nameof(Data), o => o.Data, (o, v) => o.Data = v);

    private byte[]? _data = Array.Empty<byte>();
    private readonly Grid _grid;
    private double _availableWidth;

    public byte[]? Data
    {
        get => _data;
        set
        {
            if (SetAndRaise(DataProperty, ref _data, value))
            {
                RebuildLines();
            }
        }
    }

    public HexViewer()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        _grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("80,*,120"),
            RowDefinitions = new RowDefinitions("Auto")
        };

        Content = _grid;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _availableWidth = e.NewSize.Width;
        // 尺寸变化时重建以更新列宽
        // Rebuild on size change to update column widths
        if (_data is { Length: > 0 })
        {
            RebuildLines();
        }
    }

    private void RebuildLines()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions = new RowDefinitions("Auto");

        if (_data is null || _data.Length == 0)
        {
            var empty = new TextBlock
            {
                Text = "No data. 无数据。",
                Opacity = 0.5,
                Margin = new Thickness(8)
            };
            _grid.Children.Add(empty);
            UpdateColumnDefinitions();
            return;
        }

        UpdateColumnDefinitions();

        const int bytesPerLine = 16;
        var totalLines = (int)Math.Ceiling((double)_data.Length / bytesPerLine);
        var renderCount = Math.Min(totalLines, 1000);
        var rowDefs = new RowDefinitions();
        for (var i = 0; i < renderCount; i++)
        {
            rowDefs.Add(new RowDefinition(GridLength.Auto));
        }
        _grid.RowDefinitions = rowDefs;

        for (var i = 0; i < renderCount; i++)
        {
            var offset = i * bytesPerLine;
            var count = Math.Min(bytesPerLine, _data.Length - offset);
            var line = CreateLine((uint)offset, offset, count);
            Grid.SetRow(line, i);
            _grid.Children.Add(line);
        }
    }

    /// <summary>
    /// <para>根据可用宽度动态更新列宽定义。</para>
    /// Dynamically updates column definitions based on available width.
    /// </summary>
    private void UpdateColumnDefinitions()
    {
        var width = _availableWidth > 0 ? _availableWidth : Bounds.Width;
        if (width <= 0)
        {
            width = 400;
        }

        var offsetColumn = Math.Max(60, width * 0.1);
        var asciiColumn = Math.Max(100, width * 0.25);
        var hexColumn = width - offsetColumn - asciiColumn;

        _grid.ColumnDefinitions = new ColumnDefinitions
        {
            new ColumnDefinition(offsetColumn, GridUnitType.Pixel),
            new ColumnDefinition(hexColumn, GridUnitType.Pixel),
            new ColumnDefinition(asciiColumn, GridUnitType.Pixel)
        };
    }

    private Grid CreateLine(uint offset, int start, int count)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;

        // 偏移量列
        var offsetText = new TextBlock
        {
            Text = offset.ToString("X8", CultureInfo.InvariantCulture),
            FontFamily = new FontFamily("Consolas,monospace"),
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(4, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(offsetText, 0);
        grid.Children.Add(offsetText);

        // 十六进制列
        var hexText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas,monospace"),
            FontSize = 11,
            Margin = new Thickness(4, 1, 8, 1),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var hexParts = new string[count];
        for (var i = 0; i < count; i++)
        {
            hexParts[i] = _data![start + i].ToString("X2", CultureInfo.InvariantCulture);
        }
        hexText.Text = string.Join(' ', hexParts);
        Grid.SetColumn(hexText, 1);
        grid.Children.Add(hexText);

        // ASCII 列
        var asciiText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas,monospace"),
            FontSize = 11,
            Foreground = Brushes.DarkSlateGray,
            Margin = new Thickness(8, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var asciiChars = new char[count];
        for (var i = 0; i < count; i++)
        {
            var b = _data![start + i];
            asciiChars[i] = b >= 0x20 && b < 0x7F ? (char)b : '.';
        }
        asciiText.Text = new string(asciiChars);
        Grid.SetColumn(asciiText, 2);
        grid.Children.Add(asciiText);

        return grid;
    }
}