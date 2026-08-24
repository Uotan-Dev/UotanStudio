using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;

namespace UotanStudio.Areas.Editor.Structured;

/// <summary>
/// <para>只读结构化文档视图：对 JSON/XML 文本进行格式化只读展示。</para>
/// Read-only structured document view: formats and displays JSON/XML text read-only.
/// </summary>
public partial class StructuredDocumentView : UserControl
{
    /// <summary>
    /// <para>标识 <see cref="Data"/> 依赖属性。</para>
    /// Identifies the <see cref="Data"/> dependency property.
    /// </summary>
    public static readonly StyledProperty<string?> DataProperty =
        AvaloniaProperty.Register<StructuredDocumentView, string?>(nameof(Data), null);

    /// <summary>
    /// <para>获取或设置要渲染的 JSON/XML 文本。</para>
    /// Gets or sets the JSON/XML text to render.
    /// </summary>
    public string? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>
    /// <para>初始化 <see cref="StructuredDocumentView"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="StructuredDocumentView"/> class.
    /// </summary>
    public StructuredDocumentView()
    {
        InitializeComponent();
        DataProperty.Changed.AddClassHandler<StructuredDocumentView>(OnDataChanged);
    }

    private static void OnDataChanged(StructuredDocumentView view, AvaloniaPropertyChangedEventArgs args)
        => view.RenderContent();

    private void RenderContent()
    {
        var text = Data;
        if (string.IsNullOrWhiteSpace(text))
        {
            ContentTextBlock.Text = string.Empty;
            return;
        }

        ContentTextBlock.Text = TryFormatJson(text) ?? TryFormatXml(text) ?? text;
    }

    private static string? TryFormatJson(string text)
    {
        try
        {
            // JsonNode 序列化不依赖反射，AOT 安全。
            // JsonNode serialization does not rely on reflection; AOT-safe.
            var node = JsonNode.Parse(text);
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryFormatXml(string text)
    {
        try
        {
            var document = XDocument.Parse(text);
            return document.ToString();
        }
        catch (XmlException)
        {
            return null;
        }
    }
}