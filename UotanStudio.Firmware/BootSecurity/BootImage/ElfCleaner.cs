using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.BootSecurity.BootImage;

/// <summary>
/// Strips debug, note, Android dynamic-linking and symbol-table sections from ELF images held in memory or on disk.
/// <para>从内存或磁盘中的 ELF 镜像剥离调试、note、Android 动态链接与符号表等节。</para>
/// </summary>
public static class ElfCleaner
{
    private static readonly string[] DebugSections =
    [
        ".debug_abbrev",
        ".debug_addr",
        ".debug_str",
        ".debug_line",
        ".debug_frame",
        ".debug_loc",
        ".debug_ranges",
        ".debug_pubnames",
        ".debug_pubtypes",
        ".debug_info",
        ".debug_types",
        ".debug_macro",
        ".debug_sup",
        ".note.gnu.build-id",
        ".note.GNU-stack",
        ".note.*",
        ".gnu_debuglink",
        ".gnu_version",
        ".comment",
        ".ARM.attributes",
        ".stab",
        ".stabstr"
    ];

    private static readonly string[] AndroidSpecificSections =
    [
        ".got",
        ".plt",
        ".rel.dyn",
        ".rela.dyn",
        ".rel.plt",
        ".rela.plt",
        ".init_array",
        ".fini_array",
        ".dynamic",
        ".dynsym",
        ".dynstr"
    ];

    /// <summary>
    /// Selects which section categories a cleaning operation removes; all three options default to enabled.
    /// <para>选择清理操作移除哪些类别的节；三个选项默认全部启用。</para>
    /// </summary>
    public class CleanOptions
    {
        /// <summary>
        /// Enables removal of debug and note pattern sections.
        /// <para>启用调试与 note 模式节的移除。</para>
        /// </summary>
        /// <value>
        /// True to strip sections matching <c>.debug_*</c>, <c>.note.*</c>, <c>.comment</c>, <c>.gnu_debuglink</c>
        /// and <c>.stab</c>; enabled by default.
        /// <para>为 true 时移除匹配 <c>.debug_*</c>、<c>.note.*</c>、<c>.comment</c>、<c>.gnu_debuglink</c> 与 <c>.stab</c> 的节；默认启用。</para>
        /// </value>
        public bool RemoveDebugSections { get; set; } = true;

        /// <summary>
        /// Enables removal of Android dynamic-linking sections.
        /// <para>启用 Android 动态链接节的移除。</para>
        /// </summary>
        /// <value>
        /// True to strip <c>.got</c>, <c>.plt</c>, <c>.rel.dyn</c>, <c>.rela.dyn</c>, <c>.rel.plt</c>, <c>.rela.plt</c>,
        /// <c>.init_array</c>, <c>.fini_array</c>, <c>.dynamic</c>, <c>.dynsym</c> and <c>.dynstr</c>; enabled by default.
        /// <para>为 true 时移除 <c>.got</c>、<c>.plt</c>、<c>.rel.dyn</c>、<c>.rela.dyn</c>、<c>.rel.plt</c>、<c>.rela.plt</c>、
        /// <c>.init_array</c>、<c>.fini_array</c>、<c>.dynamic</c>、<c>.dynsym</c> 与 <c>.dynstr</c>；默认启用。</para>
        /// </value>
        public bool RemoveAndroidSections { get; set; } = true;

        /// <summary>
        /// Enables removal of the symbol and string tables.
        /// <para>启用符号表与字符串表的移除。</para>
        /// </summary>
        /// <value>
        /// True to strip the <c>.symtab</c> and <c>.strtab</c> sections; enabled by default.
        /// <para>为 true 时移除 <c>.symtab</c> 与 <c>.strtab</c> 节；默认启用。</para>
        /// </value>
        public bool CleanSymbolTable { get; set; } = true;
    }

    /// <summary>
    /// Reported outcome of an ELF cleaning operation, including sizes, removed section names and the failure message.
    /// <para>ELF 清理操作的结果报告，包含大小、被移除的节名与失败信息。</para>
    /// </summary>
    public class CleanResult
    {
        /// <summary>
        /// Outcome flag for the cleaning operation.
        /// <para>清理操作的结果标记。</para>
        /// </summary>
        /// <value>
        /// True when the input was a cleanable ELF file, even if no section needed removal; false when cleaning failed.
        /// <para>输入为可清理的 ELF 文件时为 true，即使没有节需要移除；清理失败时为 false。</para>
        /// </value>
        public bool Success { get; set; }

        /// <summary>
        /// Input size measured before cleaning.
        /// <para>清理前测得的输入大小。</para>
        /// </summary>
        /// <value>
        /// Byte length of the input data; 0 when the input was null or could not be read.
        /// <para>输入数据的字节长度；输入为 null 或无法读取时为 0。</para>
        /// </value>
        public long OriginalSize { get; set; }

        /// <summary>
        /// Output size of the operation.
        /// <para>操作的输出大小。</para>
        /// </summary>
        /// <value>
        /// Byte length of the cleaned output; set only when <see cref="Success"/> is true, and equal to
        /// <see cref="OriginalSize"/> when the cleaning removes no section.
        /// <para>清理后输出的字节长度；仅在 <see cref="Success"/> 为 true 时设置，未移除任何节时与 <see cref="OriginalSize"/> 相等。</para>
        /// </value>
        public long CleanedSize { get; set; }

        /// <summary>
        /// Count of cleared section entries.
        /// <para>被清空的节条目数量。</para>
        /// </summary>
        /// <value>
        /// Number of section entries the operation cleared; 0 on failure or when nothing matched.
        /// <para>本次操作清空的节条目数量；失败或没有匹配项时为 0。</para>
        /// </value>
        public int SectionsRemoved { get; set; }

        /// <summary>
        /// Names of the removed sections.
        /// <para>被移除节的名称列表。</para>
        /// </summary>
        /// <value>
        /// Section names in ELF section-table order; never null, and empty when no section is removed or the operation fails.
        /// <para>按 ELF 节表顺序列出的节名；永不为 null，没有节被移除或操作失败时为空。</para>
        /// </value>
        public List<string> RemovedSectionNames { get; set; } = [];

        /// <summary>
        /// Failure description.
        /// <para>失败描述。</para>
        /// </summary>
        /// <value>
        /// Message explaining why cleaning failed; null on success.
        /// <para>解释清理失败原因的消息；成功时为 null。</para>
        /// </value>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Bytes reclaimed by the operation.
        /// <para>本次操作回收的字节数。</para>
        /// </summary>
        /// <value>
        /// <c>Math.Max(0, OriginalSize - CleanedSize)</c>, so a grown output never yields a negative saving.
        /// <para>按 <c>Math.Max(0, OriginalSize - CleanedSize)</c> 计算，输出变大时也不会得到负值。</para>
        /// </value>
        public long SpaceSaved => Math.Max(0, OriginalSize - CleanedSize);
    }

    /// <summary>
    /// One parsed ELF section header together with its cleanability under the default options.
    /// <para>单个已解析的 ELF 节头，以及在默认选项下是否可清理的标记。</para>
    /// </summary>
    public class SectionInfo
    {
        /// <summary>
        /// Location of the section in the ELF section header table.
        /// <para>节在 ELF 节头表中的位置。</para>
        /// </summary>
        /// <value>
        /// Zero-based section header index.
        /// <para>零基节头索引。</para>
        /// </value>
        public int Index { get; set; }

        /// <summary>
        /// Resolved section name.
        /// <para>已解析的节名。</para>
        /// </summary>
        /// <value>
        /// Name read from the string table; empty when the name cannot be resolved.
        /// <para>从字符串表读取的名称；无法解析时为空字符串。</para>
        /// </value>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Section kind as stored in the header.
        /// <para>节头中记录的节类型。</para>
        /// </summary>
        /// <value>
        /// The <c>SHT_*</c> type code read from the section header.
        /// <para>从节头读取的 <c>SHT_*</c> 类型编码。</para>
        /// </value>
        public uint Type { get; set; }

        /// <summary>
        /// Where the section data lives in the file.
        /// <para>节数据在文件中的位置。</para>
        /// </summary>
        /// <value>
        /// File offset of the section contents; 0 for sections without data.
        /// <para>节内容在文件中的偏移；无数据的节为 0。</para>
        /// </value>
        public ulong Offset { get; set; }

        /// <summary>
        /// Declared extent of the section.
        /// <para>节声明的大小范围。</para>
        /// </summary>
        /// <value>
        /// Section size in bytes as stored in the section header.
        /// <para>节头中记录的节大小（字节）。</para>
        /// </value>
        public ulong Size { get; set; }

        /// <summary>
        /// Whether default options would remove the section.
        /// <para>默认选项是否会移除该节。</para>
        /// </summary>
        /// <value>
        /// True when the section name matches <see cref="CleanOptions"/> defaults with every category enabled.
        /// <para>节名匹配全部类别启用的默认 <see cref="CleanOptions"/> 时为 true。</para>
        /// </value>
        public bool IsCleanable { get; set; }
    }

    /// <summary>
    /// Checks whether the data is cleanable (is an ELF file).
    /// <para>检查数据是否可清理（是否为 ELF）。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate file bytes; null, fewer than five bytes, or a missing ELF magic all count as not cleanable.
    /// <para>候选文件字节；null、少于五个字节或缺少 ELF 魔数均视为不可清理。</para>
    /// </param>
    /// <returns>True when the data starts with the ELF magic and its class byte names ELF32 or ELF64.
    /// <para>数据以 ELF 魔数开头且类别字节指明 ELF32 或 ELF64 时为 true。</para></returns>
    public static bool IsCleanable(byte[] data) => ElfTool.IsElf(data);

    /// <summary>
    /// Enumerates the cleanable sections in the data.
    /// <para>枚举数据中可清理的节。</para>
    /// </summary>
    /// <param name="data">
    /// File bytes to parse; null or non-ELF input yields an empty list.
    /// <para>待解析的文件字节；null 或非 ELF 输入返回空列表。</para>
    /// </param>
    /// <returns>Every parsed section with <see cref="SectionInfo.IsCleanable"/> evaluated against the default options;
    /// empty when the input is not a cleanable ELF file.
    /// <para>所有已解析的节，其 <see cref="SectionInfo.IsCleanable"/> 按默认选项评估；输入不是可清理 ELF 文件时为空列表。</para></returns>
    public static List<SectionInfo> GetCleanableSections(byte[] data)
    {
        var result = new List<SectionInfo>();
        if (!IsCleanable(data))
        {
            return result;
        }

        foreach (var section in ElfTool.GetSections(data))
        {
            result.Add(new SectionInfo
            {
                Index = section.Index,
                Name = section.Name,
                Type = section.Type,
                Offset = section.Offset,
                Size = section.Size,
                IsCleanable = CanRemoveSection(section.Name)
            });
        }

        return result;
    }

    /// <summary>
    /// Cleans useless sections from ELF data.
    /// <para>清理 ELF 数据中的无用节。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// var data = File.ReadAllBytes("libfoo.so");
    /// var result = ElfCleaner.Clean(data);
    /// if (result.Success)
    /// {
    ///     File.WriteAllBytes("libfoo.stripped.so", ElfCleaner.CleanData(data));
    /// }
    /// </code>
    /// </example>
    /// <param name="data">
    /// ELF file bytes; null is reported as a failure instead of throwing.
    /// <para>ELF 文件字节；null 会被报告为失败而不会抛出异常。</para>
    /// </param>
    /// <param name="options">
    /// Section categories to remove; null uses <see cref="CleanOptions"/> defaults with every category enabled.
    /// <para>要移除的节类别；null 时使用全部启用的 <see cref="CleanOptions"/> 默认值。</para>
    /// </param>
    /// <returns>Result with <see cref="CleanResult.Success"/> set; any exception raised while stripping is caught
    /// and surfaced through <see cref="CleanResult.ErrorMessage"/>, so this method never throws.
    /// <para>设置了 <see cref="CleanResult.Success"/> 的结果；清理过程中抛出的任何异常都会被捕获并通过
    /// <see cref="CleanResult.ErrorMessage"/> 呈现，因此本方法不会抛出异常。</para></returns>
    public static CleanResult Clean(byte[] data, CleanOptions? options = null)
    {
        options ??= new CleanOptions();

        var result = new CleanResult { OriginalSize = data?.Length ?? 0 };

        if (data == null || !IsCleanable(data))
        {
            result.ErrorMessage = "Not a cleanable ELF file";
            return result;
        }

        try
        {
            var sections = ElfTool.GetSections(data);
            var toRemove = sections
                .Where(s => ShouldRemove(s.Name, options))
                .ToList();

            if (toRemove.Count == 0)
            {
                result.Success = true;
                result.CleanedSize = data.Length;
                return result;
            }

            var cleaned = ElfTool.StripSectionsByName(data, toRemove.Select(s => s.Name));
            result.Success = true;
            result.CleanedSize = cleaned.Length;
            result.SectionsRemoved = toRemove.Count;
            result.RemovedSectionNames = [.. toRemove.Select(s => s.Name)];
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Cleans ELF data and returns the cleaned bytes.
    /// <para>清理 ELF 数据并返回清理后的字节。</para>
    /// </summary>
    /// <param name="data">
    /// ELF file bytes; null or non-ELF input is returned unchanged, with null becoming an empty array.
    /// <para>ELF 文件字节；null 或非 ELF 输入原样返回，其中 null 变为空数组。</para>
    /// </param>
    /// <param name="options">
    /// Section categories to remove; null uses <see cref="CleanOptions"/> defaults with every category enabled.
    /// <para>要移除的节类别；null 时使用全部启用的 <see cref="CleanOptions"/> 默认值。</para>
    /// </param>
    /// <returns>A cleaned copy of the image when at least one section matched, otherwise the original array
    /// reference; unlike <see cref="Clean"/> this method does not catch stripping failures.
    /// <para>至少有一个节匹配时返回镜像的清理副本，否则返回原数组引用；与 <see cref="Clean"/> 不同，本方法不捕获清理失败。</para></returns>
    public static byte[] CleanData(byte[] data, CleanOptions? options = null)
    {
        if (data == null || !IsCleanable(data))
        {
            return data ?? [];
        }

        options ??= new CleanOptions();
        var sections = ElfTool.GetSections(data);
        var toRemove = sections
            .Where(s => ShouldRemove(s.Name, options))
            .Select(s => s.Name)
            .ToList();

        return toRemove.Count == 0 ? data : ElfTool.StripSectionsByName(data, toRemove);
    }

    /// <summary>
    /// Cleans an ELF file and writes it to the output file.
    /// <para>清理 ELF 文件并写入输出文件。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Path of the ELF file to read; read failures are reported through the result instead of throwing.
    /// <para>要读取的 ELF 文件路径；读取失败通过结果报告而不会抛出异常。</para>
    /// </param>
    /// <param name="outputPath">
    /// Path written with the cleaned bytes; an existing file is overwritten.
    /// <para>写入清理后字节的目标路径；已存在的文件会被覆盖。</para>
    /// </param>
    /// <param name="options">
    /// Section categories to remove; null uses <see cref="CleanOptions"/> defaults with every category enabled.
    /// <para>要移除的节类别；null 时使用全部启用的 <see cref="CleanOptions"/> 默认值。</para>
    /// </param>
    /// <returns>Result with sizes and removed section names; on any read or cleaning failure <see cref="CleanResult.Success"/>
    /// is false and nothing is written.
    /// <para>包含大小与被移除节名的结果；任何读取或清理失败时 <see cref="CleanResult.Success"/> 为 false 且不写入任何内容。</para></returns>
    /// <exception cref="IOException">The output file exists but cannot be replaced, or the cleaned bytes cannot be written.
    /// <para>输出文件已存在但无法替换，或清理后的字节无法写入。</para></exception>
    public static CleanResult CleanFile(string inputPath, string outputPath, CleanOptions? options = null)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(inputPath);
        }
        catch (Exception ex)
        {
            return new CleanResult { ErrorMessage = ex.Message };
        }

        var result = Clean(data, options);
        if (result.Success)
        {
            var cleaned = CleanData(data, options);
            File.WriteAllBytes(outputPath, cleaned);
            result.CleanedSize = cleaned.Length;
        }
        return result;
    }

    /// <summary>
    /// Cleans an ELF file in place.
    /// <para>原地清理 ELF 文件。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path of the file to clean; it is replaced only after the cleaned bytes have been written to the temporary
    /// file <c>filePath + ".clean.tmp"</c>, and the original content is kept when cleaning fails.
    /// <para>要清理的文件路径；只有清理后的字节写入临时文件 <c>filePath + ".clean.tmp"</c> 之后才会替换原文件，清理失败时保留原内容。</para>
    /// </param>
    /// <param name="options">
    /// Section categories to remove; null uses <see cref="CleanOptions"/> defaults with every category enabled.
    /// <para>要移除的节类别；null 时使用全部启用的 <see cref="CleanOptions"/> 默认值。</para>
    /// </param>
    /// <returns>Result of the in-place clean; a failed clean deletes the temporary file and leaves the original untouched.
    /// <para>原地清理的结果；清理失败时会删除临时文件并保持原文件不变。</para></returns>
    /// <exception cref="IOException">The temporary file cannot be written or cannot be moved onto the original path.
    /// <para>临时文件无法写入，或无法移动到原路径。</para></exception>
    public static CleanResult CleanFileInPlace(string filePath, CleanOptions? options = null)
    {
        var temp = filePath + ".clean.tmp";
        var result = CleanFile(filePath, temp, options);
        if (result.Success)
        {
            AtomicPublish.Publish(temp, filePath);
        }
        else
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        return result;
    }

    private static bool ShouldRemove(string name, CleanOptions options)
    {
        return (options.RemoveDebugSections && DebugSections.Any(p => NameMatches(name, p)))
            || (options.RemoveAndroidSections && AndroidSpecificSections.Contains(name, StringComparer.Ordinal))
            || (options.CleanSymbolTable && name is ".symtab" or ".strtab");
    }

    private static bool CanRemoveSection(string name) => ShouldRemove(name, new CleanOptions());

    private static bool NameMatches(string name, string pattern)
        => name.Equals(pattern, StringComparison.Ordinal)
           || (pattern.EndsWith("*", StringComparison.Ordinal) && name.StartsWith(pattern[..^1], StringComparison.Ordinal));
}