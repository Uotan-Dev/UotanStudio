using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Tint;
using Tint.Grammars;
using Tint.Themes;
using TintFontStyle = Tint.FontStyle;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Bridges the Tint tokenizer to AvaloniaEdit's line transformers: the document is tokenized with a
/// VS Code TextMate grammar and theme, and each line's tokens become text-run foreground, typeface,
/// decorations and background.
/// <para>把 Tint 分词器接到 AvaloniaEdit 的行变换器上：用 VS Code 的 TextMate 语法与主题对文档分词，
/// 并把每行 token 转换为文本运行的前景色、字体、装饰与背景。</para>
/// </summary>
/// <remarks>
/// <para>AvaloniaEdit asks for lines in arbitrary order, while a TextMate tokenizer only advances
/// forward from the previous line's state. The tokenization of the document's leading lines is
/// therefore cached as a contiguous prefix: coloring line N tokenizes lines K+1..N only, where K is
/// the number of lines already cached. An edit on line L drops the cache from L onward, because the
/// edit changes the state entering that line. Blank lines are tokenized too: they can close a
/// block comment or a heredoc, so skipping them would leave the following lines in a stale state.</para>
/// <para>AvaloniaEdit 按任意顺序请求行，而 TextMate 分词器只能从上一行状态向前推进，因此这里把文档
/// 前若干行的分词结果按连续前缀缓存：为第 N 行着色时只分词第 K+1..N 行（K 为已缓存行数）。第 L 行发生
/// 编辑时从 L 起丢弃缓存，因为编辑改变了进入该行的状态。空白行同样要分词：它可能结束块注释或 heredoc，
/// 跳过会让后续行沿用过期状态。</para>
/// </remarks>
internal sealed class TintSyntaxColorizer : DocumentColorizingTransformer, IDisposable
{
    /// <summary>
    /// Maps the language ids resolved by <see cref="UiAbstractions.SyntaxLanguageRegistry"/> to the
    /// VS Code grammars bundled with Tint.
    /// <para>把 <see cref="UiAbstractions.SyntaxLanguageRegistry"/> 解析出的语言标识映射到 Tint 内置的
    /// VS Code 语法。</para>
    /// </summary>
    private static readonly Dictionary<string, IGrammarSource> s_grammarsByLanguageId =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ini"] = TintGrammars.Ini,
            ["xml"] = TintGrammars.Xml,
            ["json"] = TintGrammars.Json,
            ["shellscript"] = TintGrammars.ShellScript,
            ["makefile"] = TintGrammars.Makefile,
            ["c"] = TintGrammars.C,
            ["cpp"] = TintGrammars.Cpp,
            ["csharp"] = TintGrammars.CSharp,
            ["markdown"] = TintGrammars.Markdown,
            ["yaml"] = TintGrammars.Yaml,
            ["diff"] = TintGrammars.Diff,
            ["log"] = TintGrammars.Log,

            ["python"] = TintGrammars.Python,
            ["javascript"] = TintGrammars.JavaScript,
            ["typescript"] = TintGrammars.TypeScript,
            ["java"] = TintGrammars.Java,
            ["go"] = TintGrammars.Go,
            ["rust"] = TintGrammars.Rust,
            ["ruby"] = TintGrammars.Ruby,
            ["php"] = TintGrammars.Php,
            ["swift"] = TintGrammars.Swift,
            ["sql"] = TintGrammars.Sql,
            ["css"] = TintGrammars.Css,
            ["scss"] = TintGrammars.Scss,
            ["html"] = TintGrammars.Html,
            ["powershell"] = TintGrammars.PowerShell,
            ["batch"] = TintGrammars.Batch,
            ["dockerfile"] = TintGrammars.Dockerfile,
            ["kotlin"] = TintGrammars.Java,
            ["groovy"] = TintGrammars.Groovy,
            ["objective-c"] = TintGrammars.ObjectiveC,

            ["dts"] = DtsGrammar.Instance,
            ["toml"] = TomlGrammar.Instance,
            ["asm"] = AsmX86Grammar.Instance,
        };

    /// <summary>
    /// The grammar set registered once per process. A grammar that includes another (Markdown fenced
    /// blocks, XML and HTML embedding, YAML 1.2 anchors, PHP inside HTML) resolves only when the
    /// included grammar is registered as well.
    /// <para>每进程只注册一次的语法集合。包含其它语法的语法（Markdown 围栏代码块、XML 与 HTML 内嵌、
    /// YAML 1.2 锚点、HTML 中的 PHP）只有在被包含的语法也已注册时才能解析。</para>
    /// </summary>
    /// <para>每进程只注册一次的语法集合。包含其它语法的语法（Markdown 围栏代码块、XML 与 HTML 内嵌、
    /// YAML 1.2 锚点）只有在被包含的语法也已注册时才能解析。</para>
    /// </summary>
    private static readonly Lazy<GrammarRegistry> s_registry = new(CreateRegistry);

    /// <summary>
    /// The dark theme, resolved once because <see cref="Theme.FromSource"/> rebuilds the color map.
    /// <para>深色主题，只解析一次，因为 <see cref="Theme.FromSource"/> 每次都会重建颜色表。</para>
    /// </summary>
    private static readonly Lazy<Theme> s_darkTheme = new(() => Theme.FromSource(TintThemes.DarkPlus));

    /// <summary>
    /// The light theme, resolved once; pairs with <see cref="s_darkTheme"/>.
    /// <para>浅色主题，只解析一次，与 <see cref="s_darkTheme"/> 配对。</para>
    /// </summary>
    private static readonly Lazy<Theme> s_lightTheme = new(() => Theme.FromSource(TintThemes.LightPlus));

    private readonly TextView _textView;

    private readonly TextDocument _document;

    private readonly Action<Exception>? _exceptionHandler;

    /// <summary>
    /// Tokenization of the document's leading lines: entry <c>i</c> holds line <c>i + 1</c>, so the
    /// list length is the number of tokenized lines and the last entry's end state is where the next
    /// line starts.
    /// <para>文档开头若干行的分词结果：第 <c>i</c> 项对应第 <c>i + 1</c> 行，因此列表长度即已分词行数，
    /// 最后一项的结束状态就是下一行的起始状态。</para>
    /// </summary>
    private readonly List<TokenizedLine> _lines = [];

    /// <summary>
    /// Brushes parsed from the theme's VS Code color literals, keyed by the literal itself.
    /// <para>由主题的 VS Code 颜色字面量解析出的画刷，以字面量为键。</para>
    /// </summary>
    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Typefaces built for tokens carrying a font style, keyed by the style flags and font family so a
    /// change of editor font does not keep a stale typeface.
    /// <para>为带字体样式的 token 构建的字体，以样式标志与字族为键，使编辑器字体变化时不会沿用旧字体。</para>
    /// </summary>
    private readonly Dictionary<(int Style, string Family), Typeface> _typefaceCache = [];

    /// <summary>
    /// Text decorations built for tokens carrying underline or strikethrough, keyed by the style flags.
    /// <para>为带下划线或删除线的 token 构建的文本装饰，以样式标志为键。</para>
    /// </summary>
    private readonly Dictionary<int, TextDecorationCollection> _decorationCache = [];

    private Tokenizer? _tokenizer;

    private Theme? _theme;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TintSyntaxColorizer"/> class and attaches it to the
    /// supplied text view; nothing is colorized until a grammar is installed.
    /// <para>初始化 <see cref="TintSyntaxColorizer"/> 的新实例并挂到给定的文本视图上；安装语法前不做任何
    /// 着色。</para>
    /// </summary>
    /// <param name="textView">The text view whose lines are colorized.<para>需要着色的文本视图。</para></param>
    /// <param name="document">The document being displayed.<para>正在显示的文档。</para></param>
    /// <param name="exceptionHandler">Receives tokenization failures, which leave the affected lines
    /// unhighlighted instead of breaking the editor.<para>接收分词失败的回调，失败只让受影响的行失去
    /// 高亮而不会中断编辑器。</para></param>
    public TintSyntaxColorizer(TextView textView, TextDocument document, Action<Exception>? exceptionHandler = null)
    {
        ArgumentNullException.ThrowIfNull(textView);
        ArgumentNullException.ThrowIfNull(document);

        _textView = textView;
        _document = document;
        _exceptionHandler = exceptionHandler;

        _theme = s_darkTheme.Value;

        _document.Changed += OnDocumentChanged;
        _textView.LineTransformers.Add(this);
    }

    /// <summary>
    /// Gets a value indicating whether a grammar is installed.
    /// <para>获取是否已安装语法。</para>
    /// </summary>
    public bool HasGrammar => _tokenizer is not null;

    /// <summary>
    /// Gets the TextMate scope name of the installed grammar, or <c>null</c> when none is installed.
    /// <para>获取已安装语法的 TextMate 作用域名，未安装时为 <c>null</c>。</para>
    /// </summary>
    public string? ScopeName => _tokenizer?.ScopeName;

    /// <summary>
    /// Installs the grammar for a language id, dropping any previous grammar and tokenization. Returns
    /// <c>false</c> when the id has no bundled grammar, in which case highlighting is turned off.
    /// <para>为语言标识安装语法，并丢弃此前的语法与分词结果。该标识没有内置语法时返回 <c>false</c>，
    /// 此时关闭高亮。</para>
    /// </summary>
    /// <param name="languageId">The language id from <see cref="UiAbstractions.SyntaxLanguageRegistry"/>.
    /// <para>来自 <see cref="UiAbstractions.SyntaxLanguageRegistry"/> 的语言标识。</para></param>
    /// <returns><c>true</c> when a grammar is installed.<para>安装成功时为 <c>true</c>。</para></returns>
    public bool SetLanguage(string? languageId)
    {
        Reset();
        _tokenizer = null;

        if (string.IsNullOrEmpty(languageId)
            || !s_grammarsByLanguageId.TryGetValue(languageId, out var grammar)
            || _theme is null)
        {
            _textView.Redraw();
            return false;
        }

        try
        {
            _tokenizer = s_registry.Value.CreateTokenizer(grammar.ScopeName, _theme);
            _textView.Redraw();
            return true;
        }
        catch (Exception ex) when (ex is TintGrammarException or KeyNotFoundException or InvalidOperationException)
        {
            _exceptionHandler?.Invoke(ex);
            _tokenizer = null;
            _textView.Redraw();
            return false;
        }
    }

    /// <summary>
    /// Switches the color theme and re-tokenizes, because token colors are resolved against the theme
    /// when the state stack is built. The installed grammar is kept.
    /// <para>切换配色主题并重新分词，因为 token 颜色是在构建状态栈时相对主题解析的；已安装的语法保持不变。</para>
    /// </summary>
    /// <param name="themeVariant">The theme variant to colorize with.<para>用于着色的主题变体。</para></param>
    public void SetTheme(ThemeVariant themeVariant)
    {
        var theme = themeVariant == ThemeVariant.Light ? s_lightTheme.Value : s_darkTheme.Value;
        if (ReferenceEquals(theme, _theme))
        {
            return;
        }

        var scopeName = _tokenizer?.ScopeName;

        _theme = theme;
        _brushCache.Clear();
        Reset();
        _tokenizer = null;

        if (!string.IsNullOrEmpty(scopeName))
        {
            try
            {
                _tokenizer = s_registry.Value.CreateTokenizer(scopeName, theme);
            }
            catch (Exception ex) when (ex is TintGrammarException or KeyNotFoundException or InvalidOperationException)
            {
                _exceptionHandler?.Invoke(ex);
            }
        }

        _textView.Redraw();
    }

    /// <summary>
    /// Removes the installed grammar and every cached tokenization.
    /// <para>移除已安装的语法与全部缓存的分词结果。</para>
    /// </summary>
    public void Clear()
    {
        _tokenizer = null;
        Reset();
        _textView.Redraw();
    }

    /// <inheritdoc />
    protected override void ColorizeLine(DocumentLine line)
    {
        if (_tokenizer is null || _theme is null || line.IsDeleted || line.LineNumber > _document.LineCount)
        {
            return;
        }

        if (!TryGetLineTokens(line.LineNumber, out var tokens) || tokens.Length == 0)
        {
            return;
        }

        var context = CurrentContext;
        if (context is null)
        {
            return;
        }

        var baseTypeface = context.GlobalTextRunProperties.Typeface;
        var defaults = _theme.Defaults;
        var lineEnd = line.EndOffset;

        foreach (var token in tokens)
        {
            if (token.Length <= 0)
            {
                continue;
            }

            var start = line.Offset + token.Start;
            if (start >= lineEnd)
            {
                break;
            }

            var end = Math.Min(start + token.Length, lineEnd);

            var foreground = token.Foreground > 0 && token.Foreground != defaults.ForegroundId
                ? ResolveBrush(token.Foreground)
                : null;
            var background = token.Background > 0 && token.Background != defaults.BackgroundId
                ? ResolveBrush(token.Background)
                : null;
            var style = token.FontStyle;

            ChangeLinePart(start, end, element =>
            {
                if (foreground is not null)
                {
                    element.TextRunProperties.SetForegroundBrush(foreground);
                }

                if (background is not null)
                {
                    element.BackgroundBrush = background;
                }

                if (style is TintFontStyle.None or TintFontStyle.NotSet)
                {
                    return;
                }

                element.TextRunProperties.SetTypeface(ResolveTypeface(style, baseTypeface));

                if (ResolveDecorations(style) is { } decorations)
                {
                    element.TextRunProperties.SetTextDecorations(decorations);
                }
            });
        }
    }

    /// <summary>
    /// Gets the tokens of one line, extending the cached prefix up to it first.
    /// <para>取一行的 token，先把缓存前缀延伸到该行。</para>
    /// </summary>
    /// <param name="lineNumber">The 1-based line number.<para>从 1 开始的行号。</para></param>
    /// <param name="tokens">The line's tokens.<para>该行的 token。</para></param>
    /// <returns><c>true</c> when the line has a tokenization.<para>该行有分词结果时为 <c>true</c>。</para></returns>
    private bool TryGetLineTokens(int lineNumber, out Token[] tokens)
    {
        if (lineNumber <= _lines.Count)
        {
            tokens = _lines[lineNumber - 1].Tokens;
            return true;
        }

        var tokenizer = _tokenizer;
        var lineCount = _document.LineCount;
        if (tokenizer is null || lineNumber > lineCount)
        {
            tokens = [];
            return false;
        }

        try
        {
            for (var current = _lines.Count + 1; current <= lineNumber; current++)
            {
                var state = _lines.Count == 0 ? StateStack.Initial : _lines[^1].EndState;
                var text = _document.GetText(_document.GetLineByNumber(current));
                var span = tokenizer.TokenizeLine(text, state, out var endState);

                _lines.Add(new TokenizedLine(span.ToArray(), endState));
            }

            tokens = _lines[lineNumber - 1].Tokens;
            return true;
        }
        catch (Exception ex) when (ex is TintGrammarException or ArgumentOutOfRangeException or InvalidOperationException or KeyNotFoundException)
        {
            _exceptionHandler?.Invoke(ex);
            tokens = [];
            return false;
        }
    }

    /// <summary>
    /// Builds the shared grammar registry: every selectable grammar plus the grammars they embed.
    /// <para>建立共享的语法注册表：全部可选语法及其内嵌的语法。</para>
    /// </summary>
    private static GrammarRegistry CreateRegistry()
    {
        var registry = new GrammarRegistry();

        registry.Add(
            TintGrammars.Html,
            TintGrammars.HtmlBasic,
            TintGrammars.Css,
            TintGrammars.JavaScript,
            TintGrammars.JsDocJavaScriptInjection,
            TintGrammars.Yaml12,
            TintGrammars.YamlEmbedded);

        foreach (var grammar in s_grammarsByLanguageId.Values.Distinct())
        {
            registry.Add(grammar);
        }

        return registry;
    }

    /// <summary>
    /// Resolves a Tint color id to a cached brush for the current theme.
    /// <para>把 Tint 颜色标识解析为当前主题下的缓存画刷。</para>
    /// </summary>
    private IBrush ResolveBrush(int colorId)
    {
        var theme = _theme;
        if (theme is null || colorId <= 0)
        {
            return Brushes.Transparent;
        }

        var literal = theme.ColorMap.GetColor(colorId);
        if (string.IsNullOrEmpty(literal))
        {
            return Brushes.Transparent;
        }

        if (_brushCache.TryGetValue(literal, out var cached))
        {
            return cached;
        }

        var brush = ParseVsCodeColor(literal) is { } color
            ? new ImmutableSolidColorBrush(color)
            : Brushes.Transparent;

        _brushCache[literal] = brush;
        return brush;
    }

    /// <summary>
    /// Parses a VS Code color literal: <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.
    /// VS Code writes the alpha channel <em>last</em>, unlike Avalonia's <c>#AARRGGBB</c>, so the literal
    /// cannot be handed to <see cref="Color.Parse"/> unchanged.
    /// <para>解析 VS Code 颜色字面量：<c>#RGB</c>、<c>#RGBA</c>、<c>#RRGGBB</c> 或 <c>#RRGGBBAA</c>。
    /// VS Code 把 alpha 写在<em>最后</em>，与 Avalonia 的 <c>#AARRGGBB</c> 不同，因此不能原样交给
    /// <see cref="Color.Parse"/>。</para>
    /// </summary>
    /// <param name="literal">The color literal from the theme.<para>来自主题的颜色字面量。</para></param>
    /// <returns>The parsed color, or <c>null</c> when the literal is not a supported form.
    /// <para>解析出的颜色；字面量不受支持时为 <c>null</c>。</para></returns>
    private static Color? ParseVsCodeColor(string literal)
    {
        var span = literal.AsSpan().Trim();
        if (span.Length < 4 || span[0] != '#')
        {
            return null;
        }

        span = span[1..];

        if (span.Length is 6 or 8
            && uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return span.Length == 6
                ? Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
                : Color.FromArgb((byte)packed, (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8));
        }

        if (span.Length is not (3 or 4))
        {
            return null;
        }

        Span<int> channels = stackalloc int[4];
        for (var i = 0; i < span.Length; i++)
        {
            var digit = span[i] switch
            {
                >= '0' and <= '9' => span[i] - '0',
                >= 'a' and <= 'f' => span[i] - 'a' + 10,
                >= 'A' and <= 'F' => span[i] - 'A' + 10,
                _ => -1,
            };

            if (digit < 0)
            {
                return null;
            }

            channels[i] = (digit << 4) | digit;
        }

        return span.Length == 3
            ? Color.FromRgb((byte)channels[0], (byte)channels[1], (byte)channels[2])
            : Color.FromArgb((byte)channels[3], (byte)channels[0], (byte)channels[1], (byte)channels[2]);
    }

    /// <summary>
    /// Builds or reuses the typeface for a token's font style, keeping the editor's font family, stretch
    /// and variations so styled tokens do not change the configured editor font.
    /// <para>为 token 的字体样式构建或复用字体，保留编辑器的字族、拉伸与变体设置，使有样式的 token 不会
    /// 改变已配置的编辑器字体。</para>
    /// </summary>
    private Typeface ResolveTypeface(TintFontStyle style, Typeface baseTypeface)
    {
        var key = ((int)style, baseTypeface.FontFamily.Name);
        if (_typefaceCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var typeface = new Typeface(
            baseTypeface.FontFamily,
            style.HasFlag(TintFontStyle.Italic) ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal,
            style.HasFlag(TintFontStyle.Bold) ? FontWeight.Bold : FontWeight.Normal,
            baseTypeface.Stretch,
            baseTypeface.FontVariations);

        _typefaceCache[key] = typeface;
        return typeface;
    }

    /// <summary>
    /// Builds or reuses the text decorations a token's font style requires, or <c>null</c> when it
    /// requests none.
    /// <para>为 token 字体样式构建或复用所需的文本装饰，未要求时为 <c>null</c>。</para>
    /// </summary>
    private TextDecorationCollection? ResolveDecorations(TintFontStyle style)
    {
        var underline = style.HasFlag(TintFontStyle.Underline);
        var strikethrough = style.HasFlag(TintFontStyle.Strikethrough);

        if (!underline && !strikethrough)
        {
            return null;
        }

        var key = (int)style;
        if (_decorationCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var decorations = (underline, strikethrough) switch
        {
            (true, false) => TextDecorations.Underline,
            (false, true) => TextDecorations.Strikethrough,
            _ => new TextDecorationCollection([.. TextDecorations.Underline, .. TextDecorations.Strikethrough]),
        };

        _decorationCache[key] = decorations;
        return decorations;
    }

    /// <summary>
    /// Discards the cached tokenization, forcing a re-tokenization on the next render.
    /// <para>丢弃缓存的分词结果，使下一次渲染重新分词。</para>
    /// </summary>
    private void Reset()
    {
        _lines.Clear();
    }

    /// <summary>
    /// Drops the cached tokenization from the edited line onward, because an edit changes the grammar
    /// state that line produces and therefore the state every following line starts from.
    /// <para>丢弃被编辑行及其后的缓存分词结果，因为编辑改变了该行产生的语法状态，也改变了后续每行的起始
    /// 状态。</para>
    /// </summary>
    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_lines.Count == 0 || _document.LineCount == 0)
        {
            return;
        }

        var offset = Math.Clamp(e.Offset, 0, _document.TextLength);
        var editedLine = _document.GetLineByOffset(offset).LineNumber;

        var keep = Math.Min(editedLine - 1, _lines.Count);
        if (keep < _lines.Count)
        {
            _lines.RemoveRange(keep, _lines.Count - keep);
        }
    }

    /// <summary>
    /// Detaches the transformer from the text view and stops listening to the document.
    /// <para>从文本视图卸下该变换器并停止监听文档。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _document.Changed -= OnDocumentChanged;
        _textView.LineTransformers.Remove(this);
        Reset();
    }

    /// <summary>
    /// One tokenized line: the copied token ranges and the grammar state the line ended with.
    /// <para>一行分词结果：复制出的 token 区间与该行结束时的语法状态。</para>
    /// </summary>
    /// <param name="Tokens">The line's tokens, copied out of the tokenizer's reused buffer.
    /// <para>该行的 token，已从分词器复用的缓冲区复制出来。</para></param>
    /// <param name="EndState">The state the following line starts from.
    /// <para>下一行的起始状态。</para></param>
    private readonly record struct TokenizedLine(Token[] Tokens, StateStack EndState);
}