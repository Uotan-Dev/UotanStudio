using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using AvaloniaEdit.Document;

namespace UotanStudio.Areas.Editor.Text;

/// <summary>
/// Text editor host control wrapping an AvaloniaEdit <see cref="AvaloniaEdit.TextEditor"/>
/// and exposing its content through a two-way <see cref="Text"/> dependency property.
/// <para>文本编辑器宿主控件：封装 AvaloniaEdit 的 <see cref="AvaloniaEdit.TextEditor"/>，
/// 通过双向 <see cref="Text"/> 依赖属性对外暴露其内容。</para>
/// </summary>
public partial class TextEditorHost : UserControl
{
    /// <summary>
    /// Identifies the <see cref="Text"/> dependency property (two-way by default).
    /// <para>标识 <see cref="Text"/> 依赖属性（默认双向绑定）。</para>
    /// </summary>
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<TextEditorHost, string>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Gets or sets the editor text content.
    /// <para>获取或设置编辑器文本内容。</para>
    /// </summary>
    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TextEditorHost"/> class and wires the
    /// editor document to the <see cref="Text"/> property.
    /// <para>初始化 <see cref="TextEditorHost"/> 类的新实例，并将编辑器文档与 <see cref="Text"/> 属性联通。</para>
    /// </summary>
    public TextEditorHost()
    {
        InitializeComponent();
        if (Editor.Document is null)
        {
            Editor.Document = new TextDocument();
        }

        Editor.Document.TextChanged += OnDocumentTextChanged;
        Editor.Document.Text = Text ?? string.Empty;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && Editor is not null && Editor.Document is not null)
        {
            var newValue = GetValue(TextProperty) ?? string.Empty;
            if (Editor.Document.Text != newValue)
            {
                Editor.Document.Text = newValue;
            }
        }
    }

    private void OnDocumentTextChanged(object? sender, System.EventArgs e)
    {
        if (Editor is not null && Editor.Document is not null)
        {
            SetCurrentValue(TextProperty, Editor.Document.Text);
        }
    }
}