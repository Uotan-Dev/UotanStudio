using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace UotanStudio.Views.Controls;

/// <summary>
/// <para>基于 AvaloniaEdit <see cref="TextEditor"/> 的代码编辑器控件，布局选项在
/// <c>CodeEditor.axaml</c> 中声明，本文件承载可绑定属性桥接与 TextMate 主题语法高亮逻辑。</para>
/// A code editor control built on the AvaloniaEdit <see cref="TextEditor"/>. Layout options are
/// declared in <c>CodeEditor.axaml</c>; this file hosts the bindable-property bridging and the
/// TextMate theming / syntax-highlighting logic.
/// </summary>
public partial class CodeEditor : TextEditor
{
    public static readonly StyledProperty<string?> LanguageProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(Language));

    /// <summary>
    /// <para>可绑定的文本内容属性。底层 <see cref="TextEditor.Text"/> 是普通 CLR 属性，无法被
    /// XAML 编译绑定直接识别，因此这里注册一个桥接属性并在两侧同步。</para>
    /// Bindable text content property. The underlying <see cref="TextEditor.Text"/> is a plain CLR
    /// property that compiled XAML bindings cannot target directly, so a bridging property is
    /// registered here and kept in sync on both sides.
    /// </summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(Text), defaultValue: null, defaultBindingMode: BindingMode.TwoWay);

    protected override Type StyleKeyOverride => typeof(TextEditor);

    public string? Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    /// <summary>
    /// <para>获取或设置编辑器文本内容（绑定桥接到 <see cref="TextProperty"/>）。</para>
    /// Gets or sets the editor text content (binding bridged to <see cref="TextProperty"/>).
    /// </summary>
    public new string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// <para>TextMate 高亮安装实例（首次安装/主题变化时重建并释放旧实例；语言变化时仅更新语法）。</para>
    /// The TextMate highlight installation (created on first install or theme change, releasing the
    /// previous one; language changes only update the grammar).
    /// </summary>
    private TextMate.Installation? _installation;

    /// <summary>
    /// <para>当前安装 TextMate 时的注册选项（用于解析语法作用域名）。</para>
    /// The registry options used for the current TextMate installation (to resolve grammar scope names).
    /// </summary>
    private RegistryOptions? _options;

    /// <summary>
    /// <para>当前安装 TextMate 时的主题（用于检测主题切换）。</para>
    /// The theme used for the current TextMate installation (to detect theme switches).
    /// </summary>
    private ThemeName? _installedTheme;

    public CodeEditor()
    {
        InitializeComponent();

        TextChanged += (_, _) => SetValue(TextProperty, base.Text);
        ActualThemeVariantChanged += (_, _) => UpdateEditorTheme();
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        foreach (var scrollViewer in this.GetVisualDescendants().OfType<ScrollViewer>())
        {
            scrollViewer.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, true);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == LanguageProperty)
        {
            UpdateEditorTheme();
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

    private void UpdateEditorTheme()
    {
        var languageId = Language;

        if (string.IsNullOrEmpty(languageId))
        {
            return;
        }

        var theme = ActualThemeVariant == ThemeVariant.Light
                    ? ThemeName.LightPlus
                    : ThemeName.DarkPlus;

        try
        {

            if (_installation is null || _installedTheme != theme)
            {
                _installation?.Dispose();
                _options = new RegistryOptions(theme);
                _installation = this.InstallTextMate(_options);
                _installedTheme = theme;
            }

            var scopeName = _options!.GetScopeByLanguageId(languageId);
            _installation.SetGrammar(scopeName);
        }
        catch
        {

        }
    }
}