using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>The five fields consumed by Android fs_mgr's non-proc fstab reader.</summary>
public enum FstabField { BlockDevice, MountPoint, FileSystem, MountOptions, FsMgrFlags }

/// <summary>Field or comma-delimited option editing; publishing is handled by the patch workspace.</summary>
public enum FstabOperation { SetValue, SetFlag, DeleteFlag, DeleteEntry }

/// <summary>Bounded lexical editing of Android's five-column fstab, preserving unrelated source text.</summary>
public static class FstabPatch
{
    /// <summary>Appends a missing mount/filesystem pair, preserves identical entries, and rejects conflicting pairs.</summary>
    public static TextPatchResult Add(string text, string blockDevice, string mountPoint, string fileSystem,
        string mountOptions, string fsMgrFlags, CancellationToken token)
    {
        string[] values = [blockDevice, mountPoint, fileSystem, mountOptions, fsMgrFlags];
        foreach (var value in values) { ValidateToken(value); }
        var rows = Select(text, mountPoint, fileSystem, null, token);
        if (rows.Count > 1) { throw new InvalidDataException("Duplicate mount/filesystem entries are ambiguous."); }
        if (rows.Count == 1)
        {
            var fields = rows[0];
            for (var index = 0; index < fields.Length; index++)
            {
                if (!text.AsSpan(fields[index].Start, fields[index].Length).SequenceEqual(values[index]))
                {
                    throw new InvalidDataException("An existing mount/filesystem entry has different field values.");
                }
            }
            return new TextPatchResult(text, 1, false);
        }
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = text.Length == 0 || text.EndsWith('\n') ? "" : newline;
        var result = text + separator + string.Join(' ', values) + newline;
        TextPatch.ValidateSize(result);
        Select(result, mountPoint, fileSystem, 1, token);
        return new TextPatchResult(result, 0, true);
    }

    /// <summary>Reads selected raw fields; repeated mount points require an explicit expected count or filesystem selector.</summary>
    public static IReadOnlyList<string> Read(string text, string mountPoint, string fileSystem, FstabField field,
        int expectedMatches, CancellationToken token)
    {
        ValidateField(field);
        return Select(text, mountPoint, fileSystem, expectedMatches, token)
            .Select(row => text.Substring(row[(int)field].Start, row[(int)field].Length)).ToArray();
    }

    /// <summary>Edits selected fields with optional full-field old-value guards.</summary>
    public static TextPatchResult Apply(string text, string mountPoint, string fileSystem, FstabField field,
        FstabOperation operation, string value, string flag, bool checkOldValue, string expectedOldValue,
        int expectedMatches, CancellationToken token)
    {
        ValidateField(field);
        if (!Enum.IsDefined(operation) || operation is FstabOperation.SetFlag or FstabOperation.DeleteFlag
            && field is not FstabField.MountOptions and not FstabField.FsMgrFlags)
        {
            throw new InvalidDataException("Flag operations require the mount-options or fs_mgr field.");
        }
        if (operation == FstabOperation.SetValue)
        {
            ValidateToken(value);
        }
        var rows = Select(text, mountPoint, fileSystem, expectedMatches, token);
        var output = new StringBuilder();
        var position = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            var range = row[(int)field];
            var old = text.Substring(range.Start, range.Length);
            if (checkOldValue && old != expectedOldValue)
            {
                throw new InvalidDataException("Fstab field does not match the expected old value.");
            }
            if (operation == FstabOperation.DeleteEntry)
            {
                var lineStart = text.LastIndexOf('\n', row[0].Start) + 1;
                var lineEnd = text.IndexOf('\n', row[4].Start + row[4].Length);
                lineEnd = lineEnd < 0 ? text.Length : lineEnd + 1;
                output.Append(text, position, lineStart - position);
                position = lineEnd;
                continue;
            }
            var replacement = operation == FstabOperation.SetValue ? value : EditFlag(old, flag, value, operation);
            ValidateToken(replacement);
            if ((long)output.Length + range.Start - position + replacement.Length > TextPatch.MaxCharacters)
            {
                throw new InvalidDataException("Fstab output exceeds the configuration limit.");
            }
            output.Append(text, position, range.Start - position).Append(replacement);
            position = range.Start + range.Length;
        }
        if ((long)output.Length + text.Length - position > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Fstab output exceeds the configuration limit.");
        }
        output.Append(text, position, text.Length - position);
        var result = output.ToString();
        return new TextPatchResult(result, rows.Count, result != text);
    }

    private static string EditFlag(string old, string flag, string value, FstabOperation operation)
    {
        ValidateToken(flag);
        if (flag.Contains(',') || flag.Contains('=') || value.Contains(','))
        {
            throw new InvalidDataException("A flag name cannot contain comma or equals, and its value cannot contain comma.");
        }
        var flags = old.Split(',').ToList();
        if (flags.Any(string.IsNullOrEmpty))
        {
            throw new InvalidDataException("Empty fstab flag items are not supported.");
        }
        var matches = flags.Select((item, index) => (item, index))
            .Where(pair => pair.item.Split('=')[0] == flag).Select(pair => pair.index).ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException("Duplicate target fstab flags are ambiguous.");
        }
        if (operation == FstabOperation.DeleteFlag)
        {
            if (matches.Length == 1)
            {
                flags.RemoveAt(matches[0]);
            }
        }
        else
        {
            var replacement = value.Length == 0 ? flag : flag + "=" + value;
            ValidateToken(replacement);
            if (matches.Length == 1)
            {
                flags[matches[0]] = replacement;
            }
            else
            {
                flags.Add(replacement);
            }
        }
        return flags.Count == 0 ? "defaults" : string.Join(',', flags);
    }

    private static List<(int Start, int Length)[]> Select(string text, string mountPoint, string fileSystem,
        int? expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TextPatch.ValidateSize(text);
        ValidateToken(mountPoint);
        if (fileSystem.Length > 0)
        {
            ValidateToken(fileSystem);
        }
        if (expected is < 0 or > 100000 || text.Contains('\0'))
        {
            throw new InvalidDataException("Invalid fstab text or expected count.");
        }
        var rows = new List<(int Start, int Length)[]>();
        var position = 0;
        var lines = 0;
        while (position < text.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++lines > 100000)
            {
                throw new InvalidDataException("Fstab line limit exceeded.");
            }
            var end = text.IndexOf('\n', position);
            end = end < 0 ? text.Length : end;
            var contentEnd = end > position && text[end - 1] == '\r' ? end - 1 : end;
            var cursor = position;
            while (cursor < contentEnd && text[cursor] is ' ' or '\t') { cursor++; }
            if (cursor < contentEnd && text[cursor] != '#')
            {
                var fields = new (int Start, int Length)[5];
                for (var index = 0; index < fields.Length; index++)
                {
                    while (cursor < contentEnd && text[cursor] is ' ' or '\t') { cursor++; }
                    var start = cursor;
                    while (cursor < contentEnd && text[cursor] is not ' ' and not '\t') { cursor++; }
                    if (cursor == start || text.AsSpan(start, cursor - start).Contains('\r'))
                    {
                        throw new InvalidDataException("Android fstab entries require five whitespace-delimited fields.");
                    }
                    fields[index] = (start, cursor - start);
                }
                if (text.AsSpan(fields[1].Start, fields[1].Length).SequenceEqual(mountPoint)
                    && (fileSystem.Length == 0 || text.AsSpan(fields[2].Start, fields[2].Length).SequenceEqual(fileSystem)))
                {
                    rows.Add(fields);
                    if (expected is { } limit && rows.Count > limit) { throw new InvalidDataException("Too many matching fstab entries."); }
                }
            }
            position = end < text.Length ? end + 1 : end;
        }
        if (expected is { } expectedCount && rows.Count != expectedCount) { throw new InvalidDataException("Unexpected fstab entry count."); }
        return rows;
    }

    private static void ValidateField(FstabField field)
    {
        if (!Enum.IsDefined(field)) { throw new ArgumentOutOfRangeException(nameof(field)); }
    }

    private static void ValidateToken(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 16384 || value.Any(character => char.IsWhiteSpace(character) || character == '\0') || value.StartsWith('#'))
        {
            throw new InvalidDataException("A fstab field must be a nonempty token without whitespace or NUL.");
        }
    }
}