namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Edits Android property files without normalizing unrelated lines or comments.</summary>
public static class AndroidPropertyPatch
{
    /// <summary>Reads exactly one key; duplicate keys are ambiguous and rejected.</summary>
    public static string? Read(string text, string key)
    {
        var entries = Find(text, key);
        return entries.Count == 0 ? null : entries[0].Value;
    }

    /// <summary>Sets or deletes a key, optionally requiring its old value. Repeated sets are idempotent.</summary>
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