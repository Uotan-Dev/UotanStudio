using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// ELF 文件解析与补丁工具。
/// <para>
/// 提供 ELF32 和 ELF64 格式文件的检测、解析和节剥离功能（MagiskBoot ElfTool 的精简移植）。</para>
/// <para>
/// Provides ELF32/ELF64 detection, parsing and section stripping (a simplified port of the MagiskBoot ElfTool).</para>
/// </summary>
public static class ElfTool
{
    private const uint ShtNull = 0;

    private const int Elf32SectionHeaderSize = 40;
    private const int Elf64SectionHeaderSize = 64;

    /// <summary>
    /// ELF 文件类型。
    /// <para>ELF file types.</para>
    /// </summary>
    public enum ElfType
    {
        /// <summary>
        /// 未知类型。
        /// <para>Unknown.</para>
        /// </summary>
        Unknown,

        /// <summary>
        /// ELF32 格式。
        /// <para>ELF32 format.</para>
        /// </summary>
        Elf32,

        /// <summary>
        /// ELF64 格式。
        /// <para>ELF64 format.</para>
        /// </summary>
        Elf64
    }

    /// <summary>
    /// ELF 解析结果。
    /// <para>ELF parsing result.</para>
    /// </summary>
    public class ElfResult
    {
        /// <summary>
        /// 是否为有效的 ELF 文件。
        /// <para>Whether it's a valid ELF file.</para>
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// ELF 文件类型。
        /// <para>The ELF file type.</para>
        /// </summary>
        public ElfType Type { get; set; }

        /// <summary>
        /// ELF 版本。
        /// <para>The ELF version.</para>
        /// </summary>
        public byte Version { get; set; }

        /// <summary>
        /// 机器架构。
        /// <para>The machine architecture.</para>
        /// </summary>
        public ushort Machine { get; set; }

        /// <summary>
        /// 入口地址。
        /// <para>The entry address.</para>
        /// </summary>
        public ulong EntryPoint { get; set; }

        /// <summary>
        /// 程序头部数量。
        /// <para>The number of program headers.</para>
        /// </summary>
        public ushort ProgramHeaderCount { get; set; }

        /// <summary>
        /// 节头部数量。
        /// <para>The number of section headers.</para>
        /// </summary>
        public ushort SectionHeaderCount { get; set; }

        /// <summary>
        /// 错误消息。
        /// <para>Error message if parsing failed.</para>
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// ELF 节信息。
    /// <para>ELF section information.</para>
    /// </summary>
    public class ElfSection
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
    }

    /// <summary>
    /// 快速检测是否为 ELF 文件。
    /// <para>Quick check if the data is an ELF file.</para>
    /// </summary>
    /// <param name="data">文件数据。<para>The file data.</para></param>
    /// <returns>如果是 ELF 文件则为 true。<para>True if ELF file.</para></returns>
    public static bool IsElf(byte[] data) => data != null && data.Length >= 5 && DetectElfType(data) != ElfType.Unknown;

    /// <summary>
    /// 检测 ELF 文件类型。
    /// <para>Detects the ELF file type.</para>
    /// </summary>
    /// <param name="data">文件数据。<para>The file data.</para></param>
    /// <returns>ELF 类型。<para>The ELF type.</para></returns>
    public static ElfType DetectElfType(byte[] data)
    {
        return data == null || data.Length < 5
            ? ElfType.Unknown
            : data[0] == 0x7F && data[1] == 0x45 && data[2] == 0x4C && data[3] == 0x46
            ? data[4] switch
            {
                1 => ElfType.Elf32,
                2 => ElfType.Elf64,
                _ => ElfType.Unknown
            }
            : ElfType.Unknown;
    }

    /// <summary>
    /// 解析 ELF 文件。
    /// <para>Parses the ELF file.</para>
    /// </summary>
    /// <param name="data">ELF 文件数据。<para>The ELF file data.</para></param>
    /// <returns>解析结果。<para>The parsing result.</para></returns>
    public static ElfResult Parse(byte[] data)
    {
        var result = new ElfResult { IsValid = false };

        if (data == null || data.Length < 64)
        {
            result.ErrorMessage = "Data is too small";
            return result;
        }

        var elfType = DetectElfType(data);
        if (elfType == ElfType.Unknown)
        {
            result.ErrorMessage = "Not an ELF file";
            return result;
        }

        result.Type = elfType;

        try
        {
            result.Version = data[6];
            result.Machine = ReadUInt16LE(data, 18);

            if (elfType == ElfType.Elf32)
            {
                result.EntryPoint = ReadUInt32LE(data, 24);
                result.ProgramHeaderCount = ReadUInt16LE(data, 44);
                result.SectionHeaderCount = ReadUInt16LE(data, 46);
            }
            else
            {
                result.EntryPoint = ReadUInt64LE(data, 24);
                result.ProgramHeaderCount = ReadUInt16LE(data, 56);
                result.SectionHeaderCount = ReadUInt16LE(data, 58);
            }

            result.IsValid = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// 提取 ELF 文件大小。
    /// <para>Extracts the ELF file size from the header.</para>
    /// </summary>
    /// <param name="data">ELF 文件数据。<para>The ELF file data.</para></param>
    /// <returns>ELF 文件大小，如果无法解析则返回 0。<para>The ELF file size, or 0 if parsing failed.</para></returns>
    public static ulong GetElfSize(byte[] data)
    {
        var result = Parse(data);
        if (!result.IsValid)
        {
            return 0;
        }

        if (result.Type == ElfType.Elf32)
        {
            var shoff = ReadUInt32LE(data, 32);
            var shentsize = ReadUInt16LE(data, 46);
            return shoff + (ulong)(result.SectionHeaderCount * shentsize);
        }
        else
        {
            var shoff = ReadUInt64LE(data, 40);
            var shentsize = ReadUInt16LE(data, 58);
            return shoff + (ulong)(result.SectionHeaderCount * shentsize);
        }
    }

    /// <summary>
    /// 枚举 ELF 文件的全部节。
    /// <para>Enumerates all sections of an ELF file.</para>
    /// </summary>
    /// <param name="data">ELF 文件数据。<para>The ELF file data.</para></param>
    /// <returns>节列表；数据无效时返回空列表。<para>The section list, or an empty list when the data is invalid.</para></returns>
    public static List<ElfSection> GetSections(byte[] data)
    {
        var sections = new List<ElfSection>();
        if (data == null)
        {
            return sections;
        }

        var elfType = DetectElfType(data);
        if (elfType == ElfType.Unknown)
        {
            return sections;
        }

        try
        {
            var is64 = elfType == ElfType.Elf64;
            var shoff = is64 ? ReadUInt64LE(data, 40) : ReadUInt32LE(data, 32);
            var shentsize = is64 ? ReadUInt16LE(data, 58) : ReadUInt16LE(data, 46);
            var shnum = is64 ? ReadUInt16LE(data, 60) : ReadUInt16LE(data, 48);
            var shstrndx = is64 ? ReadUInt16LE(data, 62) : ReadUInt16LE(data, 50);

            if (shentsize == 0 || shnum == 0 || shstrndx >= shnum)
            {
                return sections;
            }

            var entrySize = is64 ? Elf64SectionHeaderSize : Elf32SectionHeaderSize;
            var shstrtabOffset = ReadSectionOffset(data, shoff, shstrndx, entrySize, is64);

            for (var i = 0; i < shnum; i++)
            {
                var entryOff = shoff + ((ulong)i * (ulong)entrySize);
                if (entryOff + (ulong)entrySize > (ulong)data.Length)
                {
                    break;
                }

                var nameOffset = ReadUInt32LE(data, (int)entryOff);
                var type = ReadUInt32LE(data, (int)entryOff + 4);
                var offset = ReadSectionOffset(data, shoff, (ulong)i, entrySize, is64);
                var size = ReadSectionSize(data, shoff, (ulong)i, entrySize, is64);

                var name = nameOffset == 0 ? string.Empty : ReadString(data, (int)(shstrtabOffset + nameOffset));

                sections.Add(new ElfSection
                {
                    Index = i,
                    Name = name,
                    Type = type,
                    Offset = offset,
                    Size = size
                });
            }
        }
        catch
        {
            sections.Clear();
        }

        return sections;
    }

    /// <summary>
    /// 剥离 ELF 文件中指定名称的节。
    /// <para>
    /// 将指定节的数据清零，将节头部的 sh_size 设为 0，sh_type 设为 SHT_NULL。</para>
    /// <para>
    /// Strips a named section from the ELF binary by zeroing its data, setting sh_size to 0, and sh_type to SHT_NULL.</para>
    /// </summary>
    /// <param name="data">ELF 文件数据。<para>The ELF file data.</para></param>
    /// <param name="sectionName">要剥离的节名称。<para>The name of the section to strip.</para></param>
    /// <returns>修改后的 ELF 文件数据；如果未找到指定节，则返回原始数据的副本。
    /// <para>The modified ELF data; if the section is not found, returns a copy of the original data.</para></returns>
    public static byte[] StripSection(byte[] data, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(sectionName);

        if (DetectElfType(data) == ElfType.Unknown)
        {
            throw new ElfPatchingException("Invalid ELF data");
        }

        var sections = GetSections(data)
            .Where(s => string.Equals(s.Name, sectionName, StringComparison.Ordinal))
            .ToList();

        return sections.Count == 0 ? (byte[])data.Clone() : StripSections(data, sections);
    }

    /// <summary>
    /// 批量剥离 ELF 文件中匹配的节。
    /// <para>Strips the matching sections from an ELF file.</para>
    /// </summary>
    /// <param name="data">ELF 文件数据。<para>The ELF file data.</para></param>
    /// <param name="names">要剥离的节名称集合（可包含通配模式如 .note.*）。<para>The section names to strip (wildcard patterns like .note.* are allowed).</para></param>
    /// <returns>修改后的 ELF 文件数据。<para>The modified ELF data.</para></returns>
    public static byte[] StripSectionsByName(byte[] data, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(names);

        if (DetectElfType(data) == ElfType.Unknown)
        {
            throw new ElfPatchingException("Invalid ELF data");
        }

        var patternList = names.ToList();
        var sections = GetSections(data)
            .Where(s => patternList.Any(p => NameMatches(s.Name, p)))
            .ToList();

        return sections.Count == 0 ? (byte[])data.Clone() : StripSections(data, sections);
    }

    private static byte[] StripSections(byte[] data, List<ElfSection> sections)
    {
        var result = (byte[])data.Clone();
        var elfType = DetectElfType(result);
        var is64 = elfType == ElfType.Elf64;
        var shoff = is64 ? ReadUInt64LE(result, 40) : ReadUInt32LE(result, 32);
        var shentsize = is64 ? ReadUInt16LE(result, 58) : ReadUInt16LE(result, 46);

        // 先清零节数据区（避免覆盖节名字符串表后影响后续定位）
        foreach (var section in sections)
        {
            if (section.Offset > 0 && section.Offset + section.Size <= (ulong)result.Length)
            {
                Array.Clear(result, (int)section.Offset, (int)section.Size);
            }
        }

        // 再修改节头：sh_type = SHT_NULL, sh_size = 0
        foreach (var section in sections)
        {
            var entryOff = shoff + ((ulong)section.Index * shentsize);
            if (entryOff + shentsize > (ulong)result.Length)
            {
                continue;
            }

            WriteUInt32LE(result, (int)entryOff + 4, ShtNull); // sh_type
            if (is64)
            {
                WriteUInt64LE(result, (int)entryOff + 32, 0); // sh_size
            }
            else
            {
                WriteUInt32LE(result, (int)entryOff + 20, 0); // sh_size
            }
        }

        return result;
    }

    private static ulong ReadSectionOffset(byte[] data, ulong shoff, ulong index, int entrySize, bool is64)
    {
        var entryOff = shoff + (index * (ulong)entrySize);
        return entryOff + (ulong)entrySize > (ulong)data.Length
            ? 0
            : is64 ? ReadUInt64LE(data, (int)entryOff + 24) : ReadUInt32LE(data, (int)entryOff + 16);
    }

    private static ulong ReadSectionSize(byte[] data, ulong shoff, ulong index, int entrySize, bool is64)
    {
        var entryOff = shoff + (index * (ulong)entrySize);
        return entryOff + (ulong)entrySize > (ulong)data.Length
            ? 0
            : is64 ? ReadUInt64LE(data, (int)entryOff + 32) : ReadUInt32LE(data, (int)entryOff + 20);
    }

    private static bool NameMatches(string name, string pattern)
        => name.Equals(pattern, StringComparison.Ordinal)
           || (pattern.EndsWith("*", StringComparison.Ordinal) && name.StartsWith(pattern[..^1], StringComparison.Ordinal));

    private static ushort ReadUInt16LE(byte[] data, int offset) => offset + 2 > data.Length ? (ushort)0 : BitConverter.ToUInt16(data, offset);

    private static uint ReadUInt32LE(byte[] data, int offset) => offset + 4 > data.Length ? 0 : BitConverter.ToUInt32(data, offset);

    private static ulong ReadUInt64LE(byte[] data, int offset) => offset + 8 > data.Length ? 0 : BitConverter.ToUInt64(data, offset);

    private static void WriteUInt32LE(byte[] data, int offset, uint value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 4), value);

    private static void WriteUInt64LE(byte[] data, int offset, ulong value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 8), value);

    private static string ReadString(byte[] data, int offset)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return string.Empty;
        }

        var length = 0;
        while (offset + length < data.Length && data[offset + length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(data, offset, length);
    }
}