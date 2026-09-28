using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BootSecurity.Kernel;

/// <summary>
/// ELF machine identifiers supported by the managed raw-kernel symbolizer.
/// <para>托管原始内核符号化器所支持的 ELF 机器标识。</para>
/// </summary>
public enum KernelElfMachine : ushort
{
    /// <summary>ARM 32-bit (<c>EM_ARM</c>, value <c>40</c>). <para>32 位 ARM（<c>EM_ARM</c>，值 <c>40</c>）。</para></summary>
    Arm = 40,

    /// <summary>x86-64 (<c>EM_X86_64</c>, value <c>62</c>). <para>x86-64（<c>EM_X86_64</c>，值 <c>62</c>）。</para></summary>
    X86_64 = 62,

    /// <summary>ARM 64-bit (<c>EM_AARCH64</c>, value <c>183</c>). <para>64 位 ARM（<c>EM_AARCH64</c>，值 <c>183</c>）。</para></summary>
    AArch64 = 183,

    /// <summary>RISC-V (<c>EM_RISCV</c>, value <c>243</c>). <para>RISC-V（<c>EM_RISCV</c>，值 <c>243</c>）。</para></summary>
    RiscV = 243
}

/// <summary>
/// Options controlling raw-kernel ELF64 generation.
/// <para>控制原始内核 ELF64 生成的选项。</para>
/// </summary>
internal sealed record KernelElfOptions
{
    /// <summary>
    /// Machine identifier written to the ELF header; <see langword="null"/> selects automatic detection from the
    /// kernel image signatures.
    /// <para>写入 ELF 头的机器标识；为 <see langword="null"/> 时根据内核镜像特征自动探测。</para>
    /// </summary>
    /// <value>
    /// An explicit machine, or <see langword="null"/> for detection. Detection recognizes ARM64, RISC-V and
    /// x86-64 images and fails on anything else.
    /// <para>显式机器标识，或 <see langword="null"/> 表示探测。探测可识别 ARM64、RISC-V 与 x86-64 镜像，其余情况失败。</para>
    /// </value>
    public KernelElfMachine? Machine { get; init; }

    /// <summary>
    /// Segment load address recorded in the ELF program header; <see langword="null"/> infers it from the symbols.
    /// <para>ELF 程序头记录的段加载地址；为 <see langword="null"/> 时按符号推断。</para>
    /// </summary>
    /// <value>
    /// An explicit address, or <see langword="null"/> to use the lowest non-zero symbol address rounded down to a
    /// page boundary.
    /// <para>显式地址，或 <see langword="null"/> 表示取最小非零符号地址并向页边界向下取整。</para>
    /// </value>
    public ulong? LoadAddress { get; init; }
}

/// <summary>
/// Result of producing a symbolized ELF64 kernel image.
/// <para>生成带符号 ELF64 内核镜像的结果。</para>
/// </summary>
/// <param name="OutputPath">Full path of the atomically written ELF file; an existing file was replaced.
/// <para>原子写入的 ELF 文件完整路径；已存在的文件会被替换。</para></param>
/// <param name="Machine">Machine identifier recorded in the ELF header. <para>ELF 头中记录的机器标识。</para></param>
/// <param name="LoadAddress">Load address used for the text segment. <para>代码段使用的加载地址。</para></param>
/// <param name="EntryAddress">Entry point taken from <c>_stext</c>/<c>_text</c>, else the first non-zero symbol address. <para>入口地址优先取 <c>_stext</c>/<c>_text</c>，否则取首个非零符号地址。</para></param>
/// <param name="SymbolCount">Number of kernel symbols written to the symbol table, excluding its leading null entry. <para>写入符号表的内核符号数量，不含表首的空条目。</para></param>
/// <param name="OutputLength">Exact length in bytes of the produced file. <para>生成文件的精确字节长度。</para></param>
public sealed record KernelElfResult(
    string OutputPath,
    KernelElfMachine Machine,
    ulong LoadAddress,
    ulong EntryAddress,
    int SymbolCount,
    long OutputLength);

internal static class KernelElfWriter
{
    private const int ElfHeaderSize = 64;
    private const int ProgramHeaderSize = 56;
    private const int SectionHeaderSize = 64;
    private const int SymbolSize = 24;
    private const int PageSize = 4096;
    private static readonly byte[] NullByte = [0];
    private static readonly byte[] SectionNames = "\0.text\0.strtab\0.symtab\0.shstrtab\0"u8.ToArray();

    internal static async Task<KernelElfResult> WriteAsync(
        ReadOnlyMemory<byte> kernel,
        IReadOnlyList<KernelSymbol> symbols,
        string outputPath,
        KernelElfOptions options,
        CancellationToken cancellationToken)
    {
        if (kernel.IsEmpty)
        {
            throw new InvalidDataException("A symbolized ELF requires non-empty kernel bytes.");
        }
        if (symbols.Count == 0)
        {
            throw new InvalidDataException("A symbolized ELF requires at least one kernel symbol.");
        }
        var machine = options.Machine ?? DetectMachine(kernel.Span);
        var loadAddress = options.LoadAddress ?? InferLoadAddress(symbols);
        var entryAddress = symbols.FirstOrDefault(symbol => symbol.Name is "_stext" or "_text").Address;
        if (entryAddress == 0)
        {
            entryAddress = symbols.First(symbol => symbol.Address != 0).Address;
        }

        var ordered = symbols
            .OrderBy(symbol => char.IsUpper(symbol.Type) ? 1 : 0)
            .ThenBy(symbol => symbol.Address)
            .ToArray();
        var localCount = ordered.Count(symbol => !char.IsUpper(symbol.Type));
        var stringOffsets = new uint[ordered.Length];
        long stringTableSize = 1;
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Name.Length == 0 || ordered[index].Name.IndexOf('\0') >= 0
                || Encoding.UTF8.GetByteCount(ordered[index].Name) > 4096)
            {
                throw new InvalidDataException("kallsyms contains an invalid ELF symbol name.");
            }
            stringOffsets[index] = checked((uint)stringTableSize);
            stringTableSize = checked(stringTableSize + Encoding.UTF8.GetByteCount(ordered[index].Name) + 1);
        }

        var textOffset = (long)PageSize;
        var stringOffset = AlignUp(checked(textOffset + kernel.Length), 8);
        var symbolOffset = AlignUp(checked(stringOffset + stringTableSize), 8);
        var symbolTableSize = checked((long)(ordered.Length + 1) * SymbolSize);
        var sectionNameOffset = AlignUp(checked(symbolOffset + symbolTableSize), 8);
        var sectionHeaderOffset = AlignUp(checked(sectionNameOffset + SectionNames.Length), 8);
        var outputLength = checked(sectionHeaderOffset + SectionHeaderSize * 5L);
        var output = Path.GetFullPath(outputPath);

        await FileExtraction.WriteAtomicAsync(output, async stream =>
        {
            await WriteHeaderAsync(
                stream,
                machine,
                loadAddress,
                entryAddress,
                kernel.Length,
                sectionHeaderOffset,
                cancellationToken).ConfigureAwait(false);
            await PadToAsync(stream, textOffset, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(kernel, cancellationToken).ConfigureAwait(false);
            await PadToAsync(stream, stringOffset, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(NullByte, cancellationToken).ConfigureAwait(false);
            foreach (var symbol in ordered)
            {
                var name = Encoding.UTF8.GetBytes(symbol.Name);
                await stream.WriteAsync(name, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(NullByte, cancellationToken).ConfigureAwait(false);
            }

            await PadToAsync(stream, symbolOffset, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(new byte[SymbolSize], cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < ordered.Length; index++)
            {
                var symbol = ordered[index];
                var entry = new byte[SymbolSize];
                BinaryPrimitives.WriteUInt32LittleEndian(entry, stringOffsets[index]);
                var global = char.IsUpper(symbol.Type);
                entry[4] = checked((byte)((global ? 1 : 0) << 4));
                var inText = symbol.Address >= loadAddress && symbol.Address - loadAddress < (ulong)kernel.Length;
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6),
                    symbol.Type is 'A' or 'a' || !inText ? (ushort)0xFFF1 : (ushort)1);
                BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8), symbol.Address);
                await stream.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
            }

            await PadToAsync(stream, sectionNameOffset, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(SectionNames, cancellationToken).ConfigureAwait(false);
            await PadToAsync(stream, sectionHeaderOffset, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(new byte[SectionHeaderSize], cancellationToken).ConfigureAwait(false);
            await WriteSectionAsync(stream, 1, 1, 0x6, loadAddress, textOffset, kernel.Length, 0, 0, PageSize, 0, cancellationToken)
                .ConfigureAwait(false);
            await WriteSectionAsync(stream, 7, 3, 0, 0, stringOffset, stringTableSize, 0, 0, 1, 0, cancellationToken)
                .ConfigureAwait(false);
            await WriteSectionAsync(stream, 15, 2, 0, 0, symbolOffset, symbolTableSize, 2,
                    checked((uint)localCount + 1), 8, SymbolSize, cancellationToken)
                .ConfigureAwait(false);
            await WriteSectionAsync(stream, 23, 3, 0, 0, sectionNameOffset, SectionNames.Length, 0, 0, 1, 0, cancellationToken)
                .ConfigureAwait(false);
            if (stream.Length != outputLength)
            {
                throw new InvalidDataException("Symbolized ELF length does not match its section layout.");
            }
        }, overwrite: true, cancellationToken).ConfigureAwait(false);

        return new(output, machine, loadAddress, entryAddress, ordered.Length, outputLength);
    }

    private static async Task WriteHeaderAsync(
        Stream output,
        KernelElfMachine machine,
        ulong loadAddress,
        ulong entryAddress,
        int kernelLength,
        long sectionHeaderOffset,
        CancellationToken cancellationToken)
    {
        var header = new byte[ElfHeaderSize];
        header[0] = 0x7F;
        header[1] = (byte)'E';
        header[2] = (byte)'L';
        header[3] = (byte)'F';
        header[4] = 2;
        header[5] = 1;
        header[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), (ushort)machine);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), entryAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), ElfHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40), checked((ulong)sectionHeaderOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(52), ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(54), ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(58), SectionHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(60), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(62), 4);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        var program = new byte[ProgramHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(program, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(4), 5);
        BinaryPrimitives.WriteUInt64LittleEndian(program.AsSpan(8), PageSize);
        BinaryPrimitives.WriteUInt64LittleEndian(program.AsSpan(16), loadAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(program.AsSpan(24), loadAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(program.AsSpan(32), checked((ulong)kernelLength));
        BinaryPrimitives.WriteUInt64LittleEndian(program.AsSpan(40), checked((ulong)kernelLength));
        BinaryPrimitives.WriteUInt64LittleEndian(program.AsSpan(48), PageSize);
        await output.WriteAsync(program, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSectionAsync(
        Stream output,
        uint name,
        uint type,
        ulong flags,
        ulong address,
        long offset,
        long size,
        uint link,
        uint info,
        ulong alignment,
        ulong entrySize,
        CancellationToken cancellationToken)
    {
        var section = new byte[SectionHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(section, name);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(4), type);
        BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(8), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(16), address);
        BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(24), checked((ulong)offset));
        BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(32), checked((ulong)size));
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(40), link);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(44), info);
        BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(48), alignment);
        BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(56), entrySize);
        await output.WriteAsync(section, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PadToAsync(Stream output, long target, CancellationToken cancellationToken)
    {
        if (output.Position > target)
        {
            throw new InvalidDataException("ELF section layout overlaps.");
        }
        var buffer = new byte[4096];
        while (output.Position < target)
        {
            var count = (int)Math.Min(buffer.Length, target - output.Position);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private static KernelElfMachine DetectMachine(ReadOnlySpan<byte> kernel)
    {
        if (kernel.Length >= 0x3C)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(kernel.Slice(0x38, 4));
            if (magic == 0x644D5241)
            {
                return KernelElfMachine.AArch64;
            }
            if (magic == 0x05435352)
            {
                return KernelElfMachine.RiscV;
            }
        }
        if (kernel.Length >= 0x206 && kernel.Slice(0x202, 4).SequenceEqual("HdrS"u8))
        {
            return KernelElfMachine.X86_64;
        }
        throw new InvalidDataException("Kernel architecture is not recognizable; specify KernelElfOptions.Machine.");
    }

    private static ulong InferLoadAddress(IReadOnlyList<KernelSymbol> symbols)
    {
        var first = symbols.Where(symbol => symbol.Address != 0).Min(symbol => symbol.Address);
        return first & ~0xFFFUL;
    }

    private static long AlignUp(long value, int alignment) => checked((value + alignment - 1) & -alignment);
}