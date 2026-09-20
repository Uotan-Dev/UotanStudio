namespace UotanStudio.Firmware.BoundedContexts.Executable.Elf;

/// <summary>Validated ELF identification and file-header fields; table counts may use ELF escape values.</summary>
public sealed record ElfHeader(
    bool Is64Bit, bool IsLittleEndian, byte OsAbi, byte AbiVersion, ushort Type, ushort Machine,
    uint Version, ulong EntryPoint, uint Flags, ushort HeaderSize, ulong ProgramHeaderOffset,
    ushort ProgramHeaderEntrySize, uint ProgramHeaderCount, ulong SectionHeaderOffset,
    ushort SectionHeaderEntrySize, uint SectionHeaderCount, uint SectionNameTableIndex);

/// <summary>A program header describing a segment; no segment content is loaded.</summary>
public sealed record ElfSegment(
    int Index, uint Type, uint Flags, ulong Offset, ulong VirtualAddress, ulong PhysicalAddress,
    ulong FileSize, ulong MemorySize, ulong Alignment);

/// <summary>A section header and its bounded string-table name; NOBITS has no file payload.</summary>
public sealed record ElfSectionInfo(
    int Index, uint NameOffset, string Name, uint Type, ulong Flags, ulong Address, ulong Offset,
    ulong Size, uint Link, uint Info, ulong Alignment, ulong EntrySize);

/// <summary>Read-only ELF metadata with resolved extended counts and validated file ranges.</summary>
public sealed record ElfInfo(
    ElfHeader Header, long FileSize, IReadOnlyList<ElfSegment> Segments, IReadOnlyList<ElfSectionInfo> Sections);