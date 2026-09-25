using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>
/// The five fields consumed by Android fs_mgr's non-proc fstab reader.
/// <para>Android fs_mgr 非 proc fstab 读取器所消费的五个字段。</para>
/// </summary>
public enum FstabField
{
    /// <summary>The block device or by-name path column. <para>块设备或 by-name 路径列。</para></summary>
    BlockDevice,
    /// <summary>The mount point column used to select an entry. <para>用于选中条目的挂载点列。</para></summary>
    MountPoint,
    /// <summary>The filesystem type column. <para>文件系统类型列。</para></summary>
    FileSystem,
    /// <summary>The comma-delimited mount options column. <para>以逗号分隔的挂载选项列。</para></summary>
    MountOptions,
    /// <summary>The fs_mgr flags column. <para>fs_mgr 标志位列。</para></summary>
    FsMgrFlags
}

/// <summary>
/// Field or comma-delimited option editing; publishing is handled by the patch workspace.
/// <para>按字段或以逗号分隔的选项进行编辑；发布工作由补丁工作区负责。</para>
/// </summary>
public enum FstabOperation
{
    /// <summary>Overwrites the whole selected field with <c>value</c>. <para>用 <c>value</c> 覆盖整个选中字段。</para></summary>
    SetValue,
    /// <summary>Adds or updates one comma-delimited option inside the selected field.
    /// <para>在选中字段内新增或更新一个以逗号分隔的选项。</para></summary>
    SetFlag,
    /// <summary>Removes one comma-delimited option from the selected field.
    /// <para>从选中字段中移除一个以逗号分隔的选项。</para></summary>
    DeleteFlag,
    /// <summary>Deletes the whole matching entry line. <para>删除整行匹配的条目。</para></summary>
    DeleteEntry
}

/// <summary>
/// Bounded lexical editing of Android's five-column fstab, preserving unrelated source text.
/// <para>对 Android 五列 fstab 进行有界的词法编辑，保留无关的源代码文本。</para>
/// </summary>
public static class FstabPatch
{
    /// <summary>
    /// Appends a missing mount/filesystem pair, preserves identical entries, and rejects conflicting pairs.
    /// <para>追加缺失的挂载点与文件系统组合；完全相同的条目保持不变；存在冲突的组合则报错。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// FstabPatch.Add(fstab, "/dev/block/by-name/vendor", "/vendor", "ext4", "ro", "wait", token);
    /// </code>
    /// </example>
    /// <param name="text">fstab source bounded by <see cref="TextPatch.MaxCharacters"/> and free of NUL; blank and <c>#</c> comment
    /// lines are ignored, and every other entry line must hold five whitespace-delimited fields.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 且不含 NUL 的 fstab 源文本；空行与以 <c>#</c> 开头的注释行会被忽略，
    /// 其余条目行必须包含五个由空白分隔的字段。</para></param>
    /// <param name="blockDevice">First-column token of the entry to add. <para>要添加条目第一列的记号。</para></param>
    /// <param name="mountPoint">Second-column token identifying the entry; must be nonempty.
    /// <para>用于标识条目的第二列记号，必须非空。</para></param>
    /// <param name="fileSystem">Third-column token that also takes part in the selection key.
    /// <para>第三列记号，同时参与选择键的匹配。</para></param>
    /// <param name="mountOptions">Fourth-column token; flag editing on it uses <see cref="Apply"/>.
    /// <para>第四列记号；针对它的选项增删使用 <see cref="Apply"/>。</para></param>
    /// <param name="fsMgrFlags">Fifth-column token. <para>第五列记号。</para></param>
    /// <param name="token">Checked while scanning lines and appended output. <para>在扫描行与追加输出时都会检查。</para></param>
    /// <returns>The input text with match count <c>1</c> and <c>false</c> when an identical pair already exists; otherwise the
    /// appended single-space-separated line, match count <c>0</c> and <c>true</c>.
    /// <para>已存在完全相同的组合时返回原文本、匹配数量 <c>1</c> 与 <c>false</c>；否则返回追加后的单空格分隔行、
    /// 匹配数量 <c>0</c> 与 <c>true</c>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">A token is empty, longer than 16384 characters or contains whitespace, NUL or a
    /// leading <c>#</c>; more than one entry shares the mount point and filesystem pair; an existing entry holds different field
    /// values; the source is malformed; or the appended document does not select exactly one entry.
    /// <para>某个记号为空、长度超过 16384 个字符，或包含空白、NUL、以 <c>#</c> 开头；多条条目共用同一挂载点与文件系统组合；
    /// 已存在的条目字段值不同；源文本格式非法；或追加后的文档无法精确选中一个条目。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
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

    /// <summary>
    /// Reads selected raw fields; repeated mount points require an explicit expected count or filesystem selector.
    /// <para>读取选中字段的原始文本；挂载点重复时必须显式给出期望匹配数量或文件系统选择条件。</para>
    /// </summary>
    /// <param name="text">fstab source bounded by <see cref="TextPatch.MaxCharacters"/> and free of NUL.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 且不含 NUL 的 fstab 源文本。</para></param>
    /// <param name="mountPoint">Second-column token identifying the entries to read. <para>用于标识待读条目的第二列记号。</para></param>
    /// <param name="fileSystem">Third-column token narrowing the selection; empty matches every filesystem.
    /// <para>缩小选择范围的第三列记号；为空时匹配所有文件系统。</para></param>
    /// <param name="field">Column to return. <para>要返回的列。</para></param>
    /// <param name="expectedMatches">Exact number of entries that must match, from 0 to 100000.
    /// <para>必须匹配的条目数量，取值 <c>0</c> 到 <c>100000</c>。</para></param>
    /// <param name="token">Checked while scanning lines. <para>在扫描行时都会检查。</para></param>
    /// <returns>The exact source span of <paramref name="field"/> for each matching entry, in document order.
    /// <para>按文档顺序返回每个匹配条目中 <paramref name="field"/> 列的精确源码片段。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="field"/> is not a defined <see cref="FstabField"/>.
    /// <para><paramref name="field"/> 不是已定义的 <see cref="FstabField"/>。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">A selector token is invalid, an entry line lacks the five fields, more than 100000
    /// lines or matching entries are scanned, or the entry count differs from <paramref name="expectedMatches"/>.
    /// <para>选择记号非法、条目行缺少五个字段、扫描的行数或匹配条目超过 100000，或条目数量与
    /// <paramref name="expectedMatches"/> 不符。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
    public static IReadOnlyList<string> Read(string text, string mountPoint, string fileSystem, FstabField field,
        int expectedMatches, CancellationToken token)
    {
        ValidateField(field);
        return Select(text, mountPoint, fileSystem, expectedMatches, token)
            .Select(row => text.Substring(row[(int)field].Start, row[(int)field].Length)).ToArray();
    }

    /// <summary>
    /// Edits selected fields with optional full-field old-value guards.
    /// <para>编辑选中的字段，可选启用针对整字段旧值的守卫。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// FstabPatch.Apply(fstab, "/vendor", "", FstabField.MountOptions,
    ///     FstabOperation.SetFlag, "", "rw", false, "", 1, token);
    /// </code>
    /// </example>
    /// <param name="text">fstab source bounded by <see cref="TextPatch.MaxCharacters"/> and free of NUL.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 且不含 NUL 的 fstab 源文本。</para></param>
    /// <param name="mountPoint">Second-column token identifying the entries to edit. <para>用于标识待编辑条目的第二列记号。</para></param>
    /// <param name="fileSystem">Third-column token narrowing the selection; empty matches every filesystem.
    /// <para>缩小选择范围的第三列记号；为空时匹配所有文件系统。</para></param>
    /// <param name="field">Column being edited. <para>正在编辑的列。</para></param>
    /// <param name="operation">How the field is rewritten. <para>字段的重写方式。</para></param>
    /// <param name="value">Replacement token for <see cref="FstabOperation.SetValue"/>, or the option value appended after
    /// <c>=</c> for <see cref="FstabOperation.SetFlag"/>; an empty option value writes the bare flag. Ignored by
    /// <see cref="FstabOperation.DeleteFlag"/> and <see cref="FstabOperation.DeleteEntry"/>.
    /// <para><see cref="FstabOperation.SetValue"/> 的替换记号，或 <see cref="FstabOperation.SetFlag"/> 时追加在 <c>=</c>
    /// 之后的选项值；选项值为空时只写入裸标志。<see cref="FstabOperation.DeleteFlag"/> 与
    /// <see cref="FstabOperation.DeleteEntry"/> 会忽略它。</para></param>
    /// <param name="flag">Option name matched on the text before its <c>=</c>; required by the two flag operations.
    /// <para>按 <c>=</c> 之前的文本匹配的选项名；两种标志操作都必须提供。</para></param>
    /// <param name="checkOldValue">Compares the current raw field text with <paramref name="expectedOldValue"/> for every matched
    /// entry. <para>对每个匹配条目，将当前字段的原始文本与 <paramref name="expectedOldValue"/> 比较。</para></param>
    /// <param name="expectedOldValue">Raw field text each matched entry must currently hold when
    /// <paramref name="checkOldValue"/> is set. <para>启用 <paramref name="checkOldValue"/> 时每个匹配条目当前必须具有的字段原始文本。</para></param>
    /// <param name="expectedMatches">Exact number of entries that must match, from 0 to 100000.
    /// <para>必须匹配的条目数量，取值 <c>0</c> 到 <c>100000</c>。</para></param>
    /// <param name="token">Checked while rebuilding output for each matched entry. <para>在为每个匹配条目重建输出时都会检查。</para></param>
    /// <returns>
    /// The patched source, the number of matched entries and whether the text differs from <paramref name="text"/>.
    /// <see cref="FstabOperation.DeleteEntry"/> removes whole lines, and a flag edit that empties the option list writes
    /// <c>defaults</c>. <para>补丁后的源代码、匹配的条目数量，以及文本是否与 <paramref name="text"/> 不同。
    /// <see cref="FstabOperation.DeleteEntry"/> 会删除整行；若标志编辑清空了选项列表，则写入 <c>defaults</c>。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="field"/> is not a defined <see cref="FstabField"/>.
    /// <para><paramref name="field"/> 不是已定义的 <see cref="FstabField"/>。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The operation is undefined or a flag edit targets a field other than mount options or
    /// fs_mgr flags, a token is malformed, the flag name contains <c>=</c> or <c>,</c>, the source holds empty flag items or
    /// duplicate target flags, the entry count or format is unexpected, the old-value guard fails, or the output exceeds the
    /// configuration limit. <para>操作未定义，或标志类编辑指向了挂载选项与 fs_mgr 标志之外的列，或某个记号格式非法，
    /// 或标志名包含 <c>=</c> 或 <c>,</c>，或源文本存在空选项或重复的目标标志，或条目数量与格式不符合预期，
    /// 或旧值守卫校验失败，或输出超出配置上限。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
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