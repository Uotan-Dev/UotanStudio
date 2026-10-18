namespace UotanStudio.Firmware.Executable.Elf;

/// <summary>Validated ELF identification and file-header fields; table counts may use ELF escape values.
/// <para>已校验的 ELF 标识与文件头字段；表计数可能采用 ELF 转义值。</para></summary>
/// <param name="Is64Bit">True for ELFCLASS64, false for ELFCLASS32.
/// <para>ELFCLASS64 为 true，ELFCLASS32 为 false。</para></param>
/// <param name="IsLittleEndian">True when multi-byte header fields are little-endian (ELFDATA2LSB).
/// <para>多字节头字段为小端序（ELFDATA2LSB）时为 true。</para></param>
/// <param name="OsAbi">Operating-system ABI byte from the identification area.
/// <para>标识区中的操作系统 ABI 字节。</para></param>
/// <param name="AbiVersion">ABI version paired with <see cref="OsAbi"/>.
/// <para>与 <see cref="OsAbi"/> 配对的 ABI 版本。</para></param>
/// <param name="Type">Object file type (ET_* values).<para>目标文件类型（ET_* 值）。</para></param>
/// <param name="Machine">Target instruction-set architecture (EM_* values).
/// <para>目标指令集架构（EM_* 值）。</para></param>
/// <param name="Version">Object format version; validation accepts only 1.
/// <para>对象格式版本；校验仅接受 1。</para></param>
/// <param name="EntryPoint">Virtual address of the entry point; 0 when the file declares none.
/// <para>入口点的虚拟地址；未声明入口点时为 0。</para></param>
/// <param name="Flags">Processor-specific attribute flags.<para>与处理器相关的属性标志。</para></param>
/// <param name="HeaderSize">Byte size of the file header; validated to be 52 or 64 to match the class.
/// <para>文件头字节大小；校验要求与位宽匹配，为 52 或 64。</para></param>
/// <param name="ProgramHeaderOffset">File offset of the program header table; 0 when there are no segments.
/// <para>程序头表在文件中的偏移；无段时为 0。</para></param>
/// <param name="ProgramHeaderEntrySize">Byte size of one program header entry; validated to be 32 or 56.
/// <para>单个程序头条目的字节大小；校验要求为 32 或 56。</para></param>
/// <param name="ProgramHeaderCount">Number of program headers; the raw 0xffff value signals the extended count stored in section zero.
/// <para>程序头数量；原始值 0xffff 表示扩展计数存于 0 号节。</para></param>
/// <param name="SectionHeaderOffset">File offset of the section header table; 0 when the file carries no sections.
/// <para>节头表在文件中的偏移；文件无节时为 0。</para></param>
/// <param name="SectionHeaderEntrySize">Byte size of one section header entry; validated to be 40 or 64.
/// <para>单个节头条目的字节大小；校验要求为 40 或 64。</para></param>
/// <param name="SectionHeaderCount">Number of section headers; the raw 0 value signals the extended count stored in section zero.
/// <para>节头数量；原始值 0 表示扩展计数存于 0 号节。</para></param>
/// <param name="SectionNameTableIndex">Index of the section-name string table; the raw 0xffff value signals the extended index stored in section zero.
/// <para>节名字符串表的索引；原始值 0xffff 表示扩展索引存于 0 号节。</para></param>
public sealed record ElfHeader(
    bool Is64Bit, bool IsLittleEndian, byte OsAbi, byte AbiVersion, ushort Type, ushort Machine,
    uint Version, ulong EntryPoint, uint Flags, ushort HeaderSize, ulong ProgramHeaderOffset,
    ushort ProgramHeaderEntrySize, uint ProgramHeaderCount, ulong SectionHeaderOffset,
    ushort SectionHeaderEntrySize, uint SectionHeaderCount, uint SectionNameTableIndex);

/// <summary>A program header describing a segment; no segment content is loaded.
/// <para>描述一个段的程序头；不会加载段内容。</para></summary>
/// <param name="Index">Zero-based position in the program header table. <para>在程序头表中从零开始的序号。</para></param>
/// <param name="Type">Segment type (PT_* values); PT_NULL entries skip range and alignment validation. <para>段类型（PT_* 值）；PT_NULL 条目跳过范围与对齐校验。</para></param>
/// <param name="Flags">Segment permission flags (PF_* values). <para>段权限标志（PF_* 值）。</para></param>
/// <param name="Offset">File offset of the segment contents, validated against the file length. <para>段内容在文件中的偏移，已按文件长度校验。</para></param>
/// <param name="VirtualAddress">Virtual address at which the segment is mapped. <para>段映射到的虚拟地址。</para></param>
/// <param name="PhysicalAddress">Physical address recorded by the link editor. <para>链接编辑器记录的物理地址。</para></param>
/// <param name="FileSize">Byte count mapped from the file, validated to fit within it. <para>从文件映射的字节数，已校验位于文件内。</para></param>
/// <param name="MemorySize">Byte count occupied in memory; for load segments it is at least <see cref="FileSize"/>. <para>在内存中占用的字节数；加载段的值不小于 <see cref="FileSize"/>。</para></param>
/// <param name="Alignment">Required alignment; 0 or a power of two, and load segments must align offset to address. <para>要求的对齐值；为 0 或 2 的幂，加载段还要求偏移与地址同余。</para></param>
public sealed record ElfSegment(
    int Index, uint Type, uint Flags, ulong Offset, ulong VirtualAddress, ulong PhysicalAddress,
    ulong FileSize, ulong MemorySize, ulong Alignment);

/// <summary>A section header and its bounded string-table name; NOBITS has no file payload.
/// <para>节头及其受限的字符串表名称；NOBITS 节没有文件内容。</para></summary>
/// <param name="Index">Zero-based position in the section header table.
/// <para>在节头表中从零开始的序号。</para></param>
/// <param name="NameOffset">Byte offset of the name inside the string table.
/// <para>名称在字符串表中的字节偏移。</para></param>
/// <param name="Name">Decoded UTF-8 name; empty while the file has no string table or before resolution.
/// <para>解码后的 UTF-8 名称；文件无字符串表或尚未解析时为空字符串。</para></param>
/// <param name="Type">Section type (SHT_* values).<para>节类型（SHT_* 值）。</para></param>
/// <param name="Flags">Section attribute flags (SHF_* values).<para>节属性标志（SHF_* 值）。</para></param>
/// <param name="Address">Memory address of the section; 0 for sections not part of memory image.
/// <para>节的内存地址；不属于内存镜像的节为 0。</para></param>
/// <param name="Offset">File offset of the section contents, validated against the file length.
/// <para>节内容在文件中的偏移，已按文件长度校验。</para></param>
/// <param name="Size">Byte size of the section, validated to fit within the file.
/// <para>节的字节大小，已校验位于文件内。</para></param>
/// <param name="Link">Link to another section (SHT_* dependent meaning); must be below the section count.
/// <para>对另一节的链接（含义取决于节类型）；必须小于节数。</para></param>
/// <param name="Info">Additional section information (SHT_* dependent meaning).
/// <para>附加节信息（含义取决于节类型）。</para></param>
/// <param name="Alignment">Required alignment; 0 or a power of two.
/// <para>要求的对齐值；为 0 或 2 的幂。</para></param>
/// <param name="EntrySize">Byte size of one fixed-size entry; <see cref="Size"/> must be a multiple of it, or 0 when the section has no entries.
/// <para>单个定长条目的字节大小；<see cref="Size"/> 必须是其整数倍，无条目时为 0。</para></param>
public sealed record ElfSectionInfo(
    int Index, uint NameOffset, string Name, uint Type, ulong Flags, ulong Address, ulong Offset,
    ulong Size, uint Link, uint Info, ulong Alignment, ulong EntrySize);

/// <summary>Read-only ELF metadata with resolved extended counts and validated file ranges.
/// <para>只读 ELF 元数据，扩展计数已解析且文件范围已校验。</para></summary>
/// <param name="Header">The file header with escape values replaced by resolved counts and name-table index. <para>已用解析后的计数与名字表索引替换转义值的文件头。</para></param>
/// <param name="FileSize">Total byte length of the source file at read time. <para>读取时源文件的总字节长度。</para></param>
/// <param name="Segments">Program header entries in table order; may be empty. <para>按表顺序排列的程序头条目；可能为空。</para></param>
/// <param name="Sections">Section header entries in table order; section zero is always present and empty before name resolution when a table exists. <para>按表顺序排列的节头条目；存在节表时 0 号节始终在列，名字解析前为空。</para></param>
public sealed record ElfInfo(
    ElfHeader Header, long FileSize, IReadOnlyList<ElfSegment> Segments, IReadOnlyList<ElfSectionInfo> Sections);