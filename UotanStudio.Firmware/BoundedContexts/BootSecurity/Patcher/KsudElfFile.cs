using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher.Ksud;

/// <summary>ELF64（小端）解析与地址换算（对应 Python 工具中的 Elf 类）。</summary>
public sealed class ElfFile
{
    public const uint PtLoad = 1;
    public const uint PfX = 0x1, PfW = 0x2, PfR = 0x4;

    /// <summary>原始文件字节。</summary>
    public byte[] Data { get; }

    /// <summary>e_machine：183=EM_AARCH64，62=EM_X86_64。</summary>
    public ushort Machine { get; }

    /// <summary>PT_LOAD 段：(vaddr, offset, filesz, flags)，按 vaddr 升序。</summary>
    private readonly List<(ulong Vaddr, ulong Offset, ulong Filesz, uint Flags)> _segs = new();

    public ElfFile(byte[] data)
    {
        if (data.Length < 64 || data[0] != 0x7F || data[1] != (byte)'E' ||
            data[2] != (byte)'L' || data[3] != (byte)'F')
            throw new InvalidDataException("不是有效的 ELF 文件");
        if (data[4] != 2)
            throw new InvalidDataException("仅支持 ELF64");
        if (data[5] != 1)
            throw new InvalidDataException("仅支持小端 ELF");
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

    /// <summary>虚拟地址 -> 文件偏移；不在任何 PT_LOAD 段内返回 null。</summary>
    public long? VaToOff(ulong va)
    {
        foreach (var (vaddr, poff, psize, _) in _segs)
            if (va >= vaddr && va < vaddr + psize)
                return (long)(poff + (va - vaddr));
        return null;
    }

    /// <summary>文件偏移 -> 虚拟地址；不在任何 PT_LOAD 段内返回 null。</summary>
    public ulong? OffToVa(long off)
    {
        if (off < 0)
            return null;
        foreach (var (vaddr, poff, psize, _) in _segs)
            if ((ulong)off >= poff && (ulong)off < poff + psize)
                return vaddr + ((ulong)off - poff);
        return null;
    }

    public bool IsExecVa(ulong va)
    {
        foreach (var (vaddr, poff, psize, flags) in _segs)
            if (va >= vaddr && va < vaddr + psize)
                return (flags & PfX) != 0;
        return false;
    }

    public bool IsWritableVa(ulong va)
    {
        foreach (var (vaddr, poff, psize, flags) in _segs)
            if (va >= vaddr && va < vaddr + psize)
                return (flags & PfW) != 0;
        return false;
    }

    /// <summary>
    /// 把反汇编算出的引用地址还原为文件偏移。
    /// 某些构建把数据引用链接成"加载基址 + 文件偏移"的绝对地址
    /// （如 0x100000000 + offset，基址不落在任何段内），需要减去基址再查。
    /// </summary>
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

    /// <summary>只读段（rodata 所在）的 [offBegin, offEnd) 列表。</summary>
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

    /// <summary>节头表条目。</summary>
    public sealed record Section(string Name, uint Type, ulong Addr, ulong Offset, ulong Size);

    /// <summary>解析节头表，返回节列表（对应 Python 的 section_table）。</summary>
    public List<Section> SectionTable()
    {
        ulong shoff = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(40));
        ushort shentsize = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(58));
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(60));
        ushort shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(62));
        if (shoff == 0 || shnum == 0)
            return new List<Section>();
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
            return new List<Section>();
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
    /// 某些 Android .so 中，指向 rodata/text 的指针存放在可写段，文件内值为 0，
    /// 靠 .rela.dyn 的 R_AARCH64_RELATIVE 等重定位在加载时写入。
    /// 这里按重定位表重建指针值，返回修补后的字节副本（无重定位则返回 null）。
    /// </summary>
    public byte[]? PatchView()
    {
        var rela = new List<(long Off, ulong Size)>();
        List<ulong>? dynsymValues = null;
        foreach (var sec in SectionTable())
        {
            if (sec.Type == 4)                        // SHT_RELA
            {
                long? o = VaToOff(sec.Addr);
                if (o is not null)
                    rela.Add(((long)o, sec.Size));
            }
            else if (sec.Type == 11)                  // SHT_DYNSYM
            {
                var vals = new List<ulong>();
                for (ulong p = sec.Offset; p + 24 <= sec.Offset + sec.Size && p + 24 <= (ulong)Data.Length; p += 24)
                    vals.Add(BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)p + 8)));
                dynsymValues = vals;
            }
        }
        if (rela.Count == 0)
            return null;

        uint rel = Machine == 183 ? 1027u : 8u;       // R_*_RELATIVE
        uint absType = Machine == 183 ? 1026u : 1u;   // R_*_ABS64
        uint glob = Machine == 183 ? 1029u : 6u;      // R_*_GLOB_DAT
        uint jump = Machine == 183 ? 1030u : 7u;      // R_*_JUMP_SLOT

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