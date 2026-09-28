using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace UotanStudio.Firmware.Executable.Elf;

/// <summary>
/// Reads ELF32/ELF64 metadata in either byte order using bounded, seekable input.
/// Layouts and extended numbering follow the System V gABI ELF header, program-header and section-header definitions.
/// <para>使用有界、可寻址的输入，以任一字节序读取 ELF32/ELF64 元数据。布局与扩展编号遵循 System V gABI
/// 关于 ELF 头、程序头与节头的定义。</para>
/// </summary>
/// <remarks>
/// This metadata reader does not validate executability. Packed firmware may carry load-segment file offsets
/// that are not congruent with virtual addresses modulo alignment; their original values are preserved.
/// <para>此元数据读取器不验证可执行性。紧凑布局固件的加载段文件偏移与虚拟地址可能不满足对齐同余条件；读取时保留原始值。</para>
/// </remarks>
public static class ElfReader
{
    private const uint MaxTableEntries = 131072;
    private const int MaxStringTableBytes = 16 * 1024 * 1024;
    private const int MaxNameBytes = 4096;
    private const int MaxTotalNameBytes = 16 * 1024 * 1024;

    /// <summary>Validates the complete fixed header, without reading tables or relying on a filename.
    /// <para>校验完整的固定头部，不读取表，也不依赖文件名。</para></summary>
    /// <param name="data">Leading file bytes; at least 52 are required, and 64 for ELFCLASS64.
    /// <para>文件起始字节；至少需要 52 字节，ELFCLASS64 需要 64 字节。</para></param>
    /// <param name="header">The parsed header when the return value is <see langword="true"/>; otherwise <see langword="null"/>.
    /// <para>返回值为 <see langword="true"/> 时为解析出的头部；否则为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the magic, class, data encoding, version, sizes and table geometry are
    /// consistent; <see langword="false"/> for any malformed or truncated input without throwing.
    /// <para>当魔数、位宽、数据编码、版本、大小与表几何信息一致时返回 <see langword="true"/>；任何格式错误或
    /// 截断的输入都返回 <see langword="false"/> 且不抛出异常。</para></returns>
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

    /// <summary>Reads a file on a worker thread; cancellation is checked before every metadata read.
    /// <para>在工作线程上读取文件；每次读取元数据前都会检查取消。</para></summary>
    /// <param name="path">
    /// Path opened read-only with shared read access; the handle is closed when the task completes.
    /// <para>以只读共享读方式打开的路径；任务结束时句柄即关闭。</para>
    /// </param>
    /// <param name="cancellationToken">Token checked before each read; a canceled run faults the task with
    /// <see cref="OperationCanceledException"/>.
    /// <para>每次读取前检查的令牌；被取消时任务以 <see cref="OperationCanceledException"/> 失败。</para></param>
    /// <returns>A task producing the validated <see cref="ElfInfo"/> for the file.
    /// <para>产生该文件已校验 <see cref="ElfInfo"/> 的任务。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.
    /// <para><paramref name="path"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The file is not a valid ELF image. Surfaces as a faulted task.
    /// <para>文件不是有效的 ELF 镜像；表现为失败任务。</para></exception>
    public static Task<ElfInfo> ReadAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        return Read(stream, cancellationToken);
    }, cancellationToken);

    /// <summary>Reads metadata from the start of a seekable stream, leaving it open and restoring its position.
    /// <para>从可寻址流的开头读取元数据，不关闭流并恢复其位置。</para></summary>
    /// <param name="stream">Readable, seekable stream positioned anywhere; all table offsets are resolved
    /// from file start and the stream is not owned.
    /// <para>任意位置处的可读、可寻址流；所有表偏移都相对文件开头解析，流所有权不归本方法。</para></param>
    /// <param name="cancellationToken">Token checked before and after every read.
    /// <para>每次读取前后都检查的令牌。</para></param>
    /// <returns>Validated metadata with extended counts resolved; section names are resolved only when the
    /// file carries a string table.
    /// <para>已校验的元数据（扩展计数已解析）；仅当文件带有字符串表时才解析节名。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The stream cannot be read or cannot seek.
    /// <para>流不可读或不可寻址。</para></exception>
    /// <exception cref="InvalidDataException">The header, tables, ranges, alignments or string table are
    /// malformed, truncated, or exceed the bounded entry and name limits.
    /// <para>头部、表、范围、对齐或字符串表格式错误、被截断，或超出条目与名称的有界上限。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
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
                    if (segment.Type == 1 && segment.FileSize > segment.MemorySize)
                    {
                        throw new InvalidDataException("ELF load-segment file size exceeds its memory size.");
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

    /// <summary>
    /// Delegates the file-range bounds check to the shared kernel validator. Range inputs originate as unsigned
    /// gABI fields; values above <see cref="long.MaxValue"/> wrap to negatives and are still rejected, matching
    /// the previous unsigned comparisons exactly.
    /// <para>将文件范围校验委托给共享内核校验器。范围输入源自无符号的 gABI 字段；超过 <see cref="long.MaxValue"/>
    /// 的值回绕为负数后同样被拒绝，与原先的无符号比较结果完全一致。</para>
    /// </summary>
    private static void ValidateRange(ulong offset, ulong size, ulong length)
    {
        Core.FileExtraction.ValidateRange((long)offset, (long)size, (long)length, "ELF data range");
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