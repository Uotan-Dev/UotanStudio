using System.Globalization;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>
/// Outcome of rebasing an fs_config document under a merged-partition root prefix.
/// <para>将 fs_config 文档按合并分区的根前缀改基的结果。</para>
/// </summary>
/// <param name="Text">The rebased document text. <para>改基后的文档文本。</para></param>
/// <param name="PrefixedEntries">Entry paths that received the root prefix. <para>被加上根前缀的条目路径数量。</para></param>
/// <param name="DroppedEntries">Root-default and duplicate-prefix entries that were removed. <para>被移除的根默认条目与前缀重复条目数量。</para></param>
/// <param name="Changed"><see langword="true"/> when the text differs from the input. <para>文本与输入不同时为 <see langword="true"/>。</para></param>
public sealed record FsConfigRebaseResult(string Text, int PrefixedEntries, int DroppedEntries, bool Changed);

/// <summary>
/// Source-preserving edits for the fs_config sidecars consumed by UotanStudio filesystem builders.
/// <para>供 UotanStudio 文件系统构建器使用的 fs_config 附属文件的保源编辑。</para>
/// </summary>
public static class FsConfigPatch
{
    /// <summary>
    /// Rebases a partition-owned fs_config document under <c>&lt;rootPrefix&gt;&lt;partition&gt;</c>: removes the
    /// root <c>/</c> default entry and duplicate prefix defaults, prefixes every other entry path with the
    /// configured root, keeps already-rebased entries verbatim, and appends the <c>0 0 0755</c> default entry.
    /// Rebasing an already-rebased document yields the same text.
    /// <para>将分区自有的 fs_config 文档改基到 <c>&lt;rootPrefix&gt;&lt;partition&gt;</c> 之下：移除根 <c>/</c> 默认条目与前缀
    /// 默认重复条目，为其余条目路径加上配置根前缀，保持已改基条目原样，并追加 <c>0 0 0755</c> 默认条目。
    /// 对已改基文档再次改基得到完全相同的文本。</para>
    /// </summary>
    /// <param name="text">
    /// The existing fs_config source text. <para>现有的 fs_config 源文本。</para>
    /// </param>
    /// <param name="rootPrefix">
    /// Configured relative root such as <c>system/</c>; blank segments, traversal segments and absolute roots are rejected.
    /// <para>配置的相对根前缀（如 <c>system/</c>）；空段、穿越段与绝对根会被拒绝。</para>
    /// </param>
    /// <param name="partition">
    /// Merged partition name (<c>[A-Za-z0-9_-]</c>, at most 64 characters), e.g. <c>my_product</c>.
    /// <para>被合并的分区名（<c>[A-Za-z0-9_-]</c>，至多 64 个字符），如 <c>my_product</c>。</para>
    /// </param>
    /// <param name="token">
    /// Cancels the bounded line scan. <para>取消有界的逐行扫描。</para>
    /// </param>
    /// <returns>
    /// The rebased document with accounting details. <para>改基后的文档及其计数信息。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The root prefix or partition is invalid, the source contains NUL, or a size limit is exceeded.
    /// <para>根前缀或分区无效、源文本包含 NUL，或超出大小上限。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled. <para><paramref name="token"/> 被触发。</para></exception>
    public static FsConfigRebaseResult Rebase(string text, string rootPrefix, string partition, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TextPatch.ValidateSize(text);
        if (text.Contains('\0'))
            throw new InvalidDataException("Filesystem metadata source must not contain NUL.");
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (partition.Length > 64 || partition.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new InvalidDataException("Rebase partition name must use letters, digits, '_' or '-'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPrefix);
        var normalizedRoot = rootPrefix.TrimEnd('/');
        if (normalizedRoot.StartsWith('/') || normalizedRoot.Length > 4096
            || normalizedRoot.Any(character => char.IsWhiteSpace(character) || character == '\\' || character == '#')
            || normalizedRoot.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
        {
            throw new InvalidDataException("Root prefix must be a safe relative directory path.");
        }

        normalizedRoot += "/";
        var root = normalizedRoot + partition;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var output = new System.Text.StringBuilder();
        var prefixed = 0;
        var dropped = 0;
        var position = 0;
        var lineNumber = 0;
        while (position < text.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++lineNumber > 100000)
                throw new InvalidDataException("Too many filesystem metadata lines.");
            var end = text.IndexOf('\n', position);
            var lineEnd = end < 0 ? text.Length : end + 1;
            var contentEnd = end < 0 ? text.Length : end > position && text[end - 1] == '\r' ? end - 1 : end;
            var cursor = position;
            while (cursor < contentEnd && text[cursor] == ' ')
                cursor++;
            if (cursor >= contentEnd || text[cursor] == '#')
            {
                output.Append(text, position, lineEnd - position);
                position = lineEnd;
                continue;
            }

            var pathStart = cursor;
            while (cursor < contentEnd && text[cursor] != ' ')
                cursor++;
            var pathLength = cursor - pathStart;
            var path = text.Substring(pathStart, pathLength).Trim('/');
            if (path.Length == 0 || path == root)
            {
                dropped++;
                position = lineEnd;
                continue;
            }

            if (path.StartsWith(root + "/", StringComparison.Ordinal))
            {
                output.Append(text, position, lineEnd - position);
            }
            else
            {
                output.Append(text, position, pathStart - position).Append(root).Append('/').Append(path)
                    .Append(text, cursor, lineEnd - cursor);
                prefixed++;
            }

            position = lineEnd;
        }

        var separator = output.Length > 0 && output[^1] != '\n' ? newline : "";
        output.Append(separator).Append(root).Append(" 0 0 0755").Append(newline);
        var result = output.ToString();
        TextPatch.ValidateSize(result);
        return new FsConfigRebaseResult(result, prefixed, dropped,
            !string.Equals(text, result, StringComparison.Ordinal));
    }

    /// <summary>
    /// Adds a missing metadata entry; equivalent existing ownership/permissions preserve opaque trailing data.
    /// <para>添加缺失的元数据条目；已存在的等价所有权/权限会保留不透明的尾部数据。</para>
    /// </summary>
    /// <param name="text">
    /// The existing fs_config source text. <para>现有的 fs_config 源文本。</para>
    /// </param>
    /// <param name="path">
    /// Literal image path whose trailing slashes are trimmed for matching. <para>用于匹配的字面镜像路径（尾部斜杠会被裁去）。</para>
    /// </param>
    /// <param name="uid">
    /// Unsigned decimal user identity. <para>无符号十进制用户标识。</para>
    /// </param>
    /// <param name="gid">
    /// Unsigned decimal group identity. <para>无符号十进制组标识。</para>
    /// </param>
    /// <param name="mode">
    /// Decimal or zero-prefixed octal permission mode limited to the low twelve bits. <para>十进制或以 <c>0</c> 为前缀的八进制权限模式，仅限低十二位。</para>
    /// </param>
    /// <param name="token">
    /// Cancels the bounded line scan. <para>取消有界的逐行扫描。</para>
    /// </param>
    /// <returns>
    /// The patch result with the resulting text, the number of matched existing entries and whether the text changed.
    /// <para>补丁结果，包含结果文本、匹配到的现有条目数量以及文本是否发生变化。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The path, size or field syntax is invalid, the path is duplicated, or an existing entry holds different values.
    /// <para>路径、大小或字段语法无效、路径重复，或现有条目的取值不同。</para>
    /// </exception>
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

    /// <summary>
    /// Deletes one physical entry line. A missing entry is unchanged unless an old-value guard requires it.
    /// <para>删除一条物理条目行。除非旧值守卫要求，缺失条目时保持文本不变。</para>
    /// </summary>
    /// <param name="text">
    /// The existing fs_config source text. <para>现有的 fs_config 源文本。</para>
    /// </param>
    /// <param name="path">
    /// Literal image path identifying the entry to remove. <para>标识待删除条目的字面镜像路径。</para>
    /// </param>
    /// <param name="checkOldValue">
    /// When <see langword="true"/>, the entry must exist and match the expected values. <para>为 <see langword="true"/> 时，条目必须存在并与期望值一致。</para>
    /// </param>
    /// <param name="expectedUid">
    /// Unsigned decimal user identity required when <paramref name="checkOldValue"/> is set. <para>当 <paramref name="checkOldValue"/> 置位时要求的无符号十进制用户标识。</para>
    /// </param>
    /// <param name="expectedGid">
    /// Unsigned decimal group identity required when <paramref name="checkOldValue"/> is set. <para>当 <paramref name="checkOldValue"/> 置位时要求的无符号十进制组标识。</para>
    /// </param>
    /// <param name="expectedMode">
    /// Decimal or zero-prefixed octal mode required when <paramref name="checkOldValue"/> is set. <para>当 <paramref name="checkOldValue"/> 置位时要求的十进制或以 <c>0</c> 为前缀的八进制模式。</para>
    /// </param>
    /// <param name="token">
    /// Cancels the bounded line scan. <para>取消有界的逐行扫描。</para>
    /// </param>
    /// <returns>
    /// The patch result with the line removed, the number of matched entries and whether the text changed.
    /// <para>补丁结果，包含删除该行后的文本、匹配条目数量以及文本是否发生变化。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The path or size is invalid, the path is duplicated, or the old-value guard is set and the entry is missing or different.
    /// <para>路径或大小无效、路径重复，或设置了旧值守卫但条目缺失或不一致。</para>
    /// </exception>
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
    /// <summary>
    /// Reads one exact image path. Decimal modes and zero-prefixed octal match the existing builder dialect.
    /// <para>读取一个精确的镜像路径。十进制模式和以 <c>0</c> 为前缀的八进制与现有构建器方言一致。</para>
    /// </summary>
    /// <param name="text">
    /// The fs_config source text. <para>fs_config 源文本。</para>
    /// </param>
    /// <param name="path">
    /// Literal image path to resolve. <para>待解析的字面镜像路径。</para>
    /// </param>
    /// <param name="token">
    /// Cancels the bounded line scan. <para>取消有界的逐行扫描。</para>
    /// </param>
    /// <returns>
    /// The parsed user, group and permission mode for the entry.
    /// <para>该条目解析后的用户、组与权限模式。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The path or size is invalid, the path is missing or duplicated, or a field fails to parse.
    /// <para>路径或大小无效、路径缺失或重复，或某个字段解析失败。</para>
    /// </exception>
    public static FsConfigEntry Read(string text, string path, CancellationToken token)
    {
        var fields = Select(text, path, token);
        return new FsConfigEntry(ParseIdentity(Value(1)), ParseIdentity(Value(2)), ParseMode(Value(3)));
        string Value(int index) => text.Substring(fields[index].Start, fields[index].Length);
    }

    /// <summary>
    /// Replaces uid/gid/permissions only, retaining the path, separators and opaque trailing capability data.
    /// <para>仅替换 uid/gid/权限，保留路径、分隔符和不透明的尾部能力数据。</para>
    /// </summary>
    /// <param name="text">
    /// The existing fs_config source text. <para>现有的 fs_config 源文本。</para>
    /// </param>
    /// <param name="path">
    /// Literal image path identifying the entry to edit. <para>标识待编辑条目的字面镜像路径。</para>
    /// </param>
    /// <param name="uid">
    /// Unsigned decimal user identity to write. <para>要写入的无符号十进制用户标识。</para>
    /// </param>
    /// <param name="gid">
    /// Unsigned decimal group identity to write. <para>要写入的无符号十进制组标识。</para>
    /// </param>
    /// <param name="mode">
    /// Decimal or zero-prefixed octal permission mode to write. <para>要写入的十进制或以 <c>0</c> 为前缀的八进制权限模式。</para>
    /// </param>
    /// <param name="checkOldValue">
    /// When <see langword="true"/>, the current values must equal the expected ones. <para>为 <see langword="true"/> 时，当前值必须与期望值相等。</para>
    /// </param>
    /// <param name="expectedUid">
    /// Unsigned decimal user identity required when <paramref name="checkOldValue"/> is set. <para>当 <paramref name="checkOldValue"/> 置位时要求的无符号十进制用户标识。</para>
    /// </param>
    /// <param name="expectedGid">
    /// Unsigned decimal group identity required when <paramref name="checkOldValue"/> is set. <para>当 <paramref name="checkOldValue"/> 置位时要求的无符号十进制组标识。</para>
    /// </param>
    /// <param name="expectedMode">
    /// Decimal or zero-prefixed octal mode required when <paramref name="checkOldValue"/> is set. <para>当 <paramref name="checkOldValue"/> 置位时要求的十进制或以 <c>0</c> 为前缀的八进制模式。</para>
    /// </param>
    /// <param name="token">
    /// Cancels the bounded line scan. <para>取消有界的逐行扫描。</para>
    /// </param>
    /// <returns>
    /// The patch result with the value fields rewritten, one matched entry and whether the text changed.
    /// <para>补丁结果，包含重写值字段后的文本、一个匹配条目以及文本是否发生变化。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The path, size or field syntax is invalid, the path is missing or duplicated, the result exceeds the size limit, or the old-value guard fails.
    /// <para>路径、大小或字段语法无效、路径缺失或重复、结果超出大小上限，或旧值守卫校验失败。</para>
    /// </exception>
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