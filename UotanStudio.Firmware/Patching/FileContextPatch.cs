namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Outcome of rebasing a file_contexts document under a merged-partition mount prefix.
/// <para>将 file_contexts 文档按合并分区的挂载前缀改基的结果。</para>
/// </summary>
/// <param name="Text">The rebased document text. <para>改基后的文档文本。</para></param>
/// <param name="PrefixedRules">Literal expressions that received the mount prefix. <para>被加上挂载前缀的字面表达式数量。</para></param>
/// <param name="DroppedRules">Root-default and duplicate-prefix rules that were removed. <para>被移除的根默认条目与前缀重复条目数量。</para></param>
/// <param name="MetacharacterWarnings">
/// One-based line descriptions of regex-bearing expressions kept verbatim without a prefix rewrite.
/// <para>带正则元字符、保持原样且未做前缀重写的表达式，附一基行号说明。</para>
/// </param>
/// <param name="Changed"><see langword="true"/> when the text differs from the input. <para>文本与输入不同时为 <see langword="true"/>。</para></param>
public sealed record FileContextRebaseResult(string Text, int PrefixedRules, int DroppedRules,
    IReadOnlyList<string> MetacharacterWarnings, bool Changed);

/// <summary>
/// Exact rule selection and label editing for text file_contexts; does not evaluate path regexes or policy validity.
/// <para>针对文本 file_contexts 的精确规则选择与标签编辑；不求值路径正则或校验策略有效性。</para>
/// </summary>
public static class FileContextPatch
{
    private static readonly char[] RegexMetacharacters = ['.', '^', '$', '*', '+', '?', '(', ')', '[', ']', '{', '}', '|', '\\'];

    /// <summary>
    /// Rebases a partition-owned file_contexts document under <c>&lt;mountPrefix&gt;/&lt;partition&gt;</c>:
    /// removes the leading <c>/</c> default entry and duplicate prefix defaults, prefixes every literal
    /// expression, keeps already-rebased and regex-bearing expressions verbatim (reporting the latter as
    /// warnings), and appends the <c>system_file</c> default entry. Rebasing an already-rebased document
    /// yields the same text.
    /// <para>将分区自有的 file_contexts 文档改基到 <c>&lt;mountPrefix&gt;/&lt;partition&gt;</c> 之下：移除首行的 <c>/</c>
    /// 默认条目与前缀默认重复条目，为每个字面表达式加上前缀，保持已改基条目与含正则条目原样（后者以警告形式报告），
    /// 并追加 <c>system_file</c> 默认条目。对已改基文档再次改基得到完全相同的文本。</para>
    /// </summary>
    /// <param name="text">
    /// The existing file_contexts source text. <para>现有的 file_contexts 源文本。</para>
    /// </param>
    /// <param name="mountPrefix">
    /// Literal absolute mount prefix such as <c>/system</c>; regex metacharacters are rejected.
    /// <para>字面绝对挂载前缀（如 <c>/system</c>）；正则元字符会被拒绝。</para>
    /// </param>
    /// <param name="partition">
    /// Merged partition name (<c>[A-Za-z0-9_-]</c>, at most 64 characters), e.g. <c>my_product</c>.
    /// <para>被合并的分区名（<c>[A-Za-z0-9_-]</c>，至多 64 个字符），如 <c>my_product</c>。</para>
    /// </param>
    /// <param name="token">
    /// Cancels the bounded line scan. <para>取消有界的逐行扫描。</para>
    /// </param>
    /// <returns>
    /// The rebased document with accounting and warning details. <para>改基后的文档及其计数与警告信息。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The prefix or partition is invalid, a rule is malformed or duplicated against the appended default, or a size limit is exceeded.
    /// <para>前缀或分区无效、规则格式错误或与追加的默认条目重复，或超出大小上限。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled. <para><paramref name="token"/> 被触发。</para></exception>
    public static FileContextRebaseResult Rebase(string text, string mountPrefix, string partition, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TextPatch.ValidateSize(text);
        if (text.Contains('\0'))
            throw new InvalidDataException("File contexts source must not contain NUL.");
        var prefix = BuildRebasePrefix(mountPrefix, partition);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var output = new System.Text.StringBuilder();
        var prefixed = 0;
        var dropped = 0;
        var warnings = new List<string>();
        var position = 0;
        var lineNumber = 0;
        while (position < text.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++lineNumber > 100000)
                throw new InvalidDataException("Too many file context lines.");
            var end = text.IndexOf('\n', position);
            var lineEnd = end < 0 ? text.Length : end + 1;
            var contentEnd = end < 0 ? text.Length : end > position && text[end - 1] == '\r' ? end - 1 : end;
            var contentStart = position;
            while (contentStart < contentEnd && text[contentStart] is ' ' or '\t')
                contentStart++;
            if (contentStart >= contentEnd || text[contentStart] == '#')
            {
                output.Append(text, position, lineEnd - position);
                position = lineEnd;
                continue;
            }

            var fields = ReadRuleFields(text, contentStart, contentEnd, lineNumber);
            var expression = text.Substring(fields[0].Start, fields[0].Length);
            if (expression == "/" || expression == prefix)
            {
                dropped++;
                position = lineEnd;
                continue;
            }

            if (expression.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                output.Append(text, position, lineEnd - position);
            }
            else if (expression.IndexOfAny(RegexMetacharacters) >= 0)
            {
                warnings.Add($"line {lineNumber}: regex expression '{expression}' kept without a prefix rewrite");
                if (warnings.Count > 10000)
                    throw new InvalidDataException("Too many regex file context warnings.");
                output.Append(text, position, lineEnd - position);
            }
            else
            {
                output.Append(text, position, fields[0].Start - position)
                    .Append(prefix).Append(expression)
                    .Append(text, fields[0].Start + fields[0].Length, lineEnd - (fields[0].Start + fields[0].Length));
                prefixed++;
            }

            position = lineEnd;
        }

        var separator = output.Length > 0 && output[^1] != '\n' ? newline : "";
        output.Append(separator).Append(prefix).Append(" u:object_r:system_file:s0").Append(newline);
        var result = output.ToString();
        TextPatch.ValidateSize(result);
        Read(result, prefix, "", token);
        return new FileContextRebaseResult(result, prefixed, dropped, warnings,
            !string.Equals(text, result, StringComparison.Ordinal));
    }

    private static (int Start, int Length)[] ReadRuleFields(string text, int contentStart, int contentEnd, int lineNumber)
    {
        var fields = new List<(int Start, int Length)>(3);
        var cursor = contentStart;
        while (cursor < contentEnd)
        {
            var start = cursor;
            while (cursor < contentEnd && text[cursor] is not (' ' or '\t'))
                cursor++;
            fields.Add((start, cursor - start));
            if (fields.Count > 3)
                throw new InvalidDataException($"File context rule on line {lineNumber} requires two or three fields.");
            while (cursor < contentEnd && text[cursor] is ' ' or '\t')
                cursor++;
        }

        if (fields.Count < 2)
            throw new InvalidDataException($"File context rule on line {lineNumber} requires a context.");
        ValidateKind(fields.Count == 3 ? text.Substring(fields[1].Start, fields[1].Length) : "");
        ValidateContext(text.Substring(fields[^1].Start, fields[^1].Length));
        if (!text.Substring(fields[0].Start, fields[0].Length).StartsWith('/'))
            throw new InvalidDataException($"File context expression on line {lineNumber} must begin with a slash.");
        return [.. fields];
    }

    private static string BuildRebasePrefix(string mountPrefix, string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (partition.Length > 64 || partition.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new InvalidDataException("Rebase partition name must use letters, digits, '_' or '-'.");
        var trimmed = mountPrefix.TrimEnd('/');
        if (!trimmed.StartsWith('/') || trimmed.Length > 4096
            || trimmed.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not '/'))
        {
            throw new InvalidDataException("Mount prefix must be a literal absolute path without regex metacharacters.");
        }

        return (trimmed.Length == 1 ? "" : trimmed) + "/" + partition;
    }

    /// <summary>
    /// Appends a missing exact rule identity; equivalent labels preserve the original source.
    /// <para>追加缺失的精确规则标识；等价标签会保留原始源文本。</para>
    /// </summary>
    /// <param name="text">
    /// The existing file_contexts source text. <para>现有的 file_contexts 源文本。</para>
    /// </param>
    /// <param name="expression">
    /// Literal absolute-path expression, matched verbatim rather than as a regex. <para>字面绝对路径表达式，按原样匹配而非作为正则。</para>
    /// </param>
    /// <param name="fileKind">
    /// Optional file-kind qualifier (<c>--</c>, <c>-d</c>, <c>-l</c>, <c>-b</c>, <c>-c</c>, <c>-p</c>, <c>-s</c>) or empty for any. <para>可选的文件类型限定符（<c>--</c>、<c>-d</c>、<c>-l</c>、<c>-b</c>、<c>-c</c>、<c>-p</c>、<c>-s</c>），为空表示任意。</para>
    /// </param>
    /// <param name="context">
    /// SELinux <c>user:role:type</c> context, an optional range, or <c>&lt;&lt;none&gt;&gt;</c>. <para>SELinux <c>user:role:type</c> 上下文、可选范围，或 <c>&lt;&lt;none&gt;&gt;</c>。</para>
    /// </param>
    /// <param name="token">
    /// Cancels the bounded line scan. <para>取消有界的逐行扫描。</para>
    /// </param>
    /// <returns>
    /// The patch result with the rule appended, the number of matched existing rules and whether the text changed.
    /// <para>补丁结果，包含追加规则后的文本、匹配到的现有规则数量以及文本是否发生变化。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The expression, kind, context or size is invalid, a matching rule holds a different label, or the output exceeds the configuration limit.
    /// <para>表达式、类型、上下文或大小无效、匹配规则带有不同标签，或输出超出配置上限。</para>
    /// </exception>
    public static TextPatchResult Add(string text, string expression, string fileKind, string context, CancellationToken token)
    {
        ValidateContext(context);
        var range = Select(text, expression, fileKind, token, true);
        if (range.Length != 0)
        {
            if (text.Substring(range.Start, range.Length) != context)
            {
                throw new InvalidDataException("The existing file context rule has a different label.");
            }
            return new TextPatchResult(text, 1, false);
        }
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = text.Length > 0 && !text.EndsWith('\n') ? newline : "";
        var rule = expression + " " + (fileKind.Length == 0 ? "" : fileKind + " ") + context;
        if ((long)text.Length + separator.Length + rule.Length + newline.Length > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("File contexts output exceeds the configuration limit.");
        }
        var result = text + separator + rule + newline;
        Read(result, expression, fileKind, token);
        return new TextPatchResult(result, 0, true);
    }

    /// <summary>
    /// Removes one complete rule line; missing rules are unchanged unless an old-label guard is enabled.
    /// <para>删除一整条规则行；除非启用旧标签守卫，缺失规则时保持文本不变。</para>
    /// </summary>
    /// <param name="text">
    /// The existing file_contexts source text. <para>现有的 file_contexts 源文本。</para>
    /// </param>
    /// <param name="expression">
    /// Literal absolute-path expression identifying the rule. <para>标识该规则的字面绝对路径表达式。</para>
    /// </param>
    /// <param name="fileKind">
    /// Optional file-kind qualifier that together with <paramref name="expression"/> selects the rule. <para>与 <paramref name="expression"/> 共同选定规则可选文件类型限定符。</para>
    /// </param>
    /// <param name="checkOldValue">
    /// When <see langword="true"/>, the rule must exist and carry the expected label. <para>为 <see langword="true"/> 时，规则必须存在并带有期望标签。</para>
    /// </param>
    /// <param name="expectedOldValue">
    /// The context label required when <paramref name="checkOldValue"/> is set. <para>当 <paramref name="checkOldValue"/> 置位时要求的上下文标签。</para>
    /// </param>
    /// <param name="token">
    /// Cancels the bounded line scan. <para>取消有界的逐行扫描。</para>
    /// </param>
    /// <returns>
    /// The patch result with the rule line removed, the number of matched rules and whether the text changed.
    /// <para>补丁结果，包含删除规则行后的文本、匹配规则数量以及文本是否发生变化。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The expression, kind or size is invalid, the rule is duplicated, or the old-label guard fails.
    /// <para>表达式、类型或大小无效、规则重复，或旧标签守卫校验失败。</para>
    /// </exception>
    public static TextPatchResult Delete(string text, string expression, string fileKind, bool checkOldValue,
        string expectedOldValue, CancellationToken token)
    {
        var range = Select(text, expression, fileKind, token, true);
        if (range.Length == 0)
        {
            if (checkOldValue) { throw new InvalidDataException("Expected file context rule is missing."); }
            return new TextPatchResult(text, 0, false);
        }
        if (checkOldValue && text.Substring(range.Start, range.Length) != expectedOldValue)
        {
            throw new InvalidDataException("File context does not match the expected old value.");
        }
        var start = text.LastIndexOf('\n', range.Start) + 1;
        var end = text.IndexOf('\n', range.Start + range.Length);
        end = end < 0 ? text.Length : end + 1;
        return new TextPatchResult(text[..start] + text[end..], 1, true);
    }
    /// <summary>
    /// Reads one rule identified by its literal expression and optional file-kind qualifier.
    /// <para>读取由字面表达式和可选文件类型限定符标识的一条规则。</para>
    /// </summary>
    /// <param name="text">
    /// The file_contexts source text. <para>file_contexts 源文本。</para>
    /// </param>
    /// <param name="expression">
    /// Literal absolute-path expression to resolve. <para>待解析的字面绝对路径表达式。</para>
    /// </param>
    /// <param name="fileKind">
    /// Optional file-kind qualifier that narrows the match. <para>用于缩小匹配范围的可选文件类型限定符。</para>
    /// </param>
    /// <param name="token">
    /// Cancels the bounded line scan. <para>取消有界的逐行扫描。</para>
    /// </param>
    /// <returns>
    /// The SELinux context label assigned to the selected rule.
    /// <para>所选规则被赋予的 SELinux 上下文标签。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The expression, kind or size is invalid, or the rule is missing or duplicated.
    /// <para>表达式、类型或大小无效，或规则缺失或重复。</para>
    /// </exception>
    public static string Read(string text, string expression, string fileKind, CancellationToken token)
    {
        var range = Select(text, expression, fileKind, token);
        return text.Substring(range.Start, range.Length);
    }

    /// <summary>
    /// Changes only the selected context token, preserving rule order and all other source bytes represented by the text.
    /// <para>仅更改选定项的上下文标记，保留规则顺序以及由文本表示的所有其他源字节。</para>
    /// </summary>
    /// <param name="text">
    /// The existing file_contexts source text. <para>现有的 file_contexts 源文本。</para>
    /// </param>
    /// <param name="expression">
    /// Literal absolute-path expression identifying the rule. <para>标识该规则的字面绝对路径表达式。</para>
    /// </param>
    /// <param name="fileKind">
    /// Optional file-kind qualifier that together with <paramref name="expression"/> selects the rule. <para>与 <paramref name="expression"/> 共同选定规则可选文件类型限定符。</para>
    /// </param>
    /// <param name="context">
    /// New SELinux context to write. <para>要写入的新 SELinux 上下文。</para>
    /// </param>
    /// <param name="checkOldValue">
    /// When <see langword="true"/>, the current label must equal <paramref name="expectedOldValue"/>. <para>为 <see langword="true"/> 时，当前标签必须与 <paramref name="expectedOldValue"/> 相等。</para>
    /// </param>
    /// <param name="expectedOldValue">
    /// The context label required when <paramref name="checkOldValue"/> is set. <para>当 <paramref name="checkOldValue"/> 置位时要求的上下文标签。</para>
    /// </param>
    /// <param name="token">
    /// Cancels the bounded line scan. <para>取消有界的逐行扫描。</para>
    /// </param>
    /// <returns>
    /// The patch result with the label replaced, one matched rule and whether the text changed.
    /// <para>补丁结果，包含替换标签后的文本、一个匹配规则以及文本是否发生变化。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The expression, kind, context or size is invalid, the rule is missing or duplicated, the old-label guard fails, or the output exceeds the configuration limit.
    /// <para>表达式、类型、上下文或大小无效、规则缺失或重复、旧标签守卫校验失败，或输出超出配置上限。</para>
    /// </exception>
    public static TextPatchResult Edit(string text, string expression, string fileKind, string context,
        bool checkOldValue, string expectedOldValue, CancellationToken token)
    {
        ValidateContext(context);
        var range = Select(text, expression, fileKind, token);
        var old = text.Substring(range.Start, range.Length);
        if (checkOldValue && old != expectedOldValue) { throw new InvalidDataException("File context does not match the expected old value."); }
        if (old == context) { return new TextPatchResult(text, 1, false); }
        if ((long)text.Length - range.Length + context.Length > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("File contexts output exceeds the configuration limit.");
        }
        var result = text[..range.Start] + context + text[(range.Start + range.Length)..];
        Read(result, expression, fileKind, token);
        return new TextPatchResult(result, 1, true);
    }

    private static (int Start, int Length) Select(string text, string expression, string fileKind, CancellationToken token, bool allowMissing = false)
    {
        token.ThrowIfCancellationRequested();
        TextPatch.ValidateSize(text);
        if (expression.Length is < 1 or > 16384 || expression.Any(char.IsWhiteSpace) || expression.Contains('\0')
            || !expression.StartsWith('/') || text.Contains('\0'))
        {
            throw new InvalidDataException("Use a literal absolute-path expression without whitespace or NUL.");
        }
        ValidateKind(fileKind);
        (int Start, int Length)? selected = null;
        var position = 0;
        var lines = 0;
        while (position < text.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++lines > 100000) { throw new InvalidDataException("Too many file context lines."); }
            var end = text.IndexOf('\n', position);
            if (end < 0) { end = text.Length; }
            var contentEnd = end > position && text[end - 1] == '\r' ? end - 1 : end;
            if (contentEnd - position > 16384) { throw new InvalidDataException("File context line exceeds the limit."); }
            var cursor = position;
            while (cursor < contentEnd && text[cursor] is ' ' or '\t') { cursor++; }
            if (cursor < contentEnd && text[cursor] != '#')
            {
                var fields = new List<(int Start, int Length)>();
                while (cursor < contentEnd)
                {
                    var start = cursor;
                    while (cursor < contentEnd && text[cursor] is not (' ' or '\t')) { cursor++; }
                    fields.Add((start, cursor - start));
                    if (fields.Count > 3) { throw new InvalidDataException("File context rules require two or three fields."); }
                    while (cursor < contentEnd && text[cursor] is ' ' or '\t') { cursor++; }
                }
                if (fields.Count < 2) { throw new InvalidDataException("File context rules require a context."); }
                string Field(int index) => text.Substring(fields[index].Start, fields[index].Length);
                var kind = fields.Count == 3 ? Field(1) : "";
                ValidateKind(kind);
                ValidateContext(Field(fields.Count - 1));
                if (!Field(0).StartsWith('/')) { throw new InvalidDataException("File context expression must begin with a slash."); }
                if (Field(0) == expression && kind == fileKind)
                {
                    if (selected is not null) { throw new InvalidDataException("Duplicate file context rule identities are ambiguous."); }
                    selected = fields[^1];
                }
            }
            position = end < text.Length ? end + 1 : end;
        }
        return selected ?? (allowMissing ? (0, 0) : throw new InvalidDataException("File context rule was not found."));
    }

    private static void ValidateKind(string value)
    {
        if (value is not ("" or "--" or "-d" or "-l" or "-b" or "-c" or "-p" or "-s"))
        {
            throw new InvalidDataException("Unsupported file context kind qualifier.");
        }
    }

    private static void ValidateContext(string value)
    {
        if (value == "<<none>>") { return; }
        if (value.Length is < 1 or > 4096 || value.Any(char.IsWhiteSpace) || value.Contains('\0'))
        {
            throw new InvalidDataException("Invalid SELinux context token.");
        }
        var parts = value.Split(':', 4);
        if (parts.Length < 3 || parts.Any(part => part.Length == 0)
            || parts.Take(3).Any(part => part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
        {
            throw new InvalidDataException("Context requires user:role:type and an optional nonempty range.");
        }
    }
}