namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Edits case-sensitive INI with '=' assignments, whole-line #/; comments and literal inline values.</summary>
public static class IniPatch
{
    /// <summary>Reads a key from a named section, or the global section when section is empty.</summary>
    public static string? Read(string text, string section, string key, CancellationToken cancellationToken = default)
        => Parse(text, section, key, cancellationToken).Entry?.Value;

    /// <summary>Sets or deletes a unique key while preserving unrelated lines and the existing newline convention.</summary>
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