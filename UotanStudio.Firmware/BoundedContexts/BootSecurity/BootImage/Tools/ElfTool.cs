using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

public static class ElfTool
{
    private const uint ShtNull = 0;

    private const int Elf32SectionHeaderSize = 40;
    private const int Elf64SectionHeaderSize = 64;

    public enum ElfType
    {
        Unknown,

        Elf32,

        Elf64
    }

    public class ElfResult
    {
        public bool IsValid { get; set; }

        public ElfType Type { get; set; }

        public byte Version { get; set; }

        public ushort Machine { get; set; }

        public ulong EntryPoint { get; set; }

        public ushort ProgramHeaderCount { get; set; }

        public ushort SectionHeaderCount { get; set; }

        public string? ErrorMessage { get; set; }
    }

    public class ElfSection
    {
        public int Index { get; set; }

        public string Name { get; set; } = string.Empty;

        public uint Type { get; set; }

        public ulong Offset { get; set; }

        public ulong Size { get; set; }
    }

    /// <summary>
    /// Quick check if the data is an ELF file.
    /// <para>快速检测是否为 ELF 文件。</para>
    /// </summary>
    public static bool IsElf(byte[] data) => data != null && data.Length >= 5 && DetectElfType(data) != ElfType.Unknown;

    /// <summary>
    /// Detects the ELF file type.
    /// <para>检测 ELF 文件类型。</para>
    /// </summary>
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
    /// Parses the ELF file.
    /// <para>解析 ELF 文件。</para>
    /// </summary>
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
    /// Extracts the ELF file size from the header.
    /// <para>提取 ELF 文件大小。</para>
    /// </summary>
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
    /// Enumerates all sections of an ELF file.
    /// <para>枚举 ELF 文件的全部节。</para>
    /// </summary>
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
    /// Strips a named section from the ELF binary by zeroing its data, setting sh_size to 0, and sh_type to SHT_NULL.
    /// <para>剥离 ELF 文件中指定名称的节。</para>
    /// </summary>
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
    /// Strips the matching sections from an ELF file.
    /// <para>批量剥离 ELF 文件中匹配的节。</para>
    /// </summary>
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


        foreach (var section in sections)
        {
            if (section.Offset > 0 && section.Offset + section.Size <= (ulong)result.Length)
            {
                Array.Clear(result, (int)section.Offset, (int)section.Size);
            }
        }


        foreach (var section in sections)
        {
            var entryOff = shoff + ((ulong)section.Index * shentsize);
            if (entryOff + shentsize > (ulong)result.Length)
            {
                continue;
            }

            WriteUInt32LE(result, (int)entryOff + 4, ShtNull);
            if (is64)
            {
                WriteUInt64LE(result, (int)entryOff + 32, 0);
            }
            else
            {
                WriteUInt32LE(result, (int)entryOff + 20, 0);
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
