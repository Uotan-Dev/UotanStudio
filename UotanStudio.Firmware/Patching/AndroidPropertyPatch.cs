namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Edits Android property files without normalizing unrelated lines or comments.
/// <para>编辑 Android 属性文件，同时不会规范化无关的行或注释。</para>
/// </summary>
public static class AndroidPropertyPatch
{
    /// <summary>Reads exactly one key; duplicate keys are ambiguous and rejected.
    /// <para>读取唯一的键；重复的键视为歧义并被拒绝。</para></summary>
    /// <param name="text">The property file source text.<para>属性文件源文本。</para></param>
    /// <param name="key">ASCII property key using letters, digits, <c>.</c>, <c>_</c> or <c>-</c>.
    /// <para>使用字母、数字、<c>.</c>、<c>_</c> 或 <c>-</c> 的 ASCII 属性键。</para></param>
    /// <returns>The trimmed value of the key, or <see langword="null"/> when the key is absent.
    /// <para>该键去除首尾空白后的值；键不存在时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidDataException">The text exceeds the size limit, the key is empty or malformed, or the key occurs more than once.
    /// <para>文本超出大小上限、键为空或格式错误，或键出现多次。</para></exception>
    public static string? Read(string text, string key)
    {
        var entries = Find(text, key);
        return entries.Count == 0 ? null : entries[0].Value;
    }

    /// <summary>Sets or deletes a key, optionally requiring its old value. Repeated sets are idempotent.
    /// <para>设置或删除某个键，可选择要求校验其旧值。重复设置为幂等。</para></summary>
    /// <param name="text">The existing property file source text.<para>现有的属性文件源文本。</para></param>
    /// <param name="key">ASCII property key using letters, digits, <c>.</c>, <c>_</c> or <c>-</c>.
    /// <para>使用字母、数字、<c>.</c>、<c>_</c> 或 <c>-</c> 的 ASCII 属性键。</para></param>
    /// <param name="value">Single-line value to write; must contain no CR, LF or NUL.
    /// <para>要写入的单行值；不得包含 CR、LF 或 NUL。</para></param>
    /// <param name="delete">When <see langword="true"/>, the matching line is removed instead of set.
    /// <para>为 <see langword="true"/> 时删除匹配行而非设置。</para></param>
    /// <param name="checkOldValue">When <see langword="true"/>, the current value must equal <paramref name="expectedOldValue"/>.
    /// <para>为 <see langword="true"/> 时，当前值必须与 <paramref name="expectedOldValue"/> 相等。</para></param>
    /// <param name="expectedOldValue">The value required when <paramref name="checkOldValue"/> is set.
    /// <para>当 <paramref name="checkOldValue"/> 置位时要求的值。</para></param>
    /// <param name="allowCreate">When <see langword="true"/>, a set of an absent key appends it; a missing key never permits an old-value check.
    /// <para>为 <see langword="true"/> 时，对缺失键的设置会追加该键；缺失键从不允许旧值校验。</para></param>
    /// <returns>The patch result with the updated text, the number of matched entries and whether the text changed.
    /// <para>补丁结果，包含更新后的文本、匹配条目数量以及文本是否发生变化。</para></returns>
    /// <exception cref="InvalidDataException">The text exceeds the size limit, the key is malformed or duplicated, the value spans multiple lines, the key is missing under the applied guards, or the old value does not match.
    /// <para>文本超出大小上限、键格式错误或重复、值跨多行、在应用守卫下键缺失，或旧值不匹配。</para></exception>
    public static TextPatchResult Apply(string text, string key, string value, bool delete,
        bool checkOldValue, string expectedOldValue, bool allowCreate)
    {
        var entries = Find(text, key);
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new InvalidDataException("A property value must occupy one line.");
        }
        if (entries.Count == 0)
        {
            if (delete && !checkOldValue)
            {
                return new TextPatchResult(text, 0, false);
            }
            if (!allowCreate || checkOldValue)
            {
                throw new InvalidDataException($"Property '{key}' is missing.");
            }
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var separator = text.Length == 0 || text.EndsWith('\n') ? string.Empty : newline;
            var added = text + separator + key + "=" + value + newline;
            TextPatch.ValidateSize(added);
            return new TextPatchResult(added, 0, true);
        }

        var entry = entries[0];
        if (!delete && string.Equals(entry.Value, value, StringComparison.Ordinal))
        {
            return new TextPatchResult(text, 1, false);
        }
        if (checkOldValue && !string.Equals(entry.Value, expectedOldValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Property '{key}' does not match the expected old value.");
        }
        var result = delete
            ? text.Remove(entry.Start, entry.End - entry.Start)
            : text[..entry.ValueStart] + value + text[entry.ValueEnd..];
        TextPatch.ValidateSize(result);
        return new TextPatchResult(result, 1, !string.Equals(text, result, StringComparison.Ordinal));
    }

    /// <summary>Appends an <c>import &lt;path&gt;</c> directive to a property file. Appending an import that is already present leaves the source untouched, so repeated imports are idempotent.
    /// <para>向属性文件追加 <c>import &lt;path&gt;</c> 指令。若该导入已存在则保持源文本不变，因此重复导入是幂等的。</para></summary>
    /// <param name="text">The existing property file source text.<para>现有的属性文件源文本。</para></param>
    /// <param name="path">Absolute single-line path to import, e.g. <c>/my_product/build.prop</c>.
    /// <para>待导入的绝对单行路径，例如 <c>/my_product/build.prop</c>。</para></param>
    /// <returns>The patch result with the directive appended, the number of matching existing directives and whether the text changed.
    /// <para>补丁结果，包含追加指令后的文本、已存在的匹配指令数量以及文本是否发生变化。</para></returns>
    /// <exception cref="InvalidDataException">The text exceeds the size limit, the path is not a safe absolute single-line path, or the output exceeds the size limit.
    /// <para>文本超出大小上限、路径不是安全的绝对单行路径，或输出超出大小上限。</para></exception>
    public static TextPatchResult ImportLine(string text, string path)
    {
        TextPatch.ValidateSize(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/') || path.Length > 4096 || path.Contains('\0') || path.Any(char.IsWhiteSpace)
            || path.IndexOfAny(['=', '#']) >= 0
            || path.Split('/').Any(part => part is "." or ".."))
        {
            throw new InvalidDataException("Import path must be a safe absolute single-line path.");
        }

        var directive = "import " + path;
        var offset = 0;
        while (offset < text.Length)
        {
            var newline = text.IndexOf('\n', offset);
            var end = newline < 0 ? text.Length : newline + 1;
            if (string.Equals(text.AsSpan(offset, end - offset).Trim(['\r', '\n', ' ', '\t']).ToString(),
                    directive, StringComparison.Ordinal))
            {
                return new TextPatchResult(text, 1, false);
            }

            offset = end;
        }

        var separator = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = text.Length == 0 || text.EndsWith('\n') ? string.Empty : separator;
        var result = text + prefix + directive + separator;
        TextPatch.ValidateSize(result);
        return new TextPatchResult(result, 0, true);
    }

    private static List<PropertyEntry> Find(string text, string key)
    {
        TextPatch.ValidateSize(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new InvalidDataException("Invalid Android property key.");
        }
        var entries = new List<PropertyEntry>();
        var offset = 0;
        while (offset < text.Length)
        {
            var newline = text.IndexOf('\n', offset);
            var end = newline < 0 ? text.Length : newline + 1;
            var line = text.AsSpan(offset, end - offset).TrimEnd('\n').TrimEnd('\r');
            var trimmed = line.TrimStart();
            var equals = line.IndexOf('=');
            if (!trimmed.StartsWith("#", StringComparison.Ordinal) && equals >= 0
                && line[..equals].Trim().SequenceEqual(key))
            {
                var rawValue = line[(equals + 1)..];
                var value = rawValue.Trim();
                var start = offset + equals + 1 + rawValue.Length - rawValue.TrimStart().Length;
                entries.Add(new PropertyEntry(offset, end, start, start + value.Length, value.ToString()));
                if (entries.Count > 1)
                {
                    throw new InvalidDataException($"Property '{key}' occurs more than once.");
                }
            }
            offset = end;
        }
        return entries;
    }

    private sealed record PropertyEntry(int Start, int End, int ValueStart, int ValueEnd, string Value);
}