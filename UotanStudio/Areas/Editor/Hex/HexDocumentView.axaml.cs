using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;

namespace UotanStudio.Areas.Editor.Hex;

/// <summary>
/// <para>只读十六进制文档视图：以偏移/hex/ASCII 三列渲染二进制内容，大文件仅显示前 64KB。</para>
/// Read-only hex document view: renders binary content in offset/hex/ASCII columns,
/// showing only the first 64KB for large files.
/// </summary>
public partial class HexDocumentView : UserControl
{
    private const int MaxPreviewBytes = 64 * 1024;
    private const int BytesPerRow = 16;

    /// <summary>
    /// <para>标识 <see cref="Data"/> 依赖属性。</para>
    /// Identifies the <see cref="Data"/> dependency property.
    /// </summary>
    public static readonly StyledProperty<byte[]?> DataProperty =
        AvaloniaProperty.Register<HexDocumentView, byte[]?>(nameof(Data), null);

    /// <summary>
    /// <para>获取或设置要渲染的二进制数据。</para>
    /// Gets or sets the binary data to render.
    /// </summary>
    public byte[]? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>
    /// <para>初始化 <see cref="HexDocumentView"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="HexDocumentView"/> class.
    /// </summary>
    public HexDocumentView()
    {
        InitializeComponent();
        DataProperty.Changed.AddClassHandler<HexDocumentView>(OnDataChanged);
    }

    private static void OnDataChanged(HexDocumentView view, AvaloniaPropertyChangedEventArgs args)
        => view.RenderHex();

    private void RenderHex()
    {
        var data = Data;
        if (data is null || data.Length == 0)
        {
            HexTextBlock.Text = string.Empty;
            return;
        }

        var length = Math.Min(data.Length, MaxPreviewBytes);
        var builder = new StringBuilder((length / BytesPerRow) * 80 + 64);
        for (var offset = 0; offset < length; offset += BytesPerRow)
        {
            builder.Append(offset.ToString("X8"));
            builder.Append("  ");

            var end = Math.Min(offset + BytesPerRow, length);
            for (var i = offset; i < end; i++)
            {
                builder.Append(data[i].ToString("X2"));
                builder.Append(' ');
            }

            var padding = BytesPerRow - (end - offset);
            if (padding > 0)
            {
                builder.Append(' ', padding * 3);
            }

            builder.Append(' ');
            for (var i = offset; i < end; i++)
            {
                var value = data[i];
                builder.Append(value is >= 0x20 and <= 0x7e ? (char)value : '.');
            }

            builder.AppendLine();
        }

        HexTextBlock.Text = builder.ToString();
    }
}