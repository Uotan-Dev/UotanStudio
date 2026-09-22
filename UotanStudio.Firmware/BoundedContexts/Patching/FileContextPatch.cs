namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Exact rule selection and label editing for text file_contexts; does not evaluate path regexes or policy validity.</summary>
public static class FileContextPatch
{
    /// <summary>Appends a missing exact rule identity; equivalent labels preserve the original source.</summary>
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

    /// <summary>Removes one complete rule line; missing rules are unchanged unless an old-label guard is enabled.</summary>
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
    /// <summary>Reads one rule identified by its literal expression and optional file-kind qualifier.</summary>
    public static string Read(string text, string expression, string fileKind, CancellationToken token)
    {
        var range = Select(text, expression, fileKind, token);
        return text.Substring(range.Start, range.Length);
    }

    /// <summary>Changes only the selected context token, preserving rule order and all other source bytes represented by the text.</summary>
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