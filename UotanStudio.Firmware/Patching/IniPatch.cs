namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Edits case-sensitive INI with '=' assignments, whole-line #/; comments and literal inline values.
/// <para>编辑区分大小写的 INI：使用 <c>=</c> 赋值、整行 <c>#</c>/<c>;</c> 注释，行内值按字面处理。</para>
/// </summary>
public static class IniPatch
{
    /// <summary>
    /// Reads a key from a named section, or the global section when section is empty.
    /// <para>读取指定段中的键；<paramref name="section"/> 为空时读取全局段。</para>
    /// </summary>
    /// <param name="text">INI source bounded by <see cref="TextPatch.MaxCharacters"/>, using LF or CRLF line endings without NUL.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 的 INI 源文本，使用 LF 或 CRLF 换行且不含 NUL。</para></param>
    /// <param name="section">Section name without brackets, or an empty string for the keys above the first header; at most 4096
    /// characters. <para>不含方括号的段名，空字符串表示首个段头之前的键；最多 4096 个字符。</para></param>
    /// <param name="key">Exact, case-sensitive key name; at most 4096 characters. <para>区分大小写的精确键名，最多 4096 个字符。</para></param>
    /// <param name="cancellationToken">Checked before scanning and on every line. <para>在扫描开始前以及每一行都会检查。</para></param>
    /// <returns>The value with surrounding spaces trimmed, or <see langword="null"/> when the section or key is absent.
    /// <para>去除首尾空格后的值；段或键不存在时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, <paramref name="section"/> or
    /// <paramref name="key"/> is <see langword="null"/>.
    /// <para><paramref name="text"/>、<paramref name="section"/> 或 <paramref name="key"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The document exceeds the size or 100000-line limit, a name is invalid, a line is not
    /// a comment, header or <c>key=value</c> assignment, a section or key is duplicated, or the text contains a stray CR or NUL.
    /// <para>文档超出大小或 100000 行上限、名称非法、某行既不是注释、段头也不是 <c>key=value</c> 赋值、
    /// 段或键重复，或文本含多余的 CR 或 NUL。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已发出取消信号。</para></exception>
    public static string? Read(string text, string section, string key, CancellationToken cancellationToken = default)
        => Parse(text, section, key, cancellationToken).Entry?.Value;

    /// <summary>
    /// Sets or deletes a unique key while preserving unrelated lines and the existing newline convention.
    /// <para>设置或删除唯一的键，同时保留无关行并沿用文档已有的换行风格。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// IniPatch.Apply(ini, "service", "timeout", "30", false, true, "20", false, token);
    /// </code>
    /// </example>
    /// <param name="text">INI source bounded by <see cref="TextPatch.MaxCharacters"/>, using LF or CRLF line endings without NUL.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 的 INI 源文本，使用 LF 或 CRLF 换行且不含 NUL。</para></param>
    /// <param name="section">Section name without brackets, or an empty string for the global section; created with a header line
    /// when it does not exist yet, except for the global section. <para>不含方括号的段名，空字符串表示全局段；段不存在时会写入段头行，
    /// 全局段除外。</para></param>
    /// <param name="key">Exact, case-sensitive key name; at most 4096 characters. <para>区分大小写的精确键名，最多 4096 个字符。</para></param>
    /// <param name="value">Single-line value without NUL and without significant leading or trailing whitespace; ignored when
    /// <paramref name="delete"/> is set. <para>单行值，不含 NUL 且首尾不得有实质空白；<paramref name="delete"/> 置位时被忽略。</para></param>
    /// <param name="delete">Removes the whole entry line instead of assigning <paramref name="value"/>.
    /// <para>删除整行条目，而不是赋值为 <paramref name="value"/>。</para></param>
    /// <param name="checkOldValue">Requires the existing entry to hold <paramref name="expectedOldValue"/>; an absent key also
    /// fails the guard. <para>要求已存在的条目当前值为 <paramref name="expectedOldValue"/>；键不存在时该守卫同样失败。</para></param>
    /// <param name="expectedOldValue">Old value to verify; must itself be a single line when <paramref name="checkOldValue"/> is set.
    /// <para>需要校验的旧值；启用 <paramref name="checkOldValue"/> 时它也必须是单行文本。</para></param>
    /// <param name="allowCreate">Permits inserting a missing key at the end of its section.
    /// <para>允许在段末插入缺失的键。</para></param>
    /// <param name="cancellationToken">Checked before scanning and on every line of each parse.
    /// <para>在扫描开始前以及每次解析的每一行都会检查。</para></param>
    /// <returns>The output text, match count <c>1</c> when the key existed and <c>0</c> when it was created or a delete missed,
    /// and whether the text changed. A key already holding <paramref name="value"/> returns the input untouched.
    /// <para>输出文本；键已存在时匹配数量为 <c>1</c>，新建或删除未命中时为 <c>0</c>；并指明文本是否发生变化。
    /// 键的当前值已等于 <paramref name="value"/> 时原样返回输入。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, <paramref name="section"/>, <paramref name="key"/> or
    /// <paramref name="value"/> is <see langword="null"/>.
    /// <para><paramref name="text"/>、<paramref name="section"/>、<paramref name="key"/> 或 <paramref name="value"/>
    /// 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">A name, value or line breaks this INI dialect, the output exceeds a size limit, the
    /// old-value guard fails, a missing key is edited with creation disabled, or a missing key is deleted while the guard is
    /// enabled. <para>名称、取值或某行违反本 INI 方言、输出超出大小上限、旧值守卫校验失败、在禁用新建时编辑缺失的键，
    /// 或在启用守卫时删除缺失的键。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已发出取消信号。</para></exception>
    public static TextPatchResult Apply(string text, string section, string key, string value, bool delete,
        bool checkOldValue, string expectedOldValue, bool allowCreate, CancellationToken cancellationToken = default)
    {
        var document = Parse(text, section, key, cancellationToken);
        ValidateSingleLine(value);
        if (checkOldValue)
        {
            ValidateSingleLine(expectedOldValue);
        }
        if (!delete && !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("INI values cannot have significant leading or trailing whitespace in this dialect.");
        }
        var entry = document.Entry;
        if (entry is not null && !delete && string.Equals(entry.Value, value, StringComparison.Ordinal))
        {
            return new TextPatchResult(text, 1, false);
        }
        if (checkOldValue && (entry is null || !string.Equals(entry.Value, expectedOldValue, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The INI value does not match the expected old value.");
        }
        string output;
        if (entry is null)
        {
            if (delete)
            {
                return new TextPatchResult(text, 0, false);
            }
            if (!allowCreate)
            {
                throw new InvalidDataException("The INI key is missing; creation is disabled.");
            }
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var position = document.InsertAt;
            var prefix = position > 0 && text[position - 1] != '\n' ? newline : string.Empty;
            var header = document.SectionExists ? string.Empty : "[" + section + "]" + newline;
            output = text.Insert(position, prefix + header + key + "=" + value + newline);
        }
        else
        {
            output = delete ? text.Remove(entry.Start, entry.End - entry.Start)
                : text[..entry.ValueStart] + value + text[entry.ValueEnd..];
        }
        TextPatch.ValidateSize(output);
        Parse(output, section, key, cancellationToken);
        return new TextPatchResult(output, entry is null ? 0 : 1, true);
    }

    private static Document Parse(string text, string section, string key, CancellationToken token)
    {
        TextPatch.ValidateSize(text);
        ValidateName(section, true);
        ValidateName(key, false);
        token.ThrowIfCancellationRequested();
        var current = string.Empty;
        var sections = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Entry? entry = null;
        var sectionExists = section.Length == 0;
        var insertAt = text.Length;
        var offset = 0;
        var lines = 0;
        while (offset < text.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++lines > 100_000)
            {
                throw new InvalidDataException("INI exceeds the line limit.");
            }
            var newline = text.IndexOf('\n', offset);
            var end = newline < 0 ? text.Length : newline + 1;
            var line = text.AsSpan(offset, end - offset).TrimEnd('\n').TrimEnd('\r');
            if (line.Contains('\r') || line.Contains('\0'))
            {
                throw new InvalidDataException("INI contains an unsupported line separator or NUL.");
            }
            var trimmed = line.Trim();
            if (!trimmed.IsEmpty && trimmed[0] is not '#' and not ';')
            {
                if (trimmed[0] == '[')
                {
                    if (trimmed[^1] != ']')
                    {
                        throw new InvalidDataException("Invalid INI section header.");
                    }
                    var next = trimmed[1..^1].Trim().ToString();
                    ValidateName(next, false);
                    if (!sections.Add(next))
                    {
                        throw new InvalidDataException("Duplicate INI sections are ambiguous.");
                    }
                    if (current == section)
                    {
                        insertAt = offset;
                    }
                    current = next;
                    keys.Clear();
                    sectionExists |= current == section;
                }
                else
                {
                    var equals = line.IndexOf('=');
                    if (equals < 0)
                    {
                        throw new InvalidDataException("INI entries must be single-line key=value assignments.");
                    }
                    var name = line[..equals].Trim().ToString();
                    ValidateName(name, false);
                    if (!keys.Add(name))
                    {
                        throw new InvalidDataException("Duplicate INI keys are ambiguous.");
                    }
                    if (current == section && name == key)
                    {
                        var raw = line[(equals + 1)..];
                        var value = raw.Trim();
                        var start = offset + equals + 1 + raw.Length - raw.TrimStart().Length;
                        entry = new Entry(offset, end, start, start + value.Length, value.ToString());
                    }
                }
            }
            offset = end;
        }
        return new Document(entry, sectionExists, insertAt);
    }

    private static void ValidateName(string name, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(name);
        if ((!allowEmpty && name.Length == 0) || name.Length > 4096 || name != name.Trim()
            || name.IndexOfAny(['\r', '\n', '\0', '[', ']', '=']) >= 0
            || name.Length > 0 && name[0] is '#' or ';')
        {
            throw new InvalidDataException("Invalid INI section or key name.");
        }
    }

    private static void ValidateSingleLine(string value)
    {
        TextPatch.ValidateSize(value);
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new InvalidDataException("INI values must occupy one line.");
        }
    }

    private sealed record Entry(int Start, int End, int ValueStart, int ValueEnd, string Value);
    private sealed record Document(Entry? Entry, bool SectionExists, int InsertAt);
}