using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>Little-endian ELF64 parsing and virtual-address/file-offset translation.
/// <para>小端序 ELF64 解析与虚拟地址/文件偏移换算。</para></summary>
public sealed class ElfFile
{
    /// <summary>PT_LOAD program header type. <para>PT_LOAD 程序头类型。</para></summary>
    public const uint PtLoad = 1;

    /// <summary>PF_X, PF_W and PF_R segment permission flags. <para>PF_X、PF_W 与 PF_R 段权限标志。</para></summary>
    public const uint PfX = 0x1, PfW = 0x2, PfR = 0x4;

    /// <summary>The complete raw file bytes of the image.
    /// <para>镜像完整的原始文件字节。</para></summary>
    /// <value>The array passed to the constructor, kept unchanged after parsing.
    /// <para>构造时传入的数组，解析后保持不变。</para></value>
    public byte[] Data { get; }

    /// <summary>The <c>e_machine</c> field: 183 is AArch64, 62 is x86-64.
    /// <para><c>e_machine</c> 字段：183 为 AArch64，62 为 x86-64。</para></summary>
    /// <value>The machine type read from header offset 18.
    /// <para>从头部偏移 18 处读取的机器类型。</para></value>
    public ushort Machine { get; }

    /// <summary>PT_LOAD 段：(vaddr, offset, filesz, flags)，按 vaddr 升序。</summary>
    private readonly List<(ulong Vaddr, ulong Offset, ulong Filesz, uint Flags)> _segs = new();

    /// <summary>
    /// Parses the ELF header and collects the PT_LOAD program headers with a non-zero file size, sorted by
    /// virtual address; program header entries past the end of the buffer are omitted.
    /// <para>解析 ELF 头部，收集文件大小非零的 PT_LOAD 程序头并按虚拟地址升序排列；超出缓冲区末尾的程序头条目会被忽略。</para>
    /// </summary>
    /// <param name="data">
    /// Complete little-endian ELF64 file bytes; the array is retained by <see cref="Data"/> and must not be
    /// mutated afterwards.
    /// <para>完整的小端序 ELF64 文件字节；数组由 <see cref="Data"/> 持有，之后不得修改。</para>
    /// </param>
    /// <exception cref="InvalidDataException">The buffer is shorter than 64 bytes, the ELF magic is wrong,
    /// the class is not ELF64, or the data is not little-endian.
    /// <para>缓冲区不足 64 字节、ELF 魔数错误、不是 ELF64，或数据不是小端序。</para></exception>
    public ElfFile(byte[] data)
    {
        if (data.Length < 64 || data[0] != 0x7F || data[1] != (byte)'E' ||
            data[2] != (byte)'L' || data[3] != (byte)'F')
            throw new InvalidDataException("Not a valid ELF file.");
        if (data[4] != 2)
            throw new InvalidDataException("Only ELF64 is supported.");
        if (data[5] != 1)
            throw new InvalidDataException("Only little-endian ELF is supported.");
        Data = data;
        Machine = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18));
        ulong phoff = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32));
        ushort phentsize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(54));
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(56));
        for (int i = 0; i < phnum; i++)
        {
            long off = (long)phoff + i * phentsize;
            if (off + 40 > data.Length)
                break;
            uint pType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off));
            uint pFlags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off + 4));
            ulong pOffset = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)off + 8));
            ulong pVaddr = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)off + 16));
            ulong pFilesz = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)off + 32));
            if (pType == PtLoad && pFilesz > 0)
                _segs.Add((pVaddr, pOffset, pFilesz, pFlags));
        }
        _segs.Sort((a, b) => a.Vaddr.CompareTo(b.Vaddr));
    }

    /// <summary>Maps a virtual address to a file offset through the PT_LOAD segments.
    /// <para>通过 PT_LOAD 段把虚拟地址换算为文件偏移。</para></summary>
    /// <param name="va">Virtual address to translate. <para>待换算的虚拟地址。</para></param>
    /// <returns>The file offset, or <see langword="null"/> when <paramref name="va"/> falls outside every
    /// PT_LOAD segment. <para>文件偏移；当 <paramref name="va"/> 不在任何 PT_LOAD 段内时返回 <see langword="null"/>。</para></returns>
    public long? VaToOff(ulong va)
    {
        foreach (var (vaddr, poff, psize, _) in _segs)
            if (va >= vaddr && va < vaddr + psize)
                return (long)(poff + (va - vaddr));
        return null;
    }

    /// <summary>Maps a file offset back to a virtual address through the PT_LOAD segments.
    /// <para>通过 PT_LOAD 段把文件偏移换算回虚拟地址。</para></summary>
    /// <param name="off">File offset to translate. <para>待换算的文件偏移。</para></param>
    /// <returns>The virtual address, or <see langword="null"/> when <paramref name="off"/> is negative or
    /// falls outside every PT_LOAD segment.
    /// <para>虚拟地址；当 <paramref name="off"/> 为负或不在任何 PT_LOAD 段内时返回 <see langword="null"/>。</para></returns>
    public ulong? OffToVa(long off)
    {
        if (off < 0)
            return null;
        foreach (var (vaddr, poff, psize, _) in _segs)
            if ((ulong)off >= poff && (ulong)off < poff + psize)
                return vaddr + ((ulong)off - poff);
        return null;
    }

    /// <summary>Reports whether the PT_LOAD segment containing a virtual address is executable.
    /// <para>判断包含指定虚拟地址的 PT_LOAD 段是否可执行。</para></summary>
    /// <param name="va">Virtual address to test. <para>待检测的虚拟地址。</para></param>
    /// <returns><see langword="true"/> when the containing segment sets <c>PF_X</c>; <see langword="false"/>
    /// when the address is unmapped or the segment lacks <c>PF_X</c>.
    /// <para>所属段设置了 <c>PF_X</c> 时返回 <see langword="true"/>；地址未映射或段没有 <c>PF_X</c> 时返回 <see langword="false"/>。</para></returns>
    public bool IsExecVa(ulong va)
    {
        foreach (var (vaddr, poff, psize, flags) in _segs)
            if (va >= vaddr && va < vaddr + psize)
                return (flags & PfX) != 0;
        return false;
    }

    /// <summary>Reports whether the PT_LOAD segment containing a virtual address is writable.
    /// <para>判断包含指定虚拟地址的 PT_LOAD 段是否可写。</para></summary>
    /// <param name="va">Virtual address to test. <para>待检测的虚拟地址。</para></param>
    /// <returns><see langword="true"/> when the containing segment sets <c>PF_W</c>; <see langword="false"/>
    /// when the address is unmapped or the segment lacks <c>PF_W</c>.
    /// <para>所属段设置了 <c>PF_W</c> 时返回 <see langword="true"/>；地址未映射或段没有 <c>PF_W</c> 时返回 <see langword="false"/>。</para></returns>
    public bool IsWritableVa(ulong va)
    {
        foreach (var (vaddr, poff, psize, flags) in _segs)
            if (va >= vaddr && va < vaddr + psize)
                return (flags & PfW) != 0;
        return false;
    }

    /// <summary>
    /// Restores a reference address computed by disassembly to a file offset. Some builds link data
    /// references as absolute addresses of the form &quot;load base + file offset&quot; (for example
    /// 0x100000000 + offset, where the base falls outside every segment); when the direct mapping fails,
    /// the bases 0x100000000, 0x40000000 and 0x20000000 are subtracted in turn and re-looked-up.
    /// <para>把反汇编算出的引用地址还原为文件偏移。某些构建把数据引用链接成“加载基址 + 文件偏移”的绝对地址
    /// （如 0x100000000 + offset，基址不落在任何段内），直接换算失败时依次减去 0x100000000、0x40000000、
    /// 0x20000000 三个基址再查。</para>
    /// </summary>
    /// <param name="reference">Reference address produced by instruction scanning.
    /// <para>由指令扫描得到的引用地址。</para></param>
    /// <returns>The file offset, or <see langword="null"/> when neither the address nor any biased form
    /// maps into a PT_LOAD segment.
    /// <para>文件偏移；当该地址及其减去基址的形式都不落在任何 PT_LOAD 段内时返回 <see langword="null"/>。</para></returns>
    public long? ResolveRef(ulong reference)
    {
        long? off = VaToOff(reference);
        if (off is not null)
            return off;
        foreach (ulong bias in new ulong[] { 0x100000000UL, 0x40000000UL, 0x20000000UL })
        {
            if (reference >= bias)
            {
                off = VaToOff(reference - bias);
                if (off is not null)
                    return off;
            }
        }
        return null;
    }

    /// <summary>Lists the file ranges of read-only data segments (where rodata lives).
    /// <para>列出只读数据段（rodata 所在）的文件范围。</para></summary>
    /// <returns>One half-open <c>[Off, End)</c> file range per PT_LOAD segment that has neither
    /// <c>PF_W</c> nor <c>PF_X</c>. <para>每个既无 <c>PF_W</c> 也无 <c>PF_X</c> 的 PT_LOAD 段对应一个半开区间 <c>[Off, End)</c>。</para></returns>
    public List<(long Off, long End)> ReadableRanges()
    {
        var ranges = new List<(long, long)>();
        foreach (var (vaddr, poff, psize, flags) in _segs)
        {
            if ((flags & PfW) != 0 || (flags & PfX) != 0)
                continue;
            ranges.Add(((long)poff, (long)(poff + psize)));
        }
        return ranges;
    }

    /// <summary>A section header table entry.
    /// <para>节头表条目。</para></summary>
    /// <param name="Name">Section name decoded as Latin-1 from the section name string table.
    /// <para>从节名字符串表按 Latin-1 解码出的节名。</para></param>
    /// <param name="Type">The <c>sh_type</c> value (e.g. 4 = SHT_RELA, 11 = SHT_DYNSYM).
    /// <para><c>sh_type</c> 值（如 4 = SHT_RELA，11 = SHT_DYNSYM）。</para></param>
    /// <param name="Addr">The <c>sh_addr</c> load address. <para><c>sh_addr</c> 加载地址。</para></param>
    /// <param name="Offset">The <c>sh_offset</c> file offset. <para><c>sh_offset</c> 文件偏移。</para></param>
    /// <param name="Size">The <c>sh_size</c> byte length. <para><c>sh_size</c> 字节长度。</para></param>
    public sealed record Section(string Name, uint Type, ulong Addr, ulong Offset, ulong Size);

    /// <summary>Parses the section header table and resolves names from the string table section.
    /// <para>解析节头表，并从字符串表节还原节名称。</para></summary>
    /// <returns>The sections in table order; an empty list when <c>sh_offset</c> or <c>sh_num</c> is zero
    /// or the string table index is out of range. Entries past the end of the file are omitted.
    /// <para>按表顺序返回的节；当 <c>sh_offset</c> 或 <c>sh_num</c> 为 0、或字符串表索引越界时返回空列表。
    /// 超出文件末尾的条目会被忽略。</para></returns>
    public List<Section> SectionTable()
    {
        ulong shoff = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(40));
        ushort shentsize = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(58));
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(60));
        ushort shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(62));
        if (shoff == 0 || shnum == 0)
            return [];
        var secs = new List<(uint NameOff, uint Type, ulong Flags, ulong Addr, ulong Offset, ulong Size)>();
        for (int i = 0; i < shnum; i++)
        {
            long off = (long)shoff + i * shentsize;
            if (off + 40 > Data.Length)
                break;
            uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)off));
            uint typ = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)off + 4));
            ulong flags = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)off + 8));
            ulong addr = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)off + 16));
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)off + 24));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)off + 32));
            secs.Add((nameOff, typ, flags, addr, offset, size));
        }
        if (shstrndx >= secs.Count)
            return [];
        ulong shstrOff = secs[shstrndx].Offset;
        var outSections = new List<Section>(secs.Count);
        foreach (var (nameOff, typ, flags, addr, offset, size) in secs)
        {
            int p = (int)(shstrOff + nameOff);
            int end = p;
            while (end < Data.Length && Data[end] != 0)
                end++;
            string name = EncodingLatin1.GetString(Data, p, end - p);
            outSections.Add(new Section(name, typ, addr, offset, size));
        }
        return outSections;
    }

    internal static readonly Encoding EncodingLatin1 = Encoding.GetEncoding("ISO-8859-1");

    /// <summary>
    /// Rebuilds load-time pointer values from the relocation table and returns a patched byte copy. In
    /// some Android .so files, pointers into rodata/text sit in writable segments with an on-disk value
    /// of 0 and are written at load time by R_AARCH64_RELATIVE-style entries in .rela.dyn. Only SHT_RELA
    /// entries for the machine's relative, absolute, GLOB_DAT and JUMP_SLOT types are applied; the
    /// original <see cref="Data"/> is never modified.
    /// <para>某些 Android .so 中，指向 rodata/text 的指针存放在可写段，文件内值为 0，
    /// 靠 .rela.dyn 的 R_AARCH64_RELATIVE 等重定位在加载时写入。这里按重定位表重建指针值，返回修补后的字节副本。
    /// 只应用与当前机器匹配的相对、绝对、GLOB_DAT 与 JUMP_SLOT 类型的 SHT_RELA 条目；原始 <see cref="Data"/> 不会被修改。</para>
    /// </summary>
    /// <returns>A patched clone of <see cref="Data"/>, or <see langword="null"/> when the image has no
    /// applicable RELA section or no relocation was written.
    /// <para>修补后的 <see cref="Data"/> 副本；镜像没有可用的 RELA 段或没有任何重定位被写入时返回 <see langword="null"/>。</para></returns>
    public byte[]? PatchView()
    {
        var rela = new List<(long Off, ulong Size)>();
        List<ulong>? dynsymValues = null;
        foreach (var sec in SectionTable())
        {
            if (sec.Type == 4)
            {
                long? o = VaToOff(sec.Addr);
                if (o is not null)
                    rela.Add(((long)o, sec.Size));
            }
            else if (sec.Type == 11)
            {
                var vals = new List<ulong>();
                for (ulong p = sec.Offset; p + 24 <= sec.Offset + sec.Size && p + 24 <= (ulong)Data.Length; p += 24)
                    vals.Add(BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)p + 8)));
                dynsymValues = vals;
            }
        }
        if (rela.Count == 0)
            return null;

        uint rel = Machine == 183 ? 1027u : 8u;
        uint absType = Machine == 183 ? 1026u : 1u;
        uint glob = Machine == 183 ? 1029u : 6u;
        uint jump = Machine == 183 ? 1030u : 7u;

        var view = (byte[])Data.Clone();
        int patched = 0;
        foreach (var (rOff, rSize) in rela)
        {
            long end = Math.Min((long)rOff + (long)rSize, Data.Length - 24);
            for (long p = rOff; p <= end; p += 24)
            {
                ulong loc = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)p));
                ulong info = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)p + 8));
                long add = BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan((int)p + 16));
                uint rtype = (uint)(info & 0xFFFFFFFF);
                ulong rsym = info >> 32;
                long? lo = VaToOff(loc);
                if (lo is null || lo + 8 > view.Length)
                    continue;
                ulong val;
                if (rtype == rel)
                {
                    val = (ulong)add;
                }
                else if (rtype == absType)
                {
                    ulong symval = (dynsymValues != null && rsym < (ulong)dynsymValues.Count) ? dynsymValues[(int)rsym] : 0;
                    val = symval + (ulong)add;
                }
                else if (rtype == glob || rtype == jump)
                {
                    val = (dynsymValues != null && rsym < (ulong)dynsymValues.Count) ? dynsymValues[(int)rsym] : 0;
                }
                else
                {
                    continue;
                }
                BinaryPrimitives.WriteUInt64LittleEndian(view.AsSpan((int)lo), val);
                patched++;
            }
        }
        return patched > 0 ? view : null;
    }
}