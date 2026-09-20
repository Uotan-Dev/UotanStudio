using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.Executable.Elf;

/// <summary>
/// Reads ELF32/ELF64 metadata in either byte order using bounded, seekable input.
/// Layouts and extended numbering follow the System V gABI ELF header, program-header and section-header definitions.
/// </summary>
public static class ElfReader
{
    // Metadata limits bound both I/O and the objects retained by the information panel.
    private const uint MaxTableEntries = 131072;
    private const int MaxStringTableBytes = 16 * 1024 * 1024;
    private const int MaxNameBytes = 4096;
    private const int MaxTotalNameBytes = 16 * 1024 * 1024;

    /// <summary>Validates the complete fixed header, without reading tables or relying on a filename.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ElfHeader? header)
    {
        header = null;
        if (data.Length < 52 || !data[..4].SequenceEqual("\u007fELF"u8)
            || data[4] is not (1 or 2) || data[5] is not (1 or 2) || data[6] != 1)
        {
            return false;
        }

        var is64 = data[4] == 2;
        var little = data[5] == 1;
        var size = is64 ? 64 : 52;
        if (data.Length < size || U32(data, 20, little) != 1)
        {
            return false;
        }

        var tail = is64 ? 52 : 40;
        var value = new ElfHeader(is64, little, data[7], data[8], U16(data, 16, little), U16(data, 18, little),
            U32(data, 20, little), Word(data, 24, is64, little), U32(data, is64 ? 48 : 36, little),
            U16(data, tail, little), Word(data, is64 ? 32 : 28, is64, little), U16(data, tail + 2, little),
            U16(data, tail + 4, little), Word(data, is64 ? 40 : 32, is64, little), U16(data, tail + 6, little),
            U16(data, tail + 8, little), U16(data, tail + 10, little));

        if (value.HeaderSize != size
            || value.ProgramHeaderCount != 0 && (value.ProgramHeaderOffset < (ulong)size || value.ProgramHeaderEntrySize != (is64 ? 56 : 32))
            || value.SectionHeaderOffset != 0 && (value.SectionHeaderOffset < (ulong)size || value.SectionHeaderEntrySize != (is64 ? 64 : 40))
            || value.SectionHeaderOffset == 0 && (value.SectionHeaderCount != 0 || value.SectionNameTableIndex != 0 || value.ProgramHeaderCount == 0xffff)
            || value.SectionNameTableIndex != 0xffff && value.SectionHeaderCount != 0 && value.SectionNameTableIndex >= value.SectionHeaderCount)
        {
            return false;
        }

        header = value;
        return true;
    }

    /// <summary>Reads a file on a worker thread; cancellation is checked before every metadata read.</summary>
    public static Task<ElfInfo> ReadAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        return Read(stream, cancellationToken);
    }, cancellationToken);

    /// <summary>Reads metadata from the start of a seekable stream, leaving it open and restoring its position.</summary>
    public static ElfInfo Read(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("ELF metadata requires a readable, seekable stream.", nameof(stream));
        }

        var originalPosition = stream.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = (ulong)stream.Length;
            Span<byte> buffer = stackalloc byte[64];
            var headerLength = (int)Math.Min(length, 64);
            ReadAt(stream, 0, buffer[..headerLength], length, cancellationToken);
            if (!TryReadHeader(buffer[..headerLength], out var header))
            {
                throw new InvalidDataException("Invalid or truncated ELF header.");
            }

            var sectionCount = header.SectionHeaderCount;
            var segmentCount = header.ProgramHeaderCount;
            var namesIndex = header.SectionNameTableIndex;
            if (header.SectionHeaderOffset != 0)
            {
                ReadAt(stream, header.SectionHeaderOffset, buffer[..header.SectionHeaderEntrySize], length, cancellationToken);
                var zero = ReadSection(buffer, 0, header);
                if (zero.Type != 0)
                {
                    throw new InvalidDataException("ELF section zero must be SHT_NULL.");
                }

                if (sectionCount == 0)
                {
                    if (zero.Size == 0 || zero.Size > MaxTableEntries)
                    {
                        throw new InvalidDataException("Invalid or excessive ELF extended section count.");
                    }

                    sectionCount = (uint)zero.Size;
                }

                if (segmentCount == 0xffff)
                {
                    segmentCount = zero.Info;
                }

                if (namesIndex == 0xffff)
                {
                    namesIndex = zero.Link;
                }
            }

            ValidateTable(header.ProgramHeaderOffset, header.ProgramHeaderEntrySize, segmentCount, length);
            ValidateTable(header.SectionHeaderOffset, header.SectionHeaderEntrySize, sectionCount, length);
            if (namesIndex != 0 && namesIndex >= sectionCount)
            {
                throw new InvalidDataException("ELF section-name table index is out of range.");
            }

            header = header with { ProgramHeaderCount = segmentCount, SectionHeaderCount = sectionCount, SectionNameTableIndex = namesIndex };
            var segments = new List<ElfSegment>((int)segmentCount);
            for (var i = 0; i < segmentCount; i++)
            {
                ReadAt(stream, header.ProgramHeaderOffset + (ulong)i * header.ProgramHeaderEntrySize,
                    buffer[..header.ProgramHeaderEntrySize], length, cancellationToken);
                var segment = ReadSegment(buffer, i, header);
                if (segment.Type != 0)
                {
                    ValidateRange(segment.Offset, segment.FileSize, length);
                    ValidateAlignment(segment.Alignment);
                    if (segment.Type == 1 && (segment.FileSize > segment.MemorySize
                        || segment.Alignment > 1 && segment.Offset % segment.Alignment != segment.VirtualAddress % segment.Alignment))
                    {
                        throw new InvalidDataException("Invalid ELF load-segment size or alignment.");
                    }
                }

                segments.Add(segment);
            }

            var sections = new List<ElfSectionInfo>((int)sectionCount);
            for (var i = 0; i < sectionCount; i++)
            {
                ReadAt(stream, header.SectionHeaderOffset + (ulong)i * header.SectionHeaderEntrySize,
                    buffer[..header.SectionHeaderEntrySize], length, cancellationToken);
                var section = ReadSection(buffer, i, header);
                // SHT_NULL fields can carry extended counts; SHT_NOBITS occupies memory only.
                if (section.Type != 0)
                {
                    if (section.Type != 8)
                    {
                        ValidateRange(section.Offset, section.Size, length);
                    }

                    ValidateAlignment(section.Alignment);
                    if (section.Link >= sectionCount || section.EntrySize != 0 && section.Size % section.EntrySize != 0)
                    {
                        throw new InvalidDataException("Invalid ELF section link or entry size.");
                    }
                }

                sections.Add(section);
            }

            if (namesIndex != 0)
            {
                var names = sections[(int)namesIndex];
                if (names.Type != 3 || names.Size == 0 || names.Size > MaxStringTableBytes)
                {
                    throw new InvalidDataException("Invalid or excessive ELF section-name string table.");
                }

                var strings = new byte[(int)names.Size];
                ReadAt(stream, names.Offset, strings, length, cancellationToken);
                if (strings[0] != 0 || strings[^1] != 0)
                {
                    throw new InvalidDataException("ELF string table must start and end with NUL.");
                }

                var remainingNameBytes = MaxTotalNameBytes;
                for (var i = 0; i < sections.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var nameOffset = sections[i].NameOffset;
                    if (nameOffset >= strings.Length)
                    {
                        throw new InvalidDataException("ELF section name is outside the string table.");
                    }

                    var bytes = strings.AsSpan((int)nameOffset, Math.Min(MaxNameBytes + 1, strings.Length - (int)nameOffset));
                    var end = bytes.IndexOf((byte)0);
                    if (end < 0 || end > remainingNameBytes)
                    {
                        throw new InvalidDataException("ELF section names exceed the metadata limit.");
                    }

                    remainingNameBytes -= end;
                    sections[i] = sections[i] with { Name = Encoding.UTF8.GetString(bytes[..end]) };
                }
            }

            return new(header, (long)length, segments.AsReadOnly(), sections.AsReadOnly());
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static ElfSegment ReadSegment(ReadOnlySpan<byte> data, int index, ElfHeader header)
    {
        var little = header.IsLittleEndian;
        return header.Is64Bit
            ? new(index, U32(data, 0, little), U32(data, 4, little), U64(data, 8, little), U64(data, 16, little),
                U64(data, 24, little), U64(data, 32, little), U64(data, 40, little), U64(data, 48, little))
            : new(index, U32(data, 0, little), U32(data, 24, little), U32(data, 4, little), U32(data, 8, little),
                U32(data, 12, little), U32(data, 16, little), U32(data, 20, little), U32(data, 28, little));
    }

    private static ElfSectionInfo ReadSection(ReadOnlySpan<byte> data, int index, ElfHeader header)
    {
        var little = header.IsLittleEndian;
        return header.Is64Bit
            ? new(index, U32(data, 0, little), string.Empty, U32(data, 4, little), U64(data, 8, little), U64(data, 16, little),
                U64(data, 24, little), U64(data, 32, little), U32(data, 40, little), U32(data, 44, little), U64(data, 48, little), U64(data, 56, little))
            : new(index, U32(data, 0, little), string.Empty, U32(data, 4, little), U32(data, 8, little), U32(data, 12, little),
                U32(data, 16, little), U32(data, 20, little), U32(data, 24, little), U32(data, 28, little), U32(data, 32, little), U32(data, 36, little));
    }

    private static void ValidateTable(ulong offset, ushort entrySize, uint count, ulong length)
    {
        if (count > MaxTableEntries)
        {
            throw new InvalidDataException("ELF table exceeds the metadata entry limit.");
        }

        if (count != 0)
        {
            ValidateRange(offset, (ulong)entrySize * count, length);
        }
    }

    private static void ValidateRange(ulong offset, ulong size, ulong length)
    {
        if (offset > length || size > length - offset)
        {
            throw new InvalidDataException("ELF data range is outside the file.");
        }
    }

    private static void ValidateAlignment(ulong alignment)
    {
        if (alignment != 0 && (alignment & (alignment - 1)) != 0)
        {
            throw new InvalidDataException("ELF alignment is not a power of two.");
        }
    }

    private static void ReadAt(Stream stream, ulong offset, Span<byte> buffer, ulong length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRange(offset, (ulong)buffer.Length, length);
        stream.Position = (long)offset;
        stream.ReadExactly(buffer);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static uint U32(ReadOnlySpan<byte> data, int offset, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private static ulong U64(ReadOnlySpan<byte> data, int offset, bool little) => little
        ? BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);

    private static ulong Word(ReadOnlySpan<byte> data, int offset, bool is64, bool little) => is64
        ? U64(data, offset, little) : U32(data, offset, little);
}