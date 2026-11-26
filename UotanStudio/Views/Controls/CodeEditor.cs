using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace UotanStudio.Views.Controls;

/// <summary>
/// A code editor control built on the AvaloniaEdit <see cref="TextEditor"/>. Layout options are
/// declared in <c>CodeEditor.axaml</c>; this file hosts the bindable-property bridging and the Tint
/// syntax-highlighting logic.
/// <para>基于 AvaloniaEdit <see cref="TextEditor"/> 的代码编辑器控件，布局选项在
/// <c>CodeEditor.axaml</c> 中声明，本文件承载可绑定属性桥接与 Tint 语法高亮逻辑。</para>
/// </summary>
public partial class CodeEditor : TextEditor
{
    /// <summary>
    /// The syntax colorizer attached to the text view once the template is applied; <c>null</c> while
    /// the editor has no template or when highlighting is unavailable.
    /// <para>模板应用后挂到文本视图上的语法着色器；编辑器尚无模板或高亮不可用时为 <c>null</c>。</para>
    /// </summary>
    private TintSyntaxColorizer? _colorizer;

    /// <summary>
    /// The language id the colorizer currently holds, tracked separately because
    /// <see cref="TintSyntaxColorizer.SetLanguage"/> resets the tokenizer on every call.
    /// <para>着色器当前持有的语言标识；因为 <see cref="TintSyntaxColorizer.SetLanguage"/> 每次调用都会
    /// 重置分词器，这里单独记录以跳过重复安装。</para>
    /// </summary>
    private string? _installedLanguage;

    /// <summary>
    /// The theme variant the colorizer currently resolves token colors against.
    /// <para>着色器当前用于解析 token 颜色的主题变体。</para>
    /// </summary>
    private ThemeVariant? _installedThemeVariant;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextEditor);

    /// <summary>
    /// Gets or sets the syntax language id used for syntax highlighting, as resolved by
    /// <see cref="UiAbstractions.SyntaxLanguageRegistry"/>; an empty value means no highlighting.
    /// <para>获取或设置语法高亮使用的语法语言标识，取值由
    /// <see cref="UiAbstractions.SyntaxLanguageRegistry"/> 解析；空值表示不做高亮。</para>
    /// </summary>
    [GeneratedStyledProperty]
    public partial string? Language { get; set; }

    /// <summary>
    /// Bindable text content property. The underlying <see cref="TextEditor.Text"/> is a plain CLR
    /// property that compiled XAML bindings cannot target directly, so a bridging property is
    /// registered here and kept in sync on both sides.
    /// <para>可绑定的文本内容属性。底层 <see cref="TextEditor.Text"/> 是普通 CLR 属性，无法被
    /// XAML 编译绑定直接识别，因此这里注册一个桥接属性并在两侧同步。</para>
    /// </summary>
    [GeneratedStyledProperty(DefaultBindingMode = BindingMode.TwoWay)]
    public new partial string? Text { get; set; }

    public CodeEditor()
    {
        InitializeComponent();

        TextChanged += (_, _) => SetValue(TextProperty, base.Text);
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        foreach (var scrollViewer in this.GetVisualDescendants().OfType<ScrollViewer>())
        {
            scrollViewer.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, true);
        }

        AttachColorizer();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == LanguageProperty)
        {
            ApplyLanguage();
        }
        else if (change.Property == TextProperty)
        {
            var text = GetValue(TextProperty) ?? string.Empty;
            if (!string.Equals(base.Text, text, StringComparison.Ordinal))
            {
                base.Text = text;
            }
        }

        base.OnPropertyChanged(change);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Dock panels detach and re-attach editors; the colorizer is disposable, so a re-attached
        // editor needs a fresh one or it would render without highlighting.
        if (_colorizer is null)
        {
            AttachColorizer();
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _colorizer?.Dispose();
        _colorizer = null;
        _installedLanguage = null;
        _installedThemeVariant = null;

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Creates the colorizer and installs the current language and theme; the text view only exists
    /// after the template is applied.
    /// <para>创建着色器并安装当前语言与主题；文本视图只有在模板应用后才存在。</para>
    /// </summary>
    private void AttachColorizer()
    {
        var textArea = TextArea;
        if (textArea is null)
        {
            return;
        }

        _colorizer?.Dispose();
        _colorizer = new TintSyntaxColorizer(textArea.TextView, textArea.Document, ReportHighlightingFailure);
        _installedLanguage = null;
        _installedThemeVariant = null;

        ApplyTheme();
        ApplyLanguage();
    }

    /// <summary>
    /// Switches the colorizer's theme when the control's theme variant changes.
    /// <para>控件的主题变体变化时切换着色器的主题。</para>
    /// </summary>
    private void ApplyTheme()
    {
        if (_colorizer is null)
        {
            return;
        }

        var themeVariant = ActualThemeVariant;
        if (_installedThemeVariant == themeVariant)
        {
            return;
        }

        _colorizer.SetTheme(themeVariant);
        _installedThemeVariant = themeVariant;
    }

    /// <summary>
    /// Installs the grammar for the current <see cref="Language"/> value, when it changed.
    /// <para>当前 <see cref="Language"/> 值变化时安装对应的语法。</para>
    /// </summary>
    private void ApplyLanguage()
    {
        if (_colorizer is null)
        {
            return;
        }

        var language = Language;
        if (string.Equals(_installedLanguage, language, StringComparison.Ordinal))
        {
            return;
        }

        _colorizer.SetLanguage(language);
        _installedLanguage = language;
    }

    /// <summary>
    /// Reports a highlighting failure without interrupting the user: the editor keeps rendering plain
    /// text, which is preferable to losing the document. Views may not reach application services, so
    /// the failure surfaces on the debug console instead of the unified log.
    /// <para>报告高亮失败但不打断用户：编辑器继续以纯文本渲染，这比丢失文档更可取。视图层不得直接访问
    /// 应用服务，因此失败输出到调试控制台而不是统一日志。</para>
    /// </summary>
    private static void ReportHighlightingFailure(Exception exception)
    {
        Debug.WriteLine($"Syntax highlighting failed: {exception}");
    }
}