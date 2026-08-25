using System;
using Avalonia;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace UotanStudio.Pages.DockControl;

public class CodeEditor : TextEditor
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

    public CodeEditor()
    {
        ShowLineNumbers = true;
        FontFamily = FontFamily.Parse("Consolas");
        FlowDirection = FlowDirection.LeftToRight;

        TextChanged += (_, _) => SetValue(TextProperty, base.Text);
        ActualThemeVariantChanged += (_, _) => UpdateEditorTheme();
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

        var options = new RegistryOptions(theme);

        var installation = this.InstallTextMate(options);

        try
        {
            var scopeName = options.GetScopeByLanguageId(languageId);
            installation.SetGrammar(scopeName);
        }
        catch
        {
            // 无效的 languageId 时忽略，不安装语法高亮，避免崩溃。
            // Invalid language ids are ignored: syntax highlighting is skipped instead of crashing.
        }
    }
}