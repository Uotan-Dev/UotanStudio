using System.Text;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BootSecurity.BootImage;

/// <summary>
/// Inspects and patches ELF binaries carried inside boot images, treating every input as untrusted.
/// <para>检查并修改引导镜像中的 ELF 二进制文件，所有输入均视为不可信。</para>
/// </summary>
public static class ElfTool
{
    private const uint ShtNull = 0;

    private const int Elf32SectionHeaderSize = 40;
    private const int Elf64SectionHeaderSize = 64;

    /// <summary>
    /// The ELF class reported by header detection.
    /// <para>由头部检测得出的 ELF 类别。</para>
    /// </summary>
    public enum ElfType
    {
        /// <summary>
        /// The magic bytes are absent or the class byte is neither 1 nor 2.
        /// <para>缺少魔数字节，或类别字节既不是 1 也不是 2。</para>
        /// </summary>
        Unknown,

        /// <summary>
        /// A 32-bit ELF image (class byte 1). <para>32 位 ELF 镜像（类别字节为 1）。</para>
        /// </summary>
        Elf32,

        /// <summary>
        /// A 64-bit ELF image (class byte 2). <para>64 位 ELF 镜像（类别字节为 2）。</para>
        /// </summary>
        Elf64
    }

    /// <summary>
    /// Header fields read from an ELF image together with the outcome of the parse.
    /// <para>从 ELF 镜像读取的头部字段以及本次解析的结果。</para>
    /// </summary>
    public class ElfResult
    {
        /// <summary>
        /// Set when the magic matched and the header fields were read without error.
        /// <para>魔数匹配且头部字段读取未出错时为真。</para>
        /// </summary>
        /// <value>
        /// <see langword="true"/> only when the remaining fields can be trusted; <see langword="false"/>
        /// always comes with a populated <see cref="ErrorMessage"/>.
        /// <para>仅当其余字段可信时为 <see langword="true"/>；<see langword="false"/> 一定伴随非空的 <see cref="ErrorMessage"/>。</para>
        /// </value>
        public bool IsValid { get; set; }

        /// <summary>
        /// The detected class; <see cref="ElfType.Unknown"/> when the parse failed.
        /// <para>检测到的类别；解析失败时为 <see cref="ElfType.Unknown"/>。</para>
        /// </summary>
        /// <value>
        /// The class the header offsets were interpreted with. <para>解析头部偏移时所依据的类别。</para>
        /// </value>
        public ElfType Type { get; set; }

        /// <summary>
        /// The identity/version byte at header offset 6, or 0 for a truncated image.
        /// <para>头部偏移 6 处的标识/版本字节；镜像被截断时为 0。</para>
        /// </summary>
        /// <value>
        /// The raw byte, not checked against any ELF version constant.
        /// <para>原始字节，不会按任何 ELF 版本常量校验。</para>
        /// </value>
        public byte Version { get; set; }

        /// <summary>
        /// The little-endian machine field at header offset 18, or 0 for a truncated image.
        /// <para>头部偏移 18 处的小端机器字段；镜像被截断时为 0。</para>
        /// </summary>
        /// <value>
        /// Target architecture code as recorded by the image; 0 also stands for an unreadable field.
        /// <para>镜像记录的目标架构码；字段不可读时同样为 0。</para>
        /// </value>
        public ushort Machine { get; set; }

        /// <summary>
        /// The entry point read from header offset 24 as a 32- or 64-bit little-endian value by class.
        /// <para>按类别从头部偏移 24 以小端 32 位或 64 位读取的入口地址。</para>
        /// </summary>
        /// <value>
        /// Virtual address of the first instruction; 0 when the field lies past the buffer.
        /// <para>首条指令的虚拟地址；该字段超出缓冲区末尾时为 0。</para>
        /// </value>
        public ulong EntryPoint { get; set; }

        /// <summary>
        /// Program header count read from offset 44 for 32-bit and offset 56 for 64-bit images.
        /// <para>程序头数量：32 位取偏移 44，64 位取偏移 56。</para>
        /// </summary>
        /// <value>
        /// Count as recorded in the header; it is not checked against the buffer length.
        /// <para>头部记录的数量；不会与缓冲区长度做校验。</para>
        /// </value>
        public ushort ProgramHeaderCount { get; set; }

        /// <summary>
        /// Section header count read from offset 48 for 32-bit and offset 60 for 64-bit images.
        /// <para>节头数量：32 位取偏移 48，64 位取偏移 60。</para>
        /// </summary>
        /// <value>
        /// Count as recorded in the header; it bounds the enumeration performed by
        /// <see cref="GetSections"/> but is itself unvalidated.
        /// <para>头部记录的数量；它限定 <see cref="GetSections"/> 的枚举范围，但本身未经校验。</para>
        /// </value>
        public ushort SectionHeaderCount { get; set; }

        /// <summary>
        /// Why the parse failed, or <see langword="null"/> when the header was accepted.
        /// <para>解析失败的原因；头部被接受时为 <see langword="null"/>。</para>
        /// </summary>
        /// <value>
        /// A diagnostic string for display: "Data is too small", "Not an ELF file", or the message of an
        /// error raised while reading the fields.
        /// <para>供展示的诊断字符串：“Data is too small”、“Not an ELF file”，或读取字段时抛出的错误信息。</para>
        /// </value>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// One section header decoded from the section header table.
    /// <para>从节头表中解析出的一个节。</para>
    /// </summary>
    public class ElfSection
    {
        /// <summary>
        /// Zero-based position of the entry in the section header table.
        /// <para>该条目在节头表中的零基索引。</para>
        /// </summary>
        /// <value>
        /// The index used to locate the entry's header bytes and to rewrite them when stripping.
        /// <para>用于定位条目头部字节并在剥离时改写这些字节的索引。</para>
        /// </value>
        public int Index { get; set; }

        /// <summary>
        /// UTF-8 name taken from the string table; empty when the name offset is 0.
        /// <para>取自字符串表的 UTF-8 名称；名称偏移为 0 时为空字符串。</para>
        /// </summary>
        /// <value>
        /// Never <see langword="null"/>, and truncated at the first NUL or at the end of the buffer.
        /// <para>永不为 <see langword="null"/>，并在首个 NUL 或缓冲区末尾处截断。</para>
        /// </value>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The little-endian section type field. <para>小端的节类型字段。</para>
        /// </summary>
        /// <value>
        /// Raw type value; 0 denotes a null section, which is the state a stripped entry is left in.
        /// <para>原始类型值；0 表示空节，也是被剥离条目所处的状态。</para>
        /// </value>
        public uint Type { get; set; }

        /// <summary>
        /// File offset of the section data, or 0 when the entry lies outside the image.
        /// <para>节数据的文件偏移；条目超出镜像范围时为 0。</para>
        /// </summary>
        /// <value>
        /// Absolute position within the image buffer; a stripped section keeps its offset while its bytes
        /// become zero.
        /// <para>镜像缓冲区内的绝对位置；被剥离的节保留其偏移，但其字节变为 0。</para>
        /// </value>
        public ulong Offset { get; set; }

        /// <summary>
        /// Section size in bytes, or 0 when the entry lies outside the image.
        /// <para>按字节计算的节大小；条目超出镜像范围时为 0。</para>
        /// </summary>
        /// <value>
        /// Length of the payload at <see cref="Offset"/>; zeroed together with the header entry when the
        /// section is stripped.
        /// <para><see cref="Offset"/> 处数据的长度；节被剥离时会与头部条目一并置 0。</para>
        /// </value>
        public ulong Size { get; set; }
    }

    /// <summary>
    /// Quick check if the data is an ELF file.
    /// <para>快速检测是否为 ELF 文件。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate bytes; a <see langword="null"/> reference and buffers shorter than 5 bytes are rejected.
    /// <para>待检测字节；<see langword="null"/> 引用以及少于 5 字节的缓冲区会被判定为否。</para>
    /// </param>
    /// <returns><see langword="true"/> when the magic and class byte identify a known ELF class.
    /// <para>当魔数与类别字节标识出已知 ELF 类别时返回 <see langword="true"/>。</para></returns>
    public static bool IsElf(byte[] data) => data != null && data.Length >= 5 && DetectElfType(data) != ElfType.Unknown;

    /// <summary>
    /// Detects the ELF file type.
    /// <para>检测 ELF 文件类型。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate bytes read from offset 0; the class is taken from the fifth byte.
    /// <para>从偏移 0 读取的待检测字节；类别取自第 5 个字节。</para>
    /// </param>
    /// <returns>
    /// The detected class, or <see cref="ElfType.Unknown"/> for <see langword="null"/>, short
    /// buffers, a mismatching magic and class values other than 1 or 2.
    /// <para>检测到的类别；当参数为 <see langword="null"/>、缓冲区过短、魔数不匹配或类别非 1/2 时返回 <see cref="ElfType.Unknown"/>。</para>
    /// </returns>
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

    /// <summary>Parses the ELF file.<para>解析 ELF 文件。</para></summary>
    /// <param name="data">Whole image buffer; at least 64 bytes are required before any field is read.
    /// <para>完整镜像缓冲区；读取任何字段前至少需要 64 字节。</para></param>
    /// <returns>A result whose <see cref="ElfResult.IsValid"/> is set only for a recognised magic. Fields whose offset falls past the end of <paramref name="data"/> stay 0, and failures are reported through <see cref="ElfResult.ErrorMessage"/> instead of an exception.
    /// <para>仅当魔数被识别时 <see cref="ElfResult.IsValid"/> 才为真。偏移超出 <paramref name="data"/> 末尾的字段保持为 0，失败信息通过 <see cref="ElfResult.ErrorMessage"/> 报告而不会抛出异常。</para></returns>
    /// <example>
    /// <code language="csharp">
    /// var header = ElfTool.Parse(imageBytes);
    /// if (header.IsValid)
    /// {
    /// Console.WriteLine($"{header.Type} entry 0x{header.EntryPoint:X}");
    /// }
    /// </code>
    /// </example>
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
                result.SectionHeaderCount = ReadUInt16LE(data, 48);
            }
            else
            {
                result.EntryPoint = ReadUInt64LE(data, 24);
                result.ProgramHeaderCount = ReadUInt16LE(data, 56);
                result.SectionHeaderCount = ReadUInt16LE(data, 60);
            }

            result.IsValid = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>Extracts the ELF file size from the header.<para>提取 ELF 文件大小。</para></summary>
    /// <param name="data">Whole image buffer, handed to <see cref="Parse"/> first.
    /// <para>完整镜像缓冲区，会先交给 <see cref="Parse"/>。</para></param>
    /// <returns>The section header table offset plus the product of the parsed section-header count and the entry-size field read from the same header slot, using the 32-bit offsets for <see cref="ElfType.Elf32"/> and the 64-bit offsets otherwise. Returns 0 when the header does not parse; reads past the end of the buffer contribute 0, so a truncated image yields a lower bound rather than an error.
    /// <para>节头表偏移加上解析出的节头数量与同一头部槽位读到的条目大小字段的乘积；<see cref="ElfType.Elf32"/> 使用 32 位偏移，其余使用 64 位偏移。头部无法解析时返回 0；超出缓冲区末尾的读取按 0 计算，因此被截断的镜像 只会给出偏小的下界而不会报错。</para></returns>
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

    /// <summary>Enumerates all sections of an ELF file.<para>枚举 ELF 文件的全部节。</para></summary>
    /// <param name="data">Whole image buffer; entries are walked with the native section header size (40 for 32-bit, 64 for 64-bit), so the entry size recorded in the header is only checked for being non-zero.
    /// <para>完整镜像缓冲区；条目按原生节头大小（32 位为 40，64 位为 64）遍历，因此头部记录的条目大小只检查是否非 0。</para></param>
    /// <returns>Sections in table order, stopping at the first entry whose header would extend past the buffer. The list is empty for <see langword="null"/> data, an unrecognised magic, a zero entry size or section count, a string-table index that is not below the section count, and whenever reading the table throws. Never <see langword="null"/>.
    /// <para>按节头表顺序返回的节，遇到首个头部条目超出缓冲区末尾时停止。参数为 <see langword="null"/>、魔数无法识别、 条目大小或节数量为 0、字符串表索引不小于节数量，以及读取节头表抛出异常时，返回空列表。 返回值永不为 <see langword="null"/>。</para></returns>
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

    /// <summary>Strips a named section from the ELF binary by zeroing its data, setting sh_size to 0, and sh_type to SHT_NULL.
    /// <para>剥离 ELF 文件中指定名称的节。</para></summary>
    /// <param name="data">Untrusted image buffer; it is never modified in place.
    /// <para>不可信的镜像缓冲区；不会就地修改原数组。</para></param>
    /// <param name="sectionName">Section name compared by ordinal rule (case-sensitive, culture-insensitive); every entry with that exact name is stripped.
    /// <para>按序数码点规则（区分大小写、与文化无关）比较的节名；所有同名条目都会被剥离。</para></param>
    /// <returns>A modified copy in which each matching section's bytes are zeroed only when the whole range lies inside the image, and whose header entry has its type field set to 0 and its size field set to 0 at the 32- or 64-bit offset for the detected class. Entries outside the buffer keep their header bytes. An unrecognised name returns a byte-identical copy.
    /// <para>返回修改后的副本：仅当匹配节的完整范围位于镜像内时才清零其字节，并在其头部条目中把类型字段置 0， 按检测到的类别在 32 位或 64 位偏移处把大小字段置 0。超出缓冲区范围的条目保留其头部字节。名称未匹配时返回逐字节相同的副本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> or <paramref name="sectionName"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 或 <paramref name="sectionName"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ElfPatchingException">The buffer does not start with a recognised ELF 32/64 magic.
    /// <para>缓冲区开头不是可识别的 ELF 32/64 魔数。</para></exception>
    /// <example>
    /// <code language="csharp">
    /// byte[] stripped = ElfTool.StripSection(bootKernel, ".debug_info");
    /// </code>
    /// </example>
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

    /// <summary>Strips the matching sections from an ELF file.<para>批量剥离 ELF 文件中匹配的节。</para></summary>
    /// <param name="data">Untrusted image buffer; it is never modified in place.
    /// <para>不可信的镜像缓冲区；不会就地修改原数组。</para></param>
    /// <param name="names">Patterns compared by ordinal rule (case-sensitive, culture-insensitive): an exact name, or a pattern ending in <c>*</c> that matches names sharing the preceding prefix. The sequence is enumerated once before matching.
    /// <para>按序数码点规则（区分大小写、与文化无关）比较的模式：完整名称，或以 <c>*</c> 结尾、按去掉 <c>*</c> 后的前缀匹配的名称。该序列在匹配前只枚举一次。</para></param>
    /// <returns>A modified copy applying the same data zeroing and section header rewriting as <see cref="StripSection(byte[], string)"/>; a byte-identical copy when no pattern matches.
    /// <para>返回修改后的副本，其数据清零与节头改写规则与 <see cref="StripSection(byte[], string)"/> 一致； 无模式匹配时返回逐字节相同的副本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> or <paramref name="names"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 或 <paramref name="names"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ElfPatchingException">The buffer does not start with a recognised ELF 32/64 magic.
    /// <para>缓冲区开头不是可识别的 ELF 32/64 魔数。</para></exception>
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

    internal static byte[] StripSections(byte[] data, List<ElfSection> sections)
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

    private static ushort ReadUInt16LE(byte[] data, int offset) => offset + 2 > data.Length ? (ushort)0 : data.ReadUInt16LE(offset);

    private static uint ReadUInt32LE(byte[] data, int offset) => offset + 4 > data.Length ? 0 : data.ReadUInt32LE(offset);

    private static ulong ReadUInt64LE(byte[] data, int offset) => offset + 8 > data.Length ? 0 : data.ReadUInt64LE(offset);

    private static void WriteUInt32LE(byte[] data, int offset, uint value) => ByteWriter.WriteUInt32LE(data, offset, value);

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