using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// One named section of a synthesized ELF image together with the layout the builder assigned to it.
/// <para>合成 ELF 镜像中一个具名节，以及构建器为它分配的布局信息。</para>
/// </summary>
/// <param name="Index">Zero-based position in the section header table. <para>节头表中的零基位置。</para></param>
/// <param name="Name">Name resolved through the synthesized string table. <para>经合成字符串表解析出的名称。</para></param>
/// <param name="Type">The <c>sh_type</c> value the builder stored. <para>构建器写入的 <c>sh_type</c> 值。</para></param>
/// <param name="Offset">File offset of the section payload. <para>节数据的文件偏移。</para></param>
/// <param name="Size">Declared payload length in bytes. <para>声明的数据长度（字节）。</para></param>
internal sealed record SyntheticElfSection(int Index, string Name, uint Type, ulong Offset, ulong Size);

/// <summary>
/// A synthesized ELF image plus the layout facts a test needs, so assertions never re-derive them through the code
/// under test.
/// <para>合成的 ELF 镜像加上测试所需的布局事实，使断言无需经由被测代码重新推导这些事实。</para>
/// </summary>
/// <param name="Image">Complete little-endian ELF file bytes. <para>完整的小端序 ELF 文件字节。</para></param>
/// <param name="Sections">Section layout in header-table order, including the leading null entry.
/// <para>按节头表顺序排列的节布局，含起始的空条目。</para></param>
/// <param name="SizeFromHeader">Section header table offset plus its full extent, the value a header-driven size
/// computation must reproduce. <para>节头表偏移加上其完整范围，即按头部计算的镜像大小应复现的值。</para></param>
internal sealed record SyntheticElfImage(byte[] Image, IReadOnlyList<SyntheticElfSection> Sections, ulong SizeFromHeader)
{
    internal SyntheticElfSection Section(string name)
        => Sections.Single(section => string.Equals(section.Name, name, StringComparison.Ordinal));

    internal SyntheticElfSection Section(int index) => Sections[index];

    internal int Count => Sections.Count;
}

/// <summary>
/// Builds small, well-formed little-endian ELF images with a real section header table and string table, for tests
/// that need named sections at known offsets without a toolchain or a native object file.
/// <para>构建小而规整的小端序 ELF 镜像，带真实的节头表与字符串表，供需要"具名节位于已知偏移"的测试使用，
/// 无需工具链或原生目标文件。</para>
/// </summary>
internal static class SyntheticElf
{
    private const int SectionAlignment = 16;

    internal static SyntheticElfImage Build(
        bool is64 = true,
        IReadOnlyList<(string Name, uint Type, byte[] Data)>? sections = null,
        ushort machine = 183,
        ulong entryPoint = 0x1000,
        ushort? stringTableIndex = null)
    {
        sections ??= [];
        var entrySize = is64 ? 64 : 40;
        var headerSize = is64 ? 64 : 52;
        var programHeaderSize = is64 ? 56 : 32;

        var names = new List<string> { string.Empty };
        names.AddRange(sections.Select(section => section.Name));
        names.Add(".shstrtab");
        var nameOffsets = new int[names.Count];
        var stringTable = new List<byte> { 0 };
        for (var index = 0; index < names.Count; index++)
        {
            if (names[index].Length == 0)
            {
                continue;
            }

            nameOffsets[index] = stringTable.Count;
            stringTable.AddRange(Encoding.ASCII.GetBytes(names[index]));
            stringTable.Add(0);
        }

        var table = stringTable.ToArray();

        var offsets = new ulong[sections.Count];
        var cursor = Align(headerSize + programHeaderSize);
        for (var index = 0; index < sections.Count; index++)
        {
            offsets[index] = (ulong)cursor;
            cursor = Align(cursor + sections[index].Data.Length);
        }

        var stringTableOffset = (ulong)cursor;
        cursor = Align(cursor + table.Length);
        var sectionTableOffset = (ulong)cursor;
        var sectionCount = sections.Count + 2;
        var image = new byte[sectionTableOffset + (ulong)(sectionCount * entrySize)];

        WriteIdentification(image, is64);
        WriteUInt16(image, 16, 2);
        WriteUInt16(image, 18, machine);
        WriteUInt32(image, 20, 1);
        if (is64)
        {
            WriteUInt64(image, 24, entryPoint);
            WriteUInt64(image, 32, (ulong)headerSize);
            WriteUInt64(image, 40, sectionTableOffset);
            WriteUInt16(image, 52, (ushort)headerSize);
            WriteUInt16(image, 54, (ushort)programHeaderSize);
            WriteUInt16(image, 56, 1);
            WriteUInt16(image, 58, (ushort)entrySize);
            WriteUInt16(image, 60, (ushort)sectionCount);
            WriteUInt16(image, 62, stringTableIndex ?? (ushort)(sectionCount - 1));
        }
        else
        {
            WriteUInt32(image, 24, (uint)entryPoint);
            WriteUInt32(image, 28, (uint)headerSize);
            WriteUInt32(image, 32, (uint)sectionTableOffset);
            WriteUInt16(image, 40, (ushort)headerSize);
            WriteUInt16(image, 42, (ushort)programHeaderSize);
            WriteUInt16(image, 44, 1);
            WriteUInt16(image, 46, (ushort)entrySize);
            WriteUInt16(image, 48, (ushort)sectionCount);
            WriteUInt16(image, 50, stringTableIndex ?? (ushort)(sectionCount - 1));
        }

        WriteProgramHeader(image, headerSize, is64, size: (ulong)image.Length);

        var layouts = new List<SyntheticElfSection> { new(0, string.Empty, 0, 0, 0) };
        for (var index = 0; index < sections.Count; index++)
        {
            sections[index].Data.CopyTo(image, (int)offsets[index]);
            layouts.Add(new SyntheticElfSection(
                index + 1, sections[index].Name, sections[index].Type, offsets[index], (ulong)sections[index].Data.Length));
        }

        table.CopyTo(image, (int)stringTableOffset);
        layouts.Add(new SyntheticElfSection(sectionCount - 1, ".shstrtab", 3, stringTableOffset, (ulong)table.Length));

        WriteSectionHeader(image, sectionTableOffset, 0, entrySize, is64, 0, 0, 0, 0);
        for (var index = 0; index < sections.Count; index++)
        {
            WriteSectionHeader(image, sectionTableOffset, (ulong)(index + 1) * (ulong)entrySize, entrySize, is64,
                (uint)nameOffsets[index + 1], sections[index].Type, offsets[index], (ulong)sections[index].Data.Length);
        }

        WriteSectionHeader(image, sectionTableOffset, (ulong)(sectionCount - 1) * (ulong)entrySize, entrySize, is64,
            (uint)nameOffsets[^1], 3, stringTableOffset, (ulong)table.Length);

        return new SyntheticElfImage(image, layouts, sectionTableOffset + (ulong)(sectionCount * entrySize));
    }

    private static int Align(int value) => (value + SectionAlignment - 1) / SectionAlignment * SectionAlignment;

    private static void WriteIdentification(byte[] image, bool is64)
    {
        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = is64 ? (byte)2 : (byte)1;
        image[5] = 1;
        image[6] = 1;
    }

    private static void WriteProgramHeader(byte[] image, int offset, bool is64, ulong size)
    {
        if (is64)
        {
            WriteUInt32(image, offset, 1);
            WriteUInt32(image, offset + 4, 0x5);
            WriteUInt64(image, offset + 8, 0);
            WriteUInt64(image, offset + 16, 0x1000);
            WriteUInt64(image, offset + 24, 0x1000);
            WriteUInt64(image, offset + 32, size);
            WriteUInt64(image, offset + 40, size);
            WriteUInt64(image, offset + 48, 0x1000);
            return;
        }

        WriteUInt32(image, offset, 1);
        WriteUInt32(image, offset + 4, 0);
        WriteUInt32(image, offset + 8, 0x1000);
        WriteUInt32(image, offset + 12, 0x1000);
        WriteUInt32(image, offset + 16, (uint)size);
        WriteUInt32(image, offset + 20, (uint)size);
        WriteUInt32(image, offset + 24, 0x5);
        WriteUInt32(image, offset + 28, 0x1000);
    }

    private static void WriteSectionHeader(byte[] image, ulong tableOffset, ulong entryOffset, int entrySize, bool is64,
        uint name, uint type, ulong offset, ulong size)
    {
        var start = (int)(tableOffset + entryOffset);
        if (is64)
        {
            WriteUInt32(image, start, name);
            WriteUInt32(image, start + 4, type);
            WriteUInt64(image, start + 8, 0);
            WriteUInt64(image, start + 16, 0);
            WriteUInt64(image, start + 24, offset);
            WriteUInt64(image, start + 32, size);
            WriteUInt32(image, start + 36, 0);
            WriteUInt32(image, start + 40, 0);
            WriteUInt64(image, start + 48, 1);
            WriteUInt64(image, start + 56, 0);
            return;
        }

        WriteUInt32(image, start, name);
        WriteUInt32(image, start + 4, type);
        WriteUInt32(image, start + 8, 0);
        WriteUInt32(image, start + 12, 0);
        WriteUInt32(image, start + 16, (uint)offset);
        WriteUInt32(image, start + 20, (uint)size);
        WriteUInt32(image, start + 24, 0);
        WriteUInt32(image, start + 28, 0);
        WriteUInt32(image, start + 32, 1);
        WriteUInt32(image, start + 36, 0);
        _ = entrySize;
    }

    private static void WriteUInt16(byte[] image, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] image, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), value);

    private static void WriteUInt64(byte[] image, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset, 8), value);
}