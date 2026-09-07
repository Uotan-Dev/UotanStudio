namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

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

    public class CleanOptions
    {
        public bool RemoveDebugSections { get; set; } = true;

        public bool RemoveAndroidSections { get; set; } = true;

        public bool CleanSymbolTable { get; set; } = true;
    }

    public class CleanResult
    {
        public bool Success { get; set; }

        public long OriginalSize { get; set; }

        public long CleanedSize { get; set; }

        public int SectionsRemoved { get; set; }

        public List<string> RemovedSectionNames { get; set; } = [];

        public string? ErrorMessage { get; set; }

        public long SpaceSaved => Math.Max(0, OriginalSize - CleanedSize);
    }

    public class SectionInfo
    {
        public int Index { get; set; }

        public string Name { get; set; } = string.Empty;

        public uint Type { get; set; }

        public ulong Offset { get; set; }

        public ulong Size { get; set; }

        public bool IsCleanable { get; set; }
    }

    /// <summary>
    /// Checks whether the data is cleanable (is an ELF file).
    /// <para>检查数据是否可清理（是否为 ELF）。</para>
    /// </summary>
    public static bool IsCleanable(byte[] data) => ElfTool.IsElf(data);

    /// <summary>
    /// Enumerates the cleanable sections in the data.
    /// <para>枚举数据中可清理的节。</para>
    /// </summary>
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
