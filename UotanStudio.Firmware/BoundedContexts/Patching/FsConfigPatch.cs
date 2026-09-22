using System.Globalization;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Source-preserving edits for the fs_config sidecars consumed by UotanStudio filesystem builders.</summary>
public static class FsConfigPatch
{
    /// <summary>Adds a missing metadata entry; equivalent existing ownership/permissions preserve opaque trailing data.</summary>
    public static TextPatchResult Add(string text, string path, string uid, string gid, string mode, CancellationToken token)
    {
        var desired = new FsConfigEntry(ParseIdentity(uid), ParseIdentity(gid), ParseMode(mode));
        if (Select(text, path, token, true).Length != 0)
        {
            if (Read(text, path, token) != desired) { throw new InvalidDataException("An existing metadata entry has different values."); }
            return new TextPatchResult(text, 1, false);
        }
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = text.Length > 0 && !text.EndsWith('\n') ? newline : "";
        var imagePath = path.Trim('/');
        if (imagePath.Length == 0) { imagePath = "/"; }
        var result = text + separator + imagePath + " " + desired.Uid.ToString(CultureInfo.InvariantCulture) + " "
            + desired.Gid.ToString(CultureInfo.InvariantCulture) + " " + desired.Mode.ToString(CultureInfo.InvariantCulture) + " 0" + newline;
        Read(result, path, token);
        return new TextPatchResult(result, 0, true);
    }

    /// <summary>Deletes one physical entry line. A missing entry is unchanged unless an old-value guard requires it.</summary>
    public static TextPatchResult Delete(string text, string path, bool checkOldValue, string expectedUid,
        string expectedGid, string expectedMode, CancellationToken token)
    {
        var fields = Select(text, path, token, true);
        if (fields.Length == 0)
        {
            if (checkOldValue) { throw new InvalidDataException("Expected filesystem metadata entry is missing."); }
            return new TextPatchResult(text, 0, false);
        }
        var current = Read(text, path, token);
        if (checkOldValue && current != new FsConfigEntry(ParseIdentity(expectedUid), ParseIdentity(expectedGid), ParseMode(expectedMode)))
        {
            throw new InvalidDataException("Filesystem metadata does not match the expected old values.");
        }
        var start = fields[0].Start == 0 ? 0 : text.LastIndexOf('\n', fields[0].Start - 1) + 1;
        var end = text.IndexOf('\n', fields[3].Start + fields[3].Length);
        end = end < 0 ? text.Length : end + 1;
        return new TextPatchResult(text[..start] + text[end..], 1, true);
    }
    /// <summary>Reads one exact image path. Decimal modes and zero-prefixed octal match the existing builder dialect.</summary>
    public static FsConfigEntry Read(string text, string path, CancellationToken token)
    {
        var fields = Select(text, path, token);
        return new FsConfigEntry(ParseIdentity(Value(1)), ParseIdentity(Value(2)), ParseMode(Value(3)));
        string Value(int index) => text.Substring(fields[index].Start, fields[index].Length);
    }

    /// <summary>Replaces uid/gid/permissions only, retaining the path, separators and opaque trailing capability data.</summary>
    public static TextPatchResult Edit(string text, string path, string uid, string gid, string mode,
        bool checkOldValue, string expectedUid, string expectedGid, string expectedMode, CancellationToken token)
    {
        var desired = new FsConfigEntry(ParseIdentity(uid), ParseIdentity(gid), ParseMode(mode));
        var current = Read(text, path, token);
        if (checkOldValue && current != new FsConfigEntry(ParseIdentity(expectedUid), ParseIdentity(expectedGid), ParseMode(expectedMode)))
        {
            throw new InvalidDataException("Filesystem metadata does not match the expected old values.");
        }
        if (current == desired) { return new TextPatchResult(text, 1, false); }
        var fields = Select(text, path, token);
        string[] values = ["", desired.Uid.ToString(CultureInfo.InvariantCulture), desired.Gid.ToString(CultureInfo.InvariantCulture),
            desired.Mode.ToString(CultureInfo.InvariantCulture)];
        var output = new System.Text.StringBuilder();
        var position = 0;
        for (var index = 1; index < 4; index++)
        {
            var field = fields[index];
            output.Append(text, position, field.Start - position).Append(values[index]);
            position = field.Start + field.Length;
        }
        output.Append(text, position, text.Length - position);
        var result = output.ToString();
        TextPatch.ValidateSize(result);
        Read(result, path, token);
        return new TextPatchResult(result, 1, true);
    }

    private static uint ParseIdentity(string value)
        => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result : throw new InvalidDataException("UID/GID must be unsigned decimal integers.");

    private static ushort ParseMode(string value)
    {
        if (value.Length is < 1 or > 5 || value.Any(character => character is < '0' or > '9'))
        {
            throw new InvalidDataException("Invalid filesystem permission mode.");
        }
        var octal = value.Length > 1 && value[0] == '0';
        if (octal && value.Any(character => character > '7')) { throw new InvalidDataException("Invalid octal mode."); }
        var result = Convert.ToUInt32(value, octal ? 8 : 10);
        return result <= 0xFFF ? (ushort)result : throw new InvalidDataException("Mode must contain only permission/special bits.");
    }

    private static (int Start, int Length)[] Select(string text, string path, CancellationToken token, bool allowMissing = false)
    {
        token.ThrowIfCancellationRequested();
        TextPatch.ValidateSize(text);
        if (path.Length is < 1 or > 4096 || path.Any(char.IsWhiteSpace) || path.Contains('\0') || path.Contains('\\')
            || path.Trim('/').StartsWith('#') || path.Split('/').Any(part => part is "." or "..") || text.Contains('\0') || text.Contains('\t'))
        {
            throw new InvalidDataException("Use a literal image path and space-delimited UotanStudio fs_config text.");
        }
        var target = path.Trim('/');
        (int Start, int Length)[]? selected = null;
        var position = 0;
        var lines = 0;
        while (position < text.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++lines > 100000) { throw new InvalidDataException("Too many filesystem metadata lines."); }
            var end = text.IndexOf('\n', position);
            if (end < 0) { end = text.Length; }
            var contentEnd = end > position && text[end - 1] == '\r' ? end - 1 : end;
            var cursor = position;
            while (cursor < contentEnd && text[cursor] == ' ') { cursor++; }
            if (cursor < contentEnd && text[cursor] != '#')
            {
                var fields = new (int Start, int Length)[4];
                for (var index = 0; index < fields.Length; index++)
                {
                    while (cursor < contentEnd && text[cursor] == ' ') { cursor++; }
                    var start = cursor;
                    while (cursor < contentEnd && text[cursor] != ' ') { cursor++; }
                    if (cursor == start) { throw new InvalidDataException("Metadata entries require path, uid, gid and mode."); }
                    fields[index] = (start, cursor - start);
                }
                if (text.Substring(fields[0].Start, fields[0].Length).Trim('/') == target)
                {
                    if (selected is not null) { throw new InvalidDataException("Duplicate filesystem metadata paths are ambiguous."); }
                    selected = fields;
                }
            }
            position = end < text.Length ? end + 1 : end;
        }
        return selected ?? (allowMissing ? [] : throw new InvalidDataException("Filesystem metadata path was not found."));
    }
}