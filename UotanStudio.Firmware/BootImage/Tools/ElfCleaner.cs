namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// ELF 文件精简清理工具（MagiskBoot ElfCleaner 的精简移植）。
/// <para>
/// 通过识别并剥离调试符号表等无用节来精简 ELF 内核镜像。</para>
/// <para>
/// A simplified port of the MagiskBoot ElfCleaner: strips debug/symbol sections from ELF kernel images.</para>
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
    /// 清理选项。
    /// <para>Cleanup options.</para>
    /// </summary>
    public class CleanOptions
    {
        /// <summary>
        /// 是否移除调试节。
        /// <para>Whether to remove debug sections.</para>
        /// </summary>
        public bool RemoveDebugSections { get; set; } = true;

        /// <summary>
        /// 是否移除 Android 特定节。
        /// <para>Whether to remove Android-specific sections.</para>
        /// </summary>
        public bool RemoveAndroidSections { get; set; } = true;

        /// <summary>
        /// 是否清理符号表（.symtab / .strtab）。
        /// <para>Whether to clean symbol tables (.symtab / .strtab).</para>
        /// </summary>
        public bool CleanSymbolTable { get; set; } = true;
    }

    /// <summary>
    /// 清理结果。
    /// <para>Cleanup result.</para>
    /// </summary>
    public class CleanResult
    {
        /// <summary>
        /// 是否成功。
        /// <para>Whether the operation was successful.</para>
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 原始文件大小。
        /// <para>The original file size.</para>
        /// </summary>
        public long OriginalSize { get; set; }

        /// <summary>
        /// 清理后文件大小。
        /// <para>The cleaned file size.</para>
        /// </summary>
        public long CleanedSize { get; set; }

        /// <summary>
        /// 移除的节数量。
        /// <para>The number of sections removed.</para>
        /// </summary>
        public int SectionsRemoved { get; set; }

        /// <summary>
        /// 移除的节名称列表。
        /// <para>The list of removed section names.</para>
        /// </summary>
        public List<string> RemovedSectionNames { get; set; } = [];

        /// <summary>
        /// 错误消息。
        /// <para>Error message if failed.</para>
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 获取节省的空间大小。
        /// <para>Gets the space saved.</para>
        /// </summary>
        public long SpaceSaved => Math.Max(0, OriginalSize - CleanedSize);
    }

    /// <summary>
    /// 节信息。
    /// <para>Section information.</para>
    /// </summary>
    public class SectionInfo
    {
        /// <summary>
        /// 节索引。
        /// <para>The section index.</para>
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// 节名称。
        /// <para>The section name.</para>
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 节类型。
        /// <para>The section type.</para>
        /// </summary>
        public uint Type { get; set; }

        /// <summary>
        /// 节在文件中的偏移量。
        /// <para>The section file offset.</para>
        /// </summary>
        public ulong Offset { get; set; }

        /// <summary>
        /// 节大小。
        /// <para>The section size.</para>
        /// </summary>
        public ulong Size { get; set; }

        /// <summary>
        /// 是否可被清理器移除。
        /// <para>Whether the section can be removed by the cleaner.</para>
        /// </summary>
        public bool IsCleanable { get; set; }
    }

    /// <summary>
    /// 检查数据是否可清理（是否为 ELF）。
    /// <para>Checks whether the data is cleanable (is an ELF file).</para>
    /// </summary>
    /// <param name="data">待检查的数据。<para>The data to check.</para></param>
    /// <returns>如果是 ELF 文件则为 true。<para>True if the data is an ELF file.</para></returns>
    public static bool IsCleanable(byte[] data) => ElfTool.IsElf(data);

    /// <summary>
    /// 枚举数据中可清理的节。
    /// <para>Enumerates the cleanable sections in the data.</para>
    /// </summary>
    /// <param name="data">ELF 文件数据。<para>The ELF file data.</para></param>
    /// <returns>节信息列表。<para>The section info list.</para></returns>
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
    /// 清理 ELF 数据中的无用节。
    /// <para>Cleans useless sections from ELF data.</para>
    /// </summary>
    /// <param name="data">ELF 文件数据。<para>The ELF file data.</para></param>
    /// <param name="options">清理选项；传 null 时使用默认选项。<para>The cleanup options, or null to use defaults.</para></param>
    /// <returns>清理结果。<para>The cleanup result.</para></returns>
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
    /// 清理 ELF 数据并返回清理后的字节。
    /// <para>Cleans ELF data and returns the cleaned bytes.</para>
    /// </summary>
    /// <param name="data">ELF 文件数据。<para>The ELF file data.</para></param>
    /// <param name="options">清理选项；传 null 时使用默认选项。<para>The cleanup options, or null to use defaults.</para></param>
    /// <returns>清理后的数据；清理失败或无法清理时返回原始数据。
    /// <para>The cleaned data, or the original data when cleaning is not possible.</para></returns>
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
    /// 清理 ELF 文件并写入输出文件。
    /// <para>Cleans an ELF file and writes it to the output file.</para>
    /// </summary>
    /// <param name="inputPath">输入文件路径。<para>The input file path.</para></param>
    /// <param name="outputPath">输出文件路径。<para>The output file path.</para></param>
    /// <param name="options">清理选项。<para>The cleanup options.</para></param>
    /// <returns>清理结果。<para>The cleanup result.</para></returns>
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
    /// 原地清理 ELF 文件。
    /// <para>Cleans an ELF file in place.</para>
    /// </summary>
    /// <param name="filePath">文件路径。<para>The file path.</para></param>
    /// <param name="options">清理选项。<para>The cleanup options.</para></param>
    /// <returns>清理结果。<para>The cleanup result.</para></returns>
    public static CleanResult CleanFileInPlace(string filePath, CleanOptions? options = null)
    {
        var temp = filePath + ".clean.tmp";
        var result = CleanFile(filePath, temp, options);
        if (result.Success)
        {
            File.Move(temp, filePath, overwrite: true);
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