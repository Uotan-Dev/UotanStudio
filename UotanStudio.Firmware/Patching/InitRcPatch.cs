using System.Text;

namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Android init section selectors. Imports are standalone sections.
/// <para>Android init 段类型选择器。import 属于独立段。</para>
/// </summary>
public enum InitRcSection
{
    /// <summary>A <c>service</c> section with its body statements. <para>带正文语句的 <c>service</c> 段。</para></summary>
    Service,
    /// <summary>An <c>on</c> action section whose trigger expression may span several words. <para>触发表达式可跨多个词的 <c>on</c> 动作段。</para></summary>
    Action,
    /// <summary>A standalone <c>import</c> line that owns no body statements. <para>不拥有正文语句的独立 <c>import</c> 行。</para></summary>
    Import
}

/// <summary>
/// Lexical statement edits within explicitly selected init sections.
/// <para>在显式选中的 init 段内进行的词法级语句编辑。</para>
/// </summary>
public enum InitRcOperation
{
    /// <summary>Swaps the matched statement span for the replacement statement. <para>把命中的语句片段替换为给定的替换语句。</para></summary>
    Replace,
    /// <summary>Removes the matched statement together with its whole source line. <para>连同整行源代码一起删除命中的语句。</para></summary>
    Delete
}

/// <summary>
/// Placement relative to a uniquely matched complete body statement.
/// <para>相对于唯一命中的完整正文语句的插入位置。</para>
/// </summary>
public enum InitRcInsertion
{
    /// <summary>Appends after the last statement, or after the header when the body is empty. <para>追加在最后一条语句之后；正文为空时追加在段头之后。</para></summary>
    End,
    /// <summary>Inserts directly before the anchor statement. <para>紧插在锚定语句之前。</para></summary>
    BeforeAnchor,
    /// <summary>Inserts directly after the anchor statement. <para>紧插在锚定语句之后。</para></summary>
    AfterAnchor
}

/// <summary>
/// Bounded Android init lexer and source-preserving statement editor.
/// <para>有界的 Android init 词法分析器，并在编辑时保留原始源代码文本。</para>
/// </summary>
public static partial class InitRcPatch
{
    private sealed record Statement(int Start, int End, int LineStart, int LineEnd, string[] Words, (int Start, int End)[] Ranges);
    private sealed record Section(Statement Header, List<Statement> Body);

    /// <summary>
    /// Replaces an existing decoded argument, retaining every unselected source span. Index zero follows the directive.
    /// <para>替换已存在的、解码后的参数，并保留所有未被选中的源码片段。索引 <c>0</c> 表示指令词之后的第一个参数。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// InitRcPatch.EditArgument(text, InitRcSection.Service, "surfaceflinger", "class",
    ///     0, "base hal", false, "", 1, 1, token);
    /// </code>
    /// </example>
    /// <param name="text">Init source bounded by <see cref="TextPatch.MaxCharacters"/> and free of NUL characters.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 且不含 NUL 字符的 init 源代码。</para></param>
    /// <param name="kind">Section kind the selector identifies. <para>选择器所对应的段类型。</para></param>
    /// <param name="selector">One service name, one import path, or one action trigger expression, lexed exactly like a statement.
    /// <para>一个服务名、一个 import 路径或一个动作触发表达式，词法分析与语句完全一致。</para></param>
    /// <param name="directive">Body statement name whose argument is edited; empty selects the section header itself and must be a
    /// single token of at most 16384 characters.
    /// <para>要编辑其参数的正文语句名；为空时选中段头本身。必须是不含空白的单个词，长度不超过 16384 个字符。</para></param>
    /// <param name="argumentIndex">Argument position after the directive or header keyword, from 0 to 1022.
    /// <para>指令词或段头关键字之后的参数序号，取值 <c>0</c> 到 <c>1022</c>。</para></param>
    /// <param name="value">New decoded argument, at most 16384 characters without NUL; it is re-encoded with init escapes, and a
    /// quoted form is emitted for an empty value.
    /// <para>新的解码参数值，不超过 16384 个字符且不含 NUL；会按 init 转义规则重新编码，空值会输出为带引号的形式。</para></param>
    /// <param name="checkOldValue">Compares the currently decoded argument with <paramref name="expectedOldValue"/> before editing.
    /// <para>编辑前将当前解码得到的参数与 <paramref name="expectedOldValue"/> 比较。</para></param>
    /// <param name="expectedOldValue">Decoded old value the statement must already hold when <paramref name="checkOldValue"/> is set.
    /// <para>启用 <paramref name="checkOldValue"/> 时语句当前必须具有的解码值。</para></param>
    /// <param name="expectedSections">Exact number of sections the selector must resolve to; a header edit requires one.
    /// <para>选择器必须精确解析出的段数量；编辑段头时要求为一个。</para></param>
    /// <param name="expectedMatches">Exact number of statements the selection must resolve to.
    /// <para>选择结果必须精确命中的语句数量。</para></param>
    /// <param name="token">Checked while selecting, rebuilding output and re-parsing. <para>在选中、重建输出和重新解析时都会检查。</para></param>
    /// <returns>
    /// The rebuilt source, the number of selected statements and whether the text differs from <paramref name="text"/>. An
    /// argument that already decodes to <paramref name="value"/> keeps its original source span.
    /// <para>重建后的源代码、选中的语句数量，以及文本是否与 <paramref name="text"/> 不同。解码值已等于
    /// <paramref name="value"/> 的参数会保留其原始源码片段。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="selector"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 或 <paramref name="selector"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">
    /// The index or value is out of range, the section kind is undefined, the selector or directive does not lex to exactly one
    /// statement, the expected counts do not match, the requested argument is absent, the old-value guard fails, a header edit
    /// selects more than one section, the output exceeds the configuration limit, or the rebuilt text fails to parse.
    /// <para>索引或取值越界、段类型未定义、选择器或指令未能分析出恰好一条语句、期望数量不匹配、请求的参数不存在、
    /// 旧值守卫校验失败、段头编辑选中了多个段、输出超出配置上限，或重建后的文本解析失败。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
    public static TextPatchResult EditArgument(string text, InitRcSection kind, string selector, string directive,
        int argumentIndex, string value, bool checkOldValue, string expectedOldValue, int expectedSections,
        int expectedMatches, CancellationToken token)
    {
        if (argumentIndex is < 0 or > 1022 || value.Length > 16384 || value.Contains('\0'))
        {
            throw new InvalidDataException("Invalid init argument index or value.");
        }
        var matches = Select(text, kind, selector, directive, expectedSections, expectedMatches, token);
        if (directive.Length == 0 && matches.Count != 1)
        {
            throw new InvalidDataException("Header argument editing requires exactly one selected section.");
        }
        var wordIndex = argumentIndex + 1;
        var encoded = value.Length == 0 ? "\"\"" : string.Concat(value.Select(character => character switch
        {
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            ' ' => "\\ ",
            '\\' => "\\\\",
            '"' => "\\\"",
            '#' => "\\#",
            _ => character.ToString()
        }));
        var output = new StringBuilder();
        var position = 0;
        foreach (var line in matches)
        {
            token.ThrowIfCancellationRequested();
            if (wordIndex >= line.Words.Length)
            {
                throw new InvalidDataException("Selected init statement does not contain the requested argument.");
            }
            if (checkOldValue && line.Words[wordIndex] != expectedOldValue)
            {
                throw new InvalidDataException("Init argument does not match the expected decoded old value.");
            }
            var range = line.Ranges[wordIndex];
            var replacement = line.Words[wordIndex] == value ? text[range.Start..range.End] : encoded;
            if (directive.Length == 0)
            {
                return ReplaceHeader(text, kind, selector, text[line.Start..range.Start] + replacement + text[range.End..line.End],
                    false, "", token);
            }
            if ((long)output.Length + range.Start - position + replacement.Length > TextPatch.MaxCharacters)
            {
                throw new InvalidDataException("Init output exceeds the configuration limit.");
            }
            output.Append(text, position, range.Start - position).Append(replacement);
            position = range.End;
        }
        if ((long)output.Length + text.Length - position > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Init output exceeds the configuration limit.");
        }
        output.Append(text, position, text.Length - position);
        var result = output.ToString();
        Parse(result, token);
        return new TextPatchResult(result, matches.Count, result != text);
    }

    /// <summary>
    /// Replaces one section header without changing its kind or rewriting references to its identity.
    /// <para>替换单个段头，不会改变段的类型，也不会改写指向该身份的其他引用。</para>
    /// </summary>
    /// <param name="text">Init source bounded by <see cref="TextPatch.MaxCharacters"/> and free of NUL characters.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 且不含 NUL 字符的 init 源代码。</para></param>
    /// <param name="kind">Section kind the selector identifies; <see cref="InitRcSection.Import"/> headers are also addressable.
    /// <para>选择器所对应的段类型；<see cref="InitRcSection.Import"/> 的段头同样可以被选中。</para></param>
    /// <param name="selector">One service name, import path or action trigger expression identifying exactly one section.
    /// <para>用于精确定位一个段的服务名、import 路径或动作触发表达式。</para></param>
    /// <param name="replacement">The complete new header line: one header of the selected kind, no body, no comments, no trailing
    /// newline required.
    /// <para>完整的新段头行：必须是所选类型的单个段头，不含正文、注释，也不要求结尾换行。</para></param>
    /// <param name="checkOldValue">Compares the current header source span with <paramref name="expectedOldValue"/>.
    /// <para>将当前段头的源码片段与 <paramref name="expectedOldValue"/> 比较。</para></param>
    /// <param name="expectedOldValue">Exact source text of the header when <paramref name="checkOldValue"/> is set.
    /// <para>启用 <paramref name="checkOldValue"/> 时段头的精确源代码文本。</para></param>
    /// <param name="token">Checked while selecting, parsing the replacement and re-parsing the result.
    /// <para>在选中、解析替换文本以及重新解析结果时都会检查。</para></param>
    /// <returns>The patched source, the constant match count <c>1</c>, and whether the text differs from <paramref name="text"/>.
    /// <para>补丁后的源代码、固定值 <c>1</c> 的匹配数量，以及文本是否与 <paramref name="text"/> 不同。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, <paramref name="selector"/> or
    /// <paramref name="replacement"/> is <see langword="null"/>.
    /// <para><paramref name="text"/>、<paramref name="selector"/> 或 <paramref name="replacement"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The selector does not resolve to exactly one section, the replacement is not a single
    /// header of the selected kind, its keyword differs, the old-value guard fails, the new identity conflicts with another
    /// section, the output exceeds the configuration limit, or the patched text fails to parse.
    /// <para>选择器未能精确定位一个段、替换文本不是所选类型的单个段头、关键字不一致、旧值守卫校验失败、
    /// 新身份与另一个段冲突、输出超出配置上限，或补丁后的文本解析失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
    public static TextPatchResult ReplaceHeader(string text, InitRcSection kind, string selector, string replacement,
        bool checkOldValue, string expectedOldValue, CancellationToken token)
    {
        var selected = Select(text, kind, selector, "", 1, 1, token)[0];
        var supplied = Parse(replacement, token);
        if (supplied.Count != 1 || supplied[0].Body.Count != 0 || supplied[0].Header.Start != 0
            || supplied[0].Header.End != replacement.Length || supplied[0].Header.Words[0] != selected.Words[0])
        {
            throw new InvalidDataException("Supply exactly one header of the selected section kind, without body or comments.");
        }
        if (checkOldValue && text[selected.Start..selected.End] != expectedOldValue)
        {
            throw new InvalidDataException("Init header does not match the expected old source text.");
        }
        var desired = supplied[0].Header.Words;
        if (Parse(text, token).Any(section => section.Header.Start != selected.Start
            && section.Header.Words[0] == desired[0]
            && (kind == InitRcSection.Action ? section.Header.Words.Skip(1).SequenceEqual(desired.Skip(1))
                : section.Header.Words[1] == desired[1])))
        {
            throw new InvalidDataException("The new init header identity conflicts with another section.");
        }
        if ((long)text.Length - (selected.End - selected.Start) + replacement.Length > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Init output exceeds the configuration limit.");
        }
        var result = text[..selected.Start] + replacement + text[selected.End..];
        Parse(result, token);
        return new TextPatchResult(result, 1, result != text);
    }

    /// <summary>
    /// Appends one complete section, preserving an equivalent existing section and rejecting identity conflicts.
    /// <para>追加一个完整的段；已存在的等价段会被保留，身份冲突则报错。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// InitRcPatch.AddSection(text, "service mydaemon /system/bin/mydaemon\n    class late_start\n", token);
    /// </code>
    /// </example>
    /// <param name="text">Init source bounded by <see cref="TextPatch.MaxCharacters"/> and free of NUL characters. A CRLF document
    /// keeps CRLF; the appended block is separated from the existing text when the last line has no terminator.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 且不含 NUL 的 init 源代码。CRLF 文档继续使用 CRLF；
    /// 末行缺少换行符时，追加内容前会补上分隔换行。</para></param>
    /// <param name="sectionText">The whole section to append: exactly one service, action or import section with its body statements.
    /// <para>要追加的完整段：必须是恰好一个 service、action 或 import 段及其正文语句。</para></param>
    /// <param name="token">Checked while parsing both texts and the merged result. <para>在解析两段文本及合并结果时都会检查。</para></param>
    /// <returns>
    /// The input text with match count <c>1</c> and <c>false</c> when an identical section already exists; otherwise the appended
    /// text with match count <c>0</c> and <c>true</c>.
    /// <para>已存在完全相同的段时返回原文本、匹配数量 <c>1</c> 与 <c>false</c>；否则返回追加后的文本、
    /// 匹配数量 <c>0</c> 与 <c>true</c>。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="sectionText"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 或 <paramref name="sectionText"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException"><paramref name="sectionText"/> is not exactly one section, several existing sections
    /// share its identity, an existing section of that identity differs in header arguments or ordered body statements, the
    /// output exceeds the configuration limit, or the merged text fails to parse.
    /// <para><paramref name="sectionText"/> 不是恰好一个段、多个已存在的段共享同一身份、同身份段的段头参数或正文语句顺序不一致、
    /// 输出超出配置上限，或合并后的文本解析失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
    public static TextPatchResult AddSection(string text, string sectionText, CancellationToken token)
    {
        var supplied = Parse(sectionText, token);
        if (supplied.Count != 1)
        {
            throw new InvalidDataException("Supply exactly one service, action or import section.");
        }
        var desired = supplied[0];
        var header = desired.Header.Words;
        var existing = Parse(text, token).Where(section => section.Header.Words[0] == header[0]
            && (header[0] == "on" ? section.Header.Words.Skip(1).SequenceEqual(header.Skip(1))
                : section.Header.Words[1] == header[1])).ToArray();
        if (existing.Length > 1)
        {
            throw new InvalidDataException("Multiple init sections share the requested identity.");
        }
        if (existing.Length == 1)
        {
            var current = existing[0];
            if (!current.Header.Words.SequenceEqual(header) || current.Body.Count != desired.Body.Count
                || current.Body.Where((line, index) => !line.Words.SequenceEqual(desired.Body[index].Words)).Any())
            {
                throw new InvalidDataException("An existing init section has different header arguments or ordered body statements.");
            }
            return new TextPatchResult(text, 1, false);
        }
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = text.Length > 0 && !text.EndsWith('\n') ? newline : "";
        var ending = sectionText.EndsWith('\n') ? "" : newline;
        if ((long)text.Length + separator.Length + sectionText.Length + ending.Length > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Init output exceeds the configuration limit.");
        }
        var result = text + separator + sectionText + ending;
        Parse(result, token);
        return new TextPatchResult(result, 0, true);
    }

    /// <summary>
    /// Appends a statement if its explicit directive/argument identity is absent; conflicting identities fail.
    /// <para>当语句显式的指令与参数身份尚不存在时追加它；身份冲突则报错。</para>
    /// </summary>
    /// <param name="text">Init source bounded by <see cref="TextPatch.MaxCharacters"/> and free of NUL characters.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 且不含 NUL 字符的 init 源代码。</para></param>
    /// <param name="kind">Section kind the selector identifies; <see cref="InitRcSection.Import"/> owns no body statements and is
    /// rejected here. <para>选择器所对应的段类型；<see cref="InitRcSection.Import"/> 没有正文语句，在本方法中会被拒绝。</para></param>
    /// <param name="selector">One service name or action trigger expression. <para>一个服务名或动作触发表达式。</para></param>
    /// <param name="statement">The single body statement to insert, without a leading header keyword or trailing comments; it is
    /// emitted indented by four spaces. <para>要插入的单条正文语句，不得以段头关键字开头或带尾部注释；写入时统一缩进四个空格。</para></param>
    /// <param name="identityArguments">Number of arguments, counted after the directive, that define the statement identity; must be
    /// available in <paramref name="statement"/>. <para>用于确定语句身份的参数个数（从指令词之后计数），必须在 <paramref name="statement"/> 中存在。</para></param>
    /// <param name="expectedSections">Exact number of sections the selector must resolve to, at least one.
    /// <para>选择器必须精确解析出的段数量，至少为一个。</para></param>
    /// <param name="token">Checked while lexing, selecting and rebuilding output. <para>在词法分析、选中和重建输出时都会检查。</para></param>
    /// <param name="placement">Where the statement is inserted; a defined <see cref="InitRcInsertion"/> value is required.
    /// <para>插入位置；必须是已定义的 <see cref="InitRcInsertion"/> 值。</para></param>
    /// <param name="anchor">Ignored for <see cref="InitRcInsertion.End"/>; otherwise one complete body statement that must
    /// match exactly one statement in every selected section.
    /// <para><paramref name="placement"/> 为 <see cref="InitRcInsertion.End"/> 时忽略；否则必须是完整的一条正文语句，
    /// 且在每个选中段中恰好匹配一条语句。</para></param>
    /// <returns>The patched source, the number of selected sections that already held an equivalent statement, and whether the text
    /// differs from <paramref name="text"/>.
    /// <para>补丁后的源代码、已存在等价语句的选中段数量，以及文本是否与 <paramref name="text"/> 不同。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, <paramref name="selector"/>,
    /// <paramref name="statement"/> or <paramref name="anchor"/> is <see langword="null"/>.
    /// <para><paramref name="text"/>、<paramref name="selector"/>、<paramref name="statement"/> 或
    /// <paramref name="anchor"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The kind or counts are invalid, the selector matches the wrong number of sections,
    /// <paramref name="statement"/> is not one body statement, the insertion placement is undefined, the anchor is missing or
    /// ambiguous, an existing statement with the same identity carries different arguments, an equivalent statement is not
    /// adjacent to the requested side of its anchor, the output exceeds the configuration limit, or the patched text fails to parse.
    /// <para>段类型或数量非法、选择器命中的段数量不符、<paramref name="statement"/> 不是单条正文语句、插入位置未定义、
    /// 锚定语句缺失或存在歧义、同身份语句的参数不同、等价语句未紧邻锚定语句的指定一侧、输出超出配置上限，
    /// 或补丁后的文本解析失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
    public static TextPatchResult Add(string text, InitRcSection kind, string selector, string statement,
        int identityArguments, int expectedSections, CancellationToken token,
        InitRcInsertion placement = InitRcInsertion.End, string anchor = "")
    {
        if (kind == InitRcSection.Import || expectedSections < 1)
        {
            throw new InvalidDataException("Adding a body statement requires at least one service or action section.");
        }
        var supplied = Lex(statement, token);
        if (supplied.Count != 1 || supplied[0].Start != 0 || supplied[0].End != statement.Length
            || IsHeader(supplied[0].Words[0]) || identityArguments < 0 || identityArguments >= supplied[0].Words.Length)
        {
            throw new InvalidDataException("Supply one complete body statement and a valid number of identity arguments.");
        }
        var desired = supplied[0].Words;
        if (!Enum.IsDefined(placement)) { throw new PatchingUnsupportedFeatureException("Unsupported init insertion placement."); }
        string[] anchorWords = [];
        if (placement != InitRcInsertion.End)
        {
            var anchors = Lex(anchor, token);
            if (anchors.Count != 1 || IsHeader(anchors[0].Words[0])
                || anchors[0].Start != 0 || anchors[0].End != anchor.Length)
            {
                throw new InvalidDataException("Anchor must be one complete body statement without trailing comments.");
            }
            anchorWords = anchors[0].Words;
        }
        var headers = Select(text, kind, selector, "", expectedSections, expectedSections, token)
            .Select(header => header.Start).ToHashSet();
        var sections = Parse(text, token).Where(section => headers.Contains(section.Header.Start));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var output = new StringBuilder();
        var position = 0;
        var existing = 0;
        foreach (var section in sections)
        {
            token.ThrowIfCancellationRequested();
            Statement? anchorLine = null;
            if (placement != InitRcInsertion.End)
            {
                var anchors = section.Body.Where(line => line.Words.SequenceEqual(anchorWords)).ToArray();
                if (anchors.Length != 1)
                {
                    throw new InvalidDataException("Each selected section must contain exactly one matching anchor statement.");
                }
                anchorLine = anchors[0];
            }
            var matches = section.Body.Where(line => line.Words.Length > identityArguments
                && line.Words.Take(identityArguments + 1).SequenceEqual(desired.Take(identityArguments + 1))).ToArray();
            if (matches.Length > 1 || matches.Length == 1 && !matches[0].Words.SequenceEqual(desired))
            {
                throw new InvalidDataException("Existing init statement identity is duplicated or has different arguments.");
            }
            if (matches.Length == 1)
            {
                if (anchorLine is not null)
                {
                    var expectedIndex = section.Body.IndexOf(anchorLine) + (placement == InitRcInsertion.BeforeAnchor ? -1 : 1);
                    if (section.Body.IndexOf(matches[0]) != expectedIndex)
                    {
                        throw new InvalidDataException("The existing init statement is not adjacent to the requested side of its anchor.");
                    }
                }
                existing++;
                continue;
            }
            var last = section.Body.LastOrDefault() ?? section.Header;
            var insertion = anchorLine is null ? last.LineEnd
                : placement == InitRcInsertion.BeforeAnchor ? anchorLine.LineStart : anchorLine.LineEnd;
            var separator = insertion > 0 && text[insertion - 1] != '\n' ? newline : "";
            var addition = separator + "    " + statement + newline;
            if ((long)output.Length + insertion - position + addition.Length > TextPatch.MaxCharacters)
            {
                throw new InvalidDataException("Init output exceeds the configuration limit.");
            }
            output.Append(text, position, insertion - position).Append(addition);
            position = insertion;
        }
        if ((long)output.Length + text.Length - position > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Init output exceeds the configuration limit.");
        }
        output.Append(text, position, text.Length - position);
        var result = output.ToString();
        Parse(result, token);
        return new TextPatchResult(result, existing, result != text);
    }

    /// <summary>
    /// Reads raw headers (empty directive) or matching body statements, without expanding imports or properties.
    /// <para>读取原始段头（指令名为空时）或匹配的正文语句；不会展开 import 或属性引用。</para>
    /// </summary>
    /// <param name="text">Init source bounded by <see cref="TextPatch.MaxCharacters"/> and free of NUL characters.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 且不含 NUL 字符的 init 源代码。</para></param>
    /// <param name="kind">Section kind the selector identifies. <para>选择器所对应的段类型。</para></param>
    /// <param name="selector">One service name, import path or action trigger expression.
    /// <para>一个服务名、import 路径或动作触发表达式。</para></param>
    /// <param name="directive">Body statement name to read; empty reads the selected section headers instead.
    /// <para>要读取的正文语句名；为空时读取选中的段头。</para></param>
    /// <param name="expectedSections">Exact number of sections the selector must resolve to.
    /// <para>选择器必须精确解析出的段数量。</para></param>
    /// <param name="expectedMatches">Exact number of statements the selection must resolve to.
    /// <para>选择结果必须精确命中的语句数量。</para></param>
    /// <param name="token">Checked while lexing and parsing. <para>在词法分析与解析过程中都会检查。</para></param>
    /// <returns>One entry per matched statement, each being the exact source span including its original quoting and escapes;
    /// the list is empty only when both expected counts are zero.
    /// <para>每条命中的语句对应一个条目，内容为精确的源码片段，保留原有的引号与转义；仅当两个期望数量均为零时列表为空。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="selector"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 或 <paramref name="selector"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The section kind is undefined, the counts are outside 0 to 100000, the selector or
    /// directive is malformed, or the selected section or statement count differs from the expected count.
    /// <para>段类型未定义、数量超出 <c>0</c> 到 <c>100000</c>、选择器或指令名格式非法，或选中的段数或语句数与期望值不符。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
    public static IReadOnlyList<string> Read(string text, InitRcSection kind, string selector, string directive,
        int expectedSections, int expectedMatches, CancellationToken token)
        => [.. Select(text, kind, selector, directive, expectedSections, expectedMatches, token).Select(line => text[line.Start..line.End])];

    /// <summary>
    /// Replaces or deletes matching body statements; headers are read-only in this API.
    /// <para>替换或删除匹配的正文语句；在本 API 中段头只能读取。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// InitRcPatch.Apply(text, InitRcSection.Service, "mydaemon", "writepid",
    ///     InitRcOperation.Delete, "", false, "", 1, 1, token);
    /// </code>
    /// </example>
    /// <param name="text">Init source bounded by <see cref="TextPatch.MaxCharacters"/> and free of NUL characters.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 且不含 NUL 字符的 init 源代码。</para></param>
    /// <param name="kind">Section kind the selector identifies; <see cref="InitRcSection.Import"/> is rejected.
    /// <para>选择器所对应的段类型；<see cref="InitRcSection.Import"/> 会被拒绝。</para></param>
    /// <param name="selector">One service name or action trigger expression. <para>一个服务名或动作触发表达式。</para></param>
    /// <param name="directive">Non-empty body statement name to edit. <para>要编辑的非空正文语句名。</para></param>
    /// <param name="operation">A defined <see cref="InitRcOperation"/> value selecting replacement or deletion.
    /// <para>已定义的 <see cref="InitRcOperation"/> 值，决定执行替换还是删除。</para></param>
    /// <param name="replacement">For <see cref="InitRcOperation.Replace"/> exactly one statement whose first word equals
    /// <paramref name="directive"/>; ignored for <see cref="InitRcOperation.Delete"/>.
    /// <para>执行 <see cref="InitRcOperation.Replace"/> 时必须是恰好一条以 <paramref name="directive"/> 开头的语句；
    /// 执行 <see cref="InitRcOperation.Delete"/> 时被忽略。</para></param>
    /// <param name="checkOldValue">Compares each matched statement's source span with <paramref name="expectedOldValue"/>.
    /// <para>将每条命中语句的源码片段与 <paramref name="expectedOldValue"/> 比较。</para></param>
    /// <param name="expectedOldValue">Exact source text every matched statement must currently have when
    /// <paramref name="checkOldValue"/> is set. <para>启用 <paramref name="checkOldValue"/> 时每条命中语句当前必须具有的精确源码文本。</para></param>
    /// <param name="expectedSections">Exact number of sections the selector must resolve to.
    /// <para>选择器必须精确解析出的段数量。</para></param>
    /// <param name="expectedMatches">Exact number of statements the selection must resolve to.
    /// <para>选择结果必须精确命中的语句数量。</para></param>
    /// <param name="token">Checked while selecting, rebuilding output and re-parsing.
    /// <para>在选中、重建输出和重新解析时都会检查。</para></param>
    /// <returns>The patched source, the number of matched statements and whether the text differs from <paramref name="text"/>.
    /// Deletion removes the whole physical line, while replacement swaps only the statement span.
    /// <para>补丁后的源代码、命中的语句数量，以及文本是否与 <paramref name="text"/> 不同。删除会移除整个物理行，
    /// 替换只替换语句本身的片段。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, <paramref name="selector"/> or
    /// <paramref name="replacement"/> is <see langword="null"/>.
    /// <para><paramref name="text"/>、<paramref name="selector"/> 或 <paramref name="replacement"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The operation is undefined, the directive is blank, the section kind is
    /// <see cref="InitRcSection.Import"/>, the replacement is not one statement of the selected directive, the expected counts do
    /// not match, the old-value guard fails, the output exceeds the configuration limit, or the patched text fails to parse.
    /// <para>操作未定义、指令名为空、段类型为 <see cref="InitRcSection.Import"/>、替换文本不是所选指令的单条语句、
    /// 期望数量不匹配、旧值守卫校验失败、输出超出配置上限，或补丁后的文本解析失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
    public static TextPatchResult Apply(string text, InitRcSection kind, string selector, string directive,
        InitRcOperation operation, string replacement, bool checkOldValue, string expectedOldValue,
        int expectedSections, int expectedMatches, CancellationToken token)
    {
        if (!Enum.IsDefined(operation) || string.IsNullOrWhiteSpace(directive) || kind == InitRcSection.Import)
        {
            throw new InvalidDataException("Editing requires a service/action body directive and a supported operation.");
        }
        if (operation == InitRcOperation.Replace)
        {
            var lines = Lex(replacement, token);
            if (lines.Count != 1 || lines[0].Start != 0 || lines[0].End != replacement.Length
                || IsHeader(lines[0].Words[0]) || lines[0].Words[0] != directive)
            {
                throw new InvalidDataException("Replacement must be exactly one statement with the selected directive name.");
            }
        }
        var matches = Select(text, kind, selector, directive, expectedSections, expectedMatches, token);
        var output = new StringBuilder();
        var position = 0;
        foreach (var line in matches)
        {
            token.ThrowIfCancellationRequested();
            if (checkOldValue && text[line.Start..line.End] != expectedOldValue)
            {
                throw new InvalidDataException("Init statement does not match the expected old source text.");
            }
            var start = operation == InitRcOperation.Delete ? line.LineStart : line.Start;
            var end = operation == InitRcOperation.Delete ? line.LineEnd : line.End;
            var value = operation == InitRcOperation.Delete ? "" : replacement;
            if ((long)output.Length + start - position + value.Length > TextPatch.MaxCharacters)
            {
                throw new InvalidDataException("Init output exceeds the configuration limit.");
            }
            output.Append(text, position, start - position).Append(value);
            position = end;
        }
        if ((long)output.Length + text.Length - position > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Init output exceeds the configuration limit.");
        }
        output.Append(text, position, text.Length - position);
        var result = output.ToString();
        Parse(result, token);
        return new TextPatchResult(result, matches.Count, result != text);
    }

    private static List<Statement> Select(string text, InitRcSection kind, string selector, string directive,
        int expectedSections, int expectedMatches, CancellationToken token)
    {
        if (!Enum.IsDefined(kind) || expectedSections is < 0 or > 100000 || expectedMatches is < 0 or > 100000)
        {
            throw new InvalidDataException("Invalid init selector or expected count.");
        }
        var selection = Lex(selector, token);
        if (selection.Count != 1 || selection[0].Words.Length == 0
            || kind != InitRcSection.Action && selection[0].Words.Length != 1)
        {
            throw new InvalidDataException("Selector must contain one service name, one import path, or one action trigger expression.");
        }
        if (directive.Length > 16384 || directive.Any(char.IsWhiteSpace) || directive.Contains('\0'))
        {
            throw new InvalidDataException("Directive must be a single literal name or empty for a header query.");
        }
        var keyword = kind switch { InitRcSection.Service => "service", InitRcSection.Action => "on", _ => "import" };
        var sections = Parse(text, token).Where(section => section.Header.Words[0] == keyword
            && (kind == InitRcSection.Action
                ? section.Header.Words.Skip(1).SequenceEqual(selection[0].Words)
                : section.Header.Words[1] == selection[0].Words[0])).ToArray();
        if (sections.Length != expectedSections)
        {
            throw new InvalidDataException($"Expected {expectedSections} init sections, found {sections.Length}.");
        }
        var matches = sections.SelectMany(section => directive.Length == 0
            ? [section.Header] : section.Body.Where(line => line.Words[0] == directive)).ToList();
        if (matches.Count != expectedMatches)
        {
            throw new InvalidDataException($"Expected {expectedMatches} init statements, found {matches.Count}.");
        }
        return matches;
    }

    private static bool IsHeader(string word) => word is "service" or "on" or "import";

    private static List<Section> Parse(string text, CancellationToken token)
    {
        var sections = new List<Section>();
        Section? current = null;
        foreach (var line in Lex(text, token))
        {
            token.ThrowIfCancellationRequested();
            if (IsHeader(line.Words[0]))
            {
                if (line.Words.Length < (line.Words[0] == "service" ? 3 : 2)
                    || line.Words[0] == "import" && line.Words.Length != 2)
                {
                    throw new PatchingCorruptedException("Malformed init section header.");
                }
                current = new Section(line, []);
                sections.Add(current);
            }
            else if (current is null || current.Header.Words[0] == "import")
            {
                throw new PatchingCorruptedException("Init statement has no service or action section.");
            }
            else
            {
                current.Body.Add(line);
            }
        }
        return sections;
    }

    private static List<Statement> Lex(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TextPatch.ValidateSize(text);
        if (text.Contains('\0')) { throw new PatchingCorruptedException("NUL is not permitted in init text."); }
        var result = new List<Statement>();
        var words = new List<string>();
        var ranges = new List<(int Start, int End)>();
        var position = 0;
        var lineStart = 0;
        var start = 0;
        var end = 0;
        var physicalLines = 1;
        while (position < text.Length)
        {
            token.ThrowIfCancellationRequested();
            var character = text[position];
            if (character is ' ' or '\t' or '\r') { position++; continue; }
            if (character == '#')
            {
                while (position < text.Length && text[position] != '\n') { position++; }
                continue;
            }
            if (character == '\n')
            {
                position++;
                CountLine();
                Finish();
                lineStart = position;
                continue;
            }
            if (words.Count == 0) { start = position; }
            var tokenStart = position;
            var value = new StringBuilder();
            while (position < text.Length && text[position] is not (' ' or '\t' or '\r' or '\n'))
            {
                token.ThrowIfCancellationRequested();
                character = text[position++];
                if (character == '"')
                {
                    while (position < text.Length && text[position] != '"')
                    {
                        if (text[position] == '\n') { CountLine(); }
                        value.Append(text[position++]);
                        CheckToken();
                    }
                    if (position == text.Length) { throw new PatchingCorruptedException("Unterminated init quote."); }
                    position++;
                }
                else if (character == '\\')
                {
                    if (position == text.Length) { throw new PatchingCorruptedException("Dangling init escape."); }
                    character = text[position++];
                    if (character == '\r')
                    {
                        if (position == text.Length || text[position] != '\n') { continue; }
                        character = text[position++];
                    }
                    if (character == '\n')
                    {
                        CountLine();
                        while (position < text.Length && text[position] is ' ' or '\t') { position++; }
                    }
                    else
                    {
                        value.Append(character switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => character });
                    }
                }
                else
                {
                    value.Append(character);
                }
                CheckToken();
            }
            end = position;
            words.Add(value.ToString());
            ranges.Add((tokenStart, position));
            if (words.Count > 1024) { throw new PatchingCorruptedException("Too many init statement arguments."); }

            void CheckToken()
            {
                if (value.Length > 16384) { throw new PatchingCorruptedException("Init token exceeds the limit."); }
            }
        }
        Finish();
        return result;

        void CountLine()
        {
            if (++physicalLines > 100000) { throw new PatchingCorruptedException("Too many init lines."); }
        }

        void Finish()
        {
            if (words.Count == 0) { return; }
            result.Add(new Statement(start, end, lineStart, position, [.. words], [.. ranges]));
            words.Clear();
            ranges.Clear();
        }
    }
}