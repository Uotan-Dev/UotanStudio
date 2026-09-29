using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>
/// <para>表示从 KernelSU 模块 ELF 中定位到的一个资源条目及其还原结果。</para>
/// Represents a single asset located in the KernelSU module ELF together with its restored result.
/// </summary>
public sealed class Asset
{
    /// <summary>Asset file name as recorded in the table entry. <para>表中记录的资源文件名。</para></summary>
    /// <value>A Latin-1 name ending in <c>_kernelsu.ko</c>; <see cref="string.Empty"/> for a freshly created entry.
    /// <para>以 <c>_kernelsu.ko</c> 结尾的 Latin-1 名称；新建条目时为空字符串。</para></value>
    public string Name { get; set; } = string.Empty;
    /// <summary>Factory function referenced by the table entry. <para>表条目引用的工厂函数。</para></summary>
    /// <value>Virtual address inside an executable segment; <c>0</c> until located.
    /// <para>可执行段内的虚拟地址；定位前为 <c>0</c>。</para></value>
    public ulong FnVa { get; set; }
    /// <summary>Location of the 24-byte table entry. <para>24 字节表条目的位置。</para></summary>
    /// <value>Byte offset into the patched view data scanned by <see cref="Extractor.FindEntries"/>; <c>0</c> for a
    /// hand-built entry. <para><see cref="Extractor.FindEntries"/> 所扫描修补视图中的字节偏移；手工构造的条目为 <c>0</c>。</para></value>
    public long TableOff { get; set; }
    /// <summary>Start of the asset's deflate payload. <para>资源 deflate 数据的起点。</para></summary>
    /// <value>File offset into the bytes passed to <see cref="Extractor.GetOutBytes"/>; <see langword="null"/> when
    /// the payload was not located. <para><see cref="Extractor.GetOutBytes"/> 所用字节的文件偏移；未定位到数据时为 <see langword="null"/>。</para></value>
    public int? DataOff { get; set; }
    /// <summary>Compressed size of the located payload. <para>已定位数据的压缩后大小。</para></summary>
    /// <value>Number of input bytes the deflate stream consumes; <see langword="null"/> when
    /// <see cref="DataOff"/> is <see langword="null"/>. <para>deflate 流实际消耗的输入字节数；<see cref="DataOff"/> 为 <see langword="null"/> 时同为 null。</para></value>
    public int? CompLen { get; set; }
    /// <summary>Uncompressed size of the located payload. <para>已定位数据的原始大小。</para></summary>
    /// <value>Decompressed length in bytes; <see langword="null"/> when the payload was not located.
    /// <para>解压后的字节数；未定位到数据时为 <see langword="null"/>。</para></value>
    public int? Size { get; set; }
    /// <summary>Integrity digest of the decompressed payload. <para>解压数据的完整性摘要。</para></summary>
    /// <value>Lowercase hexadecimal SHA-256 of the decompressed bytes; <see cref="string.Empty"/> until the
    /// payload is restored. <para>解压字节的小写十六进制 SHA-256；数据还原前为空字符串。</para></value>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>Outcome of the hash and ELF-magic checks. <para>摘要与 ELF 魔数校验的结果。</para></summary>
    /// <value><see langword="true"/> only when <see cref="HashInBinary"/> holds and the decompressed output
    /// is at least 4 bytes starting with the ELF magic. <para>仅当 <see cref="HashInBinary"/> 成立且解压输出不少于 4 字节、
    /// 以 ELF 魔数开头时为 <see langword="true"/>。</para></value>
    public bool Verified { get; set; }
    /// <summary>How the payload reference was located. <para>数据引用的定位方式。</para></summary>
    /// <value><c>entry-factory</c> when found through a static thunk, <c>entry-ref</c> when found directly
    /// among entry references; <see cref="string.Empty"/> when the payload was not restored.
    /// <para>经静态跳板找到时为 <c>entry-factory</c>，直接在条目引用中找到时为 <c>entry-ref</c>；数据未还原时为空字符串。</para></value>
    public string Method { get; set; } = string.Empty;
    /// <summary>Location of the stored hash block. <para>内嵌摘要块的位置。</para></summary>
    /// <value>Virtual address of the 32-byte block matching <see cref="Sha256"/>, or <see langword="null"/>
    /// when no stored block matched. <para>与 <see cref="Sha256"/> 匹配的 32 字节块的虚拟地址；没有匹配的存储块时为 <see langword="null"/>。</para></value>
    public ulong? HashRefVa { get; set; }
    /// <summary>Whether the digest appears inside the binary. <para>摘要是否出现在二进制内部。</para></summary>
    /// <value><see langword="true"/> when the digest was found as a stored 32-byte block or reconstructed
    /// from inline load instructions. <para>摘要以 32 字节存储块形式找到、或从内联加载指令重建成功时为 <see langword="true"/>。</para></value>
    public bool HashInBinary { get; set; }
    /// <summary>Destination file path recorded for the extracted payload. <para>为提取数据记录的目标文件路径。</para></summary>
    /// <value>Written by the caller that saves the asset; <see cref="string.Empty"/> until then.
    /// <para>由保存该资源的调用方写入；在此之前为空字符串。</para></value>
    public string OutFile { get; set; } = string.Empty;

    /// <summary>Creates an entry with every field at its default value.
    /// <para>创建一个所有字段均为默认值的条目。</para></summary>
    public Asset() { }

    /// <summary>
    /// <para>复制另一个资源条目的全部字段。</para>
    /// Copies all fields of another asset entry.
    /// </summary>
    /// <param name="other">Entry whose field values are copied. <para>被复制字段值的条目。</para></param>
    public Asset(Asset other)
    {
        Name = other.Name; FnVa = other.FnVa; TableOff = other.TableOff;
        DataOff = other.DataOff; CompLen = other.CompLen; Size = other.Size;
        Sha256 = other.Sha256; Verified = other.Verified; Method = other.Method;
        HashRefVa = other.HashRefVa; HashInBinary = other.HashInBinary; OutFile = other.OutFile;
    }
}

/// <summary>An asset entry that was not extracted, with the reason.
/// <para>未被提取的资源条目及其原因。</para></summary>
public sealed class SkippedAsset
{
    /// <summary>Name of the skipped asset. <para>被跳过资源的名称。</para></summary>
    /// <value>Asset name as recorded by the caller; <see cref="string.Empty"/> until set.
    /// <para>由调用方记录的资源名称；未设置时为空字符串。</para></value>
    public string Name { get; set; } = string.Empty;
    /// <summary>Why the asset was skipped. <para>资源被跳过的原因。</para></summary>
    /// <value>Free-form reason text written by the extraction caller; <see cref="string.Empty"/> until set.
    /// <para>由提取调用方写入的自由格式原因文本；未设置时为空字符串。</para></value>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// <para>汇总一次 KernelSU 资源提取的结果、跳过的条目与日志。</para>
/// Aggregates the result, skipped entries and log of a KernelSU asset extraction run.
/// </summary>
public sealed class Report
{
    /// <summary>Origin of the analyzed image. <para>被分析镜像的来源。</para></summary>
    /// <value>Path or identifier of the KernelSU module ELF being extracted; <see cref="string.Empty"/>
    /// until the caller fills it in. <para>正在提取的 KernelSU 模块 ELF 的路径或标识；调用方填写前为空字符串。</para></value>
    public string Source { get; set; } = string.Empty;
    /// <summary>Machine type of the analyzed ELF. <para>被分析 ELF 的机器类型。</para></summary>
    /// <value><c>e_machine</c> value: 183 for AArch64, 62 for x86-64.
    /// <para><c>e_machine</c> 值：183 为 AArch64，62 为 x86-64。</para></value>
    public ushort Machine { get; set; }
    /// <summary>Number of asset table entries discovered. <para>发现的资源表条目数量。</para></summary>
    /// <value>Total entries reported by the scan, regardless of extraction outcome; <c>0</c> until set.
    /// <para>扫描报告的全部条目数，与提取结果无关；未设置时为 <c>0</c>。</para></value>
    public int TotalEntries { get; set; }
    /// <summary>Number of assets successfully restored. <para>成功还原的资源数量。</para></summary>
    /// <value>Count of extracted assets; <c>0</c> until set. <para>已提取资源的数量；未设置时为 <c>0</c>。</para></value>
    public int Extracted { get; set; }
    /// <summary>Restored asset entries. <para>已还原的资源条目。</para></summary>
    /// <value>List populated from the extraction; empty when nothing was restored.
    /// <para>由提取过程填充的列表；没有还原结果时为空。</para></value>
    public List<Asset> Assets { get; set; } = new();
    /// <summary>Entries that were not extracted. <para>未被提取的条目。</para></summary>
    /// <value>Skipped assets with their reasons; empty until the caller records them.
    /// <para>被跳过的资源及其原因；调用方记录前为空。</para></value>
    public List<SkippedAsset> Skipped { get; set; } = new();
    /// <summary>Ordered log lines from the extraction run. <para>提取过程产生的有序日志行。</para></summary>
    /// <value>Diagnostic messages in emission order; empty when nothing was logged.
    /// <para>按产生顺序排列的诊断信息；没有记录时为空。</para></value>
    public List<string> Log { get; set; } = new();
}

/// <summary>
/// <para>解析 KernelSU 模块 ELF，定位并还原其中压缩打包的资源。</para>
/// Parses a KernelSU module ELF to locate and restore the compressed assets it embeds.
/// </summary>
public static partial class Extractor
{
    /// <summary>
    /// <para>在 AArch64 函数内扫描 ADRP/ADD 指令对（ADRP: 0x90000000，掩码 0x9F000000），收集解析后的页引用地址。</para>
    /// Scans an AArch64 function for ADRP/ADD instruction pairs (ADRP: 0x90000000, mask 0x9F000000) and collects the resolved page-reference addresses.
    /// </summary>
    /// <param name="elf">Image supplying the bytes and the PT_LOAD segment map.
    /// <para>提供字节与 PT_LOAD 段映射的镜像。</para></param>
    /// <param name="fnVa">Virtual address of the function to scan; an empty list is returned when it is
    /// unmapped. <para>待扫描函数的虚拟地址；未映射时返回空列表。</para></param>
    /// <param name="window">Maximum number of bytes read past the function start.
    /// <para>从函数起始处向后读取的最大字节数。</para></param>
    /// <returns>De-duplicated final addresses, in scan order, whose resolved value maps back into a
    /// PT_LOAD segment. <para>按扫描顺序去重、且换算结果能落回 PT_LOAD 段的最终地址。</para></returns>
    public static List<ulong> ScanAarch64Refs(ElfFile elf, ulong fnVa, int window = 0x1000)
    {
        byte[] data = elf.Data;
        long? fnOff = elf.VaToOff(fnVa);
        if (fnOff is null)
            return new List<ulong>();
        var refs = new List<ulong>();
        long end = Math.Min(data.Length - 3, fnOff.Value + window);
        long i = fnOff.Value;
        while (i + 4 <= end)
        {
            uint insn = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)i));
            if ((insn & 0x9F000000) == 0x90000000)
            {
                uint rd = insn & 0x1F;
                uint immlo = (insn >> 29) & 0x3;
                uint immhi = (insn >> 5) & 0x7FFFF;
                long imm21 = ((long)immhi << 2) | immlo;
                if ((imm21 & (1L << 20)) != 0)
                    imm21 -= 1L << 21;
                ulong pcVa = elf.OffToVa(i) ?? fnVa;
                long page = checked((long)(pcVa & ~0xFFFUL));
                long targetPage = checked(page + (imm21 << 12));
                if (targetPage < 0)
                    continue;
                ulong target = (ulong)targetPage;
                for (long j = i + 4; j <= Math.Min(end, i + 12); j += 4)
                {
                    uint a = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)j));
                    if ((a & 0xFF800000) == 0x91000000)
                    {
                        uint rn = (a >> 5) & 0x1F;
                        if (rn == rd)
                        {
                            uint imm12 = (a >> 10) & 0xFFF;
                            uint shift = (a >> 22) & 1;
                            ulong final = (target + ((ulong)imm12 << (shift == 1 ? 12 : 0))) & 0xFFFFFFFFFFFFFFFFUL;
                            long? ro = elf.ResolveRef(final);
                            ulong? rva = ro is not null ? elf.OffToVa(ro.Value) : null;
                            if (rva is not null)
                                refs.Add(final);
                            break;
                        }
                    }
                    else if ((a & 0x7F000000) is 0x91000000 or 0x93000000)
                    {
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            i += 4;
        }
        var seen = new HashSet<ulong>();
        var outRefs = new List<ulong>(refs.Count);
        foreach (ulong r in refs)
            if (seen.Add(r))
                outRefs.Add(r);
        return outRefs;
    }

    /// <summary>
    /// <para>在 x86-64 函数内扫描指向全局数据的 RIP+disp32 指令并收集解析后的目标地址。</para>
    /// Scans an x86-64 function for RIP+disp32 instructions that address global data and collects the resolved target addresses.
    /// </summary>
    /// <param name="elf">Image supplying the bytes and the PT_LOAD segment map.
    /// <para>提供字节与 PT_LOAD 段映射的镜像。</para></param>
    /// <param name="fnVa">Virtual address of the function to scan; an empty list is returned when it is
    /// unmapped. <para>待扫描函数的虚拟地址；未映射时返回空列表。</para></param>
    /// <param name="window">Maximum number of bytes read past the function start.
    /// <para>从函数起始处向后读取的最大字节数。</para></param>
    /// <returns>De-duplicated target addresses, in scan order, whose resolved value maps back into a
    /// PT_LOAD segment. <para>按扫描顺序去重、且换算结果能落回 PT_LOAD 段的目标地址。</para></returns>
    public static List<ulong> ScanX86_64Refs(ElfFile elf, ulong fnVa, int window = 0x1000)
    {
        byte[] data = elf.Data;
        long? fnOff = elf.VaToOff(fnVa);
        if (fnOff is null)
            return new List<ulong>();
        var refs = new List<ulong>();
        long end = Math.Min(data.Length - 7, fnOff.Value + window);
        long i = fnOff.Value;
        while (i + 7 <= end)
        {
            if (data[i] is 0x48 or 0x4C && data[i + 1] is 0x8D or 0x8B)
            {
                byte modrm = data[i + 2];
                if ((modrm & 0xC7) == 0x05)
                {
                    int disp = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)i + 3));
                    ulong nextVa = elf.OffToVa(i + 7) ?? (fnVa + (ulong)(i + 7 - (elf.VaToOff(fnVa) ?? 0)));
                    ulong target = (nextVa + (ulong)disp) & 0xFFFFFFFFFFFFFFFFUL;
                    long? ro = elf.ResolveRef(target);
                    ulong? rva = ro is not null ? elf.OffToVa(ro.Value) : null;
                    if (rva is not null)
                        refs.Add(target);
                    i += 7;
                    continue;
                }
            }
            i += 1;
        }
        var seen = new HashSet<ulong>();
        var outRefs = new List<ulong>(refs.Count);
        foreach (ulong r in refs)
            if (seen.Add(r))
                outRefs.Add(r);
        return outRefs;
    }

    /// <summary>
    /// <para>依据 ELF 机器类型选择对应的引用扫描实现。</para>
    /// Dispatches to the reference scanner matching the ELF machine type.
    /// </summary>
    /// <param name="elf">Image whose <c>e_machine</c> selects the scanner.
    /// <para>以 <c>e_machine</c> 决定扫描实现的镜像。</para></param>
    /// <param name="fnVa">Virtual address of the function to scan. <para>待扫描函数的虚拟地址。</para></param>
    /// <param name="window">Maximum number of bytes read past the function start.
    /// <para>从函数起始处向后读取的最大字节数。</para></param>
    /// <returns>The references collected by the selected scanner. <para>由所选扫描实现收集到的引用地址。</para></returns>
    /// <exception cref="InvalidOperationException"><c>elf.Machine</c> is neither 183 nor 62.
    /// <para><c>elf.Machine</c> 既不是 183 也不是 62。</para></exception>
    public static List<ulong> ScanRefs(ElfFile elf, ulong fnVa, int window = 0x1000)
    {
        return elf.Machine switch
        {
            183 => ScanAarch64Refs(elf, fnVa, window),
            62 => ScanX86_64Refs(elf, fnVa, window),
            _ => throw new InvalidOperationException($"Unsupported ELF machine type: {elf.Machine}"),
        };
    }

    /// <summary>
    /// <para>在 ELF 视图数据中查找 KernelSU 资源表条目，返回名称、工厂函数地址与表偏移。</para>
    /// Finds KernelSU asset table entries in the ELF view data, returning their name, factory function address and table offset.
    /// </summary>
    /// <param name="view">
    /// Bytes to scan, normally the relocation-patched view of the image.
    /// <para>待扫描的字节，通常为镜像经重定位修补后的视图。</para>
    /// </param>
    /// <param name="elf">Image supplying the address mapping used to read name strings and verify the
    /// function pointer. <para>提供地址换算以读取名称并校验函数指针的镜像。</para></param>
    /// <param name="maxNameLen">Largest accepted name-length field in an entry.
    /// <para>条目中可接受的名称长度字段上限。</para></param>
    /// <param name="minCluster">Minimum number of consecutive entries a cluster needs to keep; clusters
    /// are only filtered when doing so leaves at least one cluster.
    /// <para>簇需要保留的最小连续条目数；仅当过滤后仍有簇时才应用该过滤。</para></param>
    /// <returns>Unique entries sorted by table offset; empty when no entry passes the shape and name
    /// checks. <para>按表偏移排序的去重条目；没有条目通过形态与名称检查时为空。</para></returns>
    public static List<(string Name, ulong FnVa, long TableOff)> FindEntries(
        byte[] view, ElfFile elf, int maxNameLen = 256, int minCluster = 3)
    {
        var hits = new List<(long P, string Name, ulong Fn)>();
        for (long p = 0; p <= view.Length - 24; p += 8)
        {
            ulong v1 = BinaryPrimitives.ReadUInt64LittleEndian(view.AsSpan((int)p));
            ulong v2 = BinaryPrimitives.ReadUInt64LittleEndian(view.AsSpan((int)p + 8));
            ulong v3 = BinaryPrimitives.ReadUInt64LittleEndian(view.AsSpan((int)p + 16));
            long? noff = elf.VaToOff(v1);
            if (noff is null || elf.IsWritableVa(v1))
                continue;
            byte nb = view[noff.Value];
            if (nb < 0x20 || nb > 0x7E)
                continue;
            if (v2 < 1 || v2 > (ulong)maxNameLen)
                continue;
            if (!elf.IsExecVa(v3))
                continue;
            if (noff.Value + (long)v2 > view.Length)
                continue;
            bool printable = true;
            for (long k = noff.Value; k < noff.Value + (long)v2; k++)
            {
                byte b = view[k];
                if (b < 0x20 || b > 0x7E) { printable = false; break; }
            }
            if (!printable)
                continue;
            var name = ElfFile.EncodingLatin1.GetString(view, (int)noff.Value, (int)v2);
            if (!name.EndsWith("_kernelsu.ko", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Regex.IsMatch(name, @"(?i)(?:^|/)(?:android\d+(?:\.\d+)?[-_]\d+\.\d+|\d+\.\d+(?:\.\d+)?[-_]android\d+)_kernelsu\.ko$"))
                continue;
            hits.Add((p, name, v3));
        }

        var clusters = new List<List<(long P, string Name, ulong Fn)>>();
        var cur = new List<(long P, string Name, ulong Fn)>();
        foreach (var h in hits)
        {
            if (cur.Count > 0 && h.P == cur[^1].P + 24)
                cur.Add(h);
            else
            {
                if (cur.Count > 0)
                    clusters.Add(cur);
                cur = new List<(long P, string Name, ulong Fn)> { h };
            }
        }
        if (cur.Count > 0)
            clusters.Add(cur);
        var big = clusters.Where(c => c.Count >= minCluster).ToList();
        if (big.Count > 0)
            clusters = big;

        var entries = new List<(string, ulong, long)>();
        var seen = new HashSet<string>();
        foreach (var c in clusters)
            foreach (var (p, name, fn) in c)
                if (seen.Add(name))
                    entries.Add((name, fn, p));
        entries.Sort((a, b) => a.Item3.CompareTo(b.Item3));
        return entries;
    }

    /// <summary>
    /// <para>在给定静态地址附近扫描指向可执行代码的 64 位字，返回候选内部函数地址。</para>
    /// Scans for 64-bit words pointing into executable code near a given static address and returns candidate internal function addresses.
    /// </summary>
    /// <param name="view">Bytes to scan, normally the relocation-patched view of the image.
    /// <para>待扫描的字节，通常为镜像经重定位修补后的视图。</para></param>
    /// <param name="elf">Image supplying the PT_LOAD segment map. <para>提供 PT_LOAD 段映射的镜像。</para></param>
    /// <param name="staticVa">Virtual address to start from; an empty list is returned when it is unmapped.
    /// <para>起算的虚拟地址；未映射时返回空列表。</para></param>
    /// <param name="scanBytes">Number of bytes to read from the start address.
    /// <para>从起始地址读取的字节数。</para></param>
    /// <returns>Candidate addresses in scan order; each matching word is reported once and duplicates are
    /// not removed. <para>按扫描顺序返回的候选地址；每个命中的字只记录一次，不去重。</para></returns>
    public static List<ulong> FindStaticFns(byte[] view, ElfFile elf, ulong staticVa, int scanBytes = 0x40)
    {
        long? off = elf.VaToOff(staticVa);
        if (off is null)
            return new List<ulong>();
        long end = Math.Min(view.Length - 8, off.Value + scanBytes);
        var fns = new List<ulong>();
        for (long q = off.Value; q <= end; q += 8)
        {
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(view.AsSpan((int)q));
            if (elf.IsExecVa(v))
                fns.Add(v);
        }
        return fns;
    }

    private static byte[] ReadCapped(Stream s, int maxLen)
    {
        using var outMs = new MemoryStream();
        var buf = new byte[8192];
        int total = 0;
        while (total <= maxLen)
        {
            int n = s.Read(buf, 0, buf.Length);
            if (n <= 0)
                break;
            total += n;
            outMs.Write(buf, 0, n);
        }
        return outMs.ToArray();
    }

    private static int ProduceLength(byte[] data, int off, int len, int capOut)
    {
        try
        {
            using var ms = new MemoryStream(data, off, len);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            return ReadCapped(ds, capOut).Length;
        }
        catch (InvalidDataException)
        {
            return -1;
        }
    }

    /// <summary>
    /// <para>二分查找被 deflate 流实际消耗的输入字节数。</para>
    /// Binary-searches how many input bytes the deflate stream actually consumes.
    /// </summary>
    private static int FindConsumed(byte[] data, int off, int chunkLen, int outputLen, int capOut)
    {
        int lo = 0, hi = chunkLen;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (ProduceLength(data, off, mid, capOut) >= outputLen)
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// <para>尝试把 <paramref name="off" /> 处的数据当作 deflate 流解压，并返回消耗字节数与输出。</para>
    /// Attempts to decompress data at <paramref name="off" /> as a deflate stream and returns the consumed bytes and output.
    /// </summary>
    /// <param name="data">Buffer containing a candidate deflate stream.
    /// <para>包含候选 deflate 流的缓冲区。</para></param>
    /// <param name="off">Offset in <paramref name="data"/> where the stream may start.
    /// <para><paramref name="data"/> 中该流可能的起始位置。</para></param>
    /// <param name="cap">Maximum decompressed output size in bytes; streams producing more are rejected.
    /// <para>解压输出的最大字节数；超过上限的流会被拒绝。</para></param>
    /// <returns>The input bytes consumed and the decompressed output, or <see langword="null"/> when the
    /// region is empty, the stream is malformed, the output is empty or exceeds <paramref name="cap"/>,
    /// or no input bytes are consumed. <para>消耗的输入字节数与解压输出；当区域为空、流格式错误、输出为空或超过
    /// <paramref name="cap"/>、或未消耗任何输入字节时返回 <see langword="null"/>。</para></returns>
    public static (int Consumed, byte[] Output)? TryDeflate(byte[] data, int off, int cap = 32 * 1024 * 1024)
    {
        int chunkLen = Math.Min(cap, data.Length - off);
        if (chunkLen <= 0)
            return null;
        byte[] output;
        try
        {
            using var ms = new MemoryStream(data, off, chunkLen);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            output = ReadCapped(ds, cap + 1);
            if (output.Length > cap)
                return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        if (output.Length == 0)
            return null;
        int consumed = FindConsumed(data, off, chunkLen, output.Length, cap + 1);
        if (consumed <= 0)
            return null;
        return (consumed, output);
    }

    /// <summary>
    /// <para>返回指定资源条目对应位置的解压后字节内容。</para>
    /// Returns the decompressed byte content stored at the given asset's data offset.
    /// </summary>
    /// <param name="data">Image bytes (or patched view) containing the asset's deflate stream.
    /// <para>包含资源 deflate 流的镜像字节（或修补视图）。</para></param>
    /// <param name="asset">Entry whose <c>DataOff</c> locates the stream.
    /// <para>通过 <c>DataOff</c> 定位流的条目。</para></param>
    /// <returns>The decompressed payload, or an empty array when <c>DataOff</c> is <see langword="null"/>
    /// or the stream fails to decode. <para>解压后的数据；当 <c>DataOff</c> 为 <see langword="null"/> 或流解码失败时返回空数组。</para></returns>
    public static byte[] GetOutBytes(byte[] data, Asset asset)
    {
        if (asset.DataOff is null)
            return Array.Empty<byte>();
        var got = TryDeflate(data, asset.DataOff.Value);
        return got?.Output ?? Array.Empty<byte>();
    }

    /// <summary>
    /// <para>净化资源名称：把反斜杠归一化为正斜杠、去掉前导分隔符，再把其余分隔符与非法字符压平为下划线。</para>
    /// Sanitizes an asset name by normalizing backslashes to forward slashes, stripping leading separators, then
    /// flattening every remaining separator together with the other invalid characters into underscores.
    /// </summary>
    /// <param name="name">Raw asset name as stored in the table entry.
    /// <para>表中记录的资源原始名称。</para></param>
    /// <returns>A single flat file name: leading separators are removed and every remaining character outside
    /// <c>A-Za-z0-9._+@%-</c>, including any directory separator, is replaced by <c>_</c>. Because no separator
    /// survives, the result can name a file inside one directory but can never address a path outside it; the dots of
    /// a <c>..</c> segment are kept and simply lose their separators.
    /// <para>一个扁平的文件名：去掉前导分隔符，其余每个不在 <c>A-Za-z0-9._+@%-</c> 内的字符（含任何目录分隔符）
    /// 替换为 <c>_</c>。由于没有分隔符得以保留，结果只能命名某个目录内的文件，绝不能指向该目录之外的路径；
    /// <c>..</c> 段中的点会被保留，只是失去其分隔符。</para></returns>
    public static string SanitizeName(string name)
    {
        name = name.Replace('\\', '/').TrimStart('/');
        return InvalidCharsRegex().Replace(name, "_");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[^A-Za-z0-9._+@%\-]")]
    private static partial System.Text.RegularExpressions.Regex InvalidCharsRegex();

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));

    private static IEnumerable<byte[]> EnumerateX86InlineHashes(ElfFile elf, ulong fnVa, int window)
    {
        long? start = elf.VaToOff(fnVa);
        if (start is null)
            yield break;
        var blocks = new List<byte[]>(2);
        long end = Math.Min(elf.Data.Length - 7L, start.Value + Math.Max(window, 0x80));
        for (long offset = start.Value; offset <= end; offset++)
        {
            byte op0 = elf.Data[offset];
            byte op1 = elf.Data[offset + 1];
            if (op0 != 0x0F || (op1 != 0x28 && op1 != 0x10) || elf.Data[offset + 2] != 0x05)
                continue;
            int displacement = BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)offset + 3));
            ulong nextVa = elf.OffToVa(offset + 7) ?? 0;
            if (nextVa == 0)
                continue;
            ulong target = unchecked(nextVa + (ulong)displacement);
            long? targetOffset = elf.ResolveRef(target);
            if (targetOffset is null || targetOffset.Value < 0 || targetOffset.Value + 16 > elf.Data.Length)
                continue;
            blocks.Add(elf.Data.AsSpan((int)targetOffset.Value, 16).ToArray());
            if (blocks.Count == 2)
            {
                byte[] digest = new byte[32];
                blocks[0].CopyTo(digest, 0);
                blocks[1].CopyTo(digest, 16);
                yield return digest;
                yield break;
            }
        }
    }

    private static IEnumerable<byte[]> EnumerateInlineHashes(ElfFile elf, ulong fnVa, int window)
    {
        if (elf.Machine != 183)
            yield break;
        long? start = elf.VaToOff(fnVa);
        if (start is null)
            yield break;

        ulong x8 = 0, x9 = 0;
        int mask8 = 0, mask9 = 0;
        ulong? firstHashWord8 = null, firstHashWord9 = null;
        long end = Math.Min(elf.Data.Length - 4L, start.Value + Math.Max(window, 0x80));
        for (long offset = start.Value; offset <= end; offset += 4)
        {
            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(elf.Data.AsSpan((int)offset));
            uint opcode = instruction & 0xFF800000u;
            if (opcode is 0xD2800000u or 0xF2800000u or 0x52800000u or 0x72800000u)
            {
                int registerIndex = (int)(instruction & 0x1Fu);
                if (registerIndex is 8 or 9)
                {
                    int halfword = (int)((instruction >> 21) & 0x3u);
                    ulong value = (ulong)((instruction >> 5) & 0xFFFFu) << (halfword * 16);
                    int bit = 1 << halfword;
                    bool moveWide = opcode is 0xD2800000u or 0x52800000u;
                    if (registerIndex == 8)
                    {
                        if (moveWide) x8 = value; else x8 = (x8 & ~((ulong)0xFFFF << (halfword * 16))) | value;
                        mask8 = moveWide ? bit : mask8 | bit;
                    }
                    else
                    {
                        if (moveWide) x9 = value; else x9 = (x9 & ~((ulong)0xFFFF << (halfword * 16))) | value;
                        mask9 = moveWide ? bit : mask9 | bit;
                    }
                }
            }

            if ((instruction & 0xFFC00000u) == 0xA9000000u &&
                (instruction & 0x1Fu) == 8u && ((instruction >> 10) & 0x1Fu) == 9u &&
                ((instruction >> 5) & 0x1Fu) == 19u)
            {
                int scaled = (int)((instruction >> 15) & 0x7Fu);
                if ((scaled & 0x40) != 0) scaled -= 0x80;
                int byteOffset = scaled * 8;
                if (byteOffset == 0x20 && mask8 == 0xF && mask9 == 0xF)
                {
                    firstHashWord8 = x8;
                    firstHashWord9 = x9;
                }
            }

            if ((instruction & 0xFC000000u) == 0x94000000u &&
                firstHashWord8 is not null && firstHashWord9 is not null &&
                mask8 == 0xF && mask9 == 0xF && (x8 != firstHashWord8 || x9 != firstHashWord9))
            {
                byte[] digest = new byte[32];
                BinaryPrimitives.WriteUInt64LittleEndian(digest.AsSpan(0, 8), firstHashWord8.Value);
                BinaryPrimitives.WriteUInt64LittleEndian(digest.AsSpan(8, 8), firstHashWord9.Value);
                BinaryPrimitives.WriteUInt64LittleEndian(digest.AsSpan(16, 8), x8);
                BinaryPrimitives.WriteUInt64LittleEndian(digest.AsSpan(24, 8), x9);
                yield return digest;
                firstHashWord8 = null;
                firstHashWord9 = null;
            }
        }
    }

    /// <summary>
    /// <para>从 KernelSU 模块 ELF 中提取全部资源条目，返回资源列表与日志。</para>
    /// Extracts every asset entry from a KernelSU module ELF and returns the asset list and log.
    /// </summary>
    /// <param name="elf">KernelSU module ELF to analyze; its data is kept read-only.
    /// <para>待分析的 KernelSU 模块 ELF；其数据保持只读。</para></param>
    /// <param name="verbose">When <see langword="true"/>, adds one log line listing every discovered table
    /// entry. <para>为 <see langword="true"/> 时在日志中追加一行，列出全部发现到的表条目。</para></param>
    /// <returns>One <see cref="Asset"/> per table entry — entries whose payload (at least 4096
    /// decompressed bytes) cannot be located keep <c>DataOff</c> <see langword="null"/> — plus the log
    /// lines in emission order. <para>每个表条目对应一个 <see cref="Asset"/>——无法定位数据（解压后不少于 4096 字节）
    /// 的条目其 <c>DataOff</c> 保持 <see langword="null"/>——外加按产生顺序排列的日志行。</para></returns>
    /// <example>
    /// <code>
    /// var elf = new ElfFile(File.ReadAllBytes("libKSU_apc64.so"));
    /// var (assets, log) = Extractor.ExtractAssets(elf);
    /// </code>
    /// </example>
    public static (List<Asset> Assets, List<string> Log) ExtractAssets(
        ElfFile elf, bool verbose = false)
    {
        byte[] view = elf.PatchView() ?? elf.Data;
        byte[] data = view;
        var log = new List<string>();
        var entries = FindEntries(view, elf);
        if (verbose)
            log.Add($"ENTRIES: {entries.Count} [{string.Join(", ", entries.Select(e => e.Name))}]");

        var assets = new List<Asset>();
        var entryFns = entries.Select(e => e.FnVa).OrderBy(f => f).ToList();
        foreach (var (name, fnVa, tableOff) in entries)
        {
            ulong nxt = entryFns.FirstOrDefault(f => f > fnVa);
            if (nxt == 0)
                nxt = fnVa + 0x200;
            int extWindow = (int)Math.Min(0x400UL, nxt - fnVa);
            var refs = ScanRefs(elf, fnVa, extWindow);
            var hashRefs = refs.Where(r => !elf.IsWritableVa(r)).ToList();
            var dataRefs = refs.Where(elf.IsWritableVa).ToList();

            var result = new Asset { Name = name, FnVa = fnVa, TableOff = tableOff };

            foreach (var r in dataRefs)
            {
                foreach (var thunk in FindStaticFns(view, elf, r))
                {
                    foreach (var r2 in ScanRefs(elf, thunk, 0x40))
                    {
                        long? off2 = elf.ResolveRef(r2);
                        if (off2 is null)
                            continue;
                        var got = TryDeflate(data, (int)off2.Value);
                        if (got is not null && got.Value.Output.Length >= 4096)
                        {
                            result.DataOff = (int)off2.Value;
                            result.CompLen = got.Value.Consumed;
                            result.Size = got.Value.Output.Length;
                            result.Sha256 = Sha256Hex(got.Value.Output);
                            result.Method = "entry-factory";
                            break;
                        }
                    }
                    if (result.DataOff is not null)
                        break;
                }
                if (result.DataOff is not null)
                    break;
            }

            if (result.DataOff is null)
            {
                foreach (var r in refs)
                {
                    long? off = elf.ResolveRef(r);
                    if (off is null)
                        continue;
                    var got = TryDeflate(data, (int)off.Value);
                    if (got is not null && got.Value.Output.Length >= 4096)
                    {
                        result.DataOff = (int)off.Value;
                        result.CompLen = got.Value.Consumed;
                        result.Size = got.Value.Output.Length;
                        result.Sha256 = Sha256Hex(got.Value.Output);
                        result.Method = "entry-ref";
                        break;
                    }
                }
            }

            {
                ulong? hashHit = null;
                foreach (var hr in hashRefs)
                {
                    long? hoff = elf.ResolveRef(hr);
                    if (hoff is not null && hoff + 32 <= data.Length &&
                        Convert.ToHexStringLower(data.AsSpan((int)hoff.Value, 32)) == result.Sha256)
                    {
                        hashHit = hr;
                        break;
                    }
                }
                result.HashRefVa = hashHit;
                bool inlineHash = !string.IsNullOrEmpty(result.Sha256) &&
                    (EnumerateInlineHashes(elf, fnVa, extWindow)
                        .Concat(elf.Machine == 62 ? EnumerateX86InlineHashes(elf, fnVa, extWindow) : [])
                        .Any(candidate => Convert.ToHexStringLower(candidate) == result.Sha256));
                result.HashInBinary = hashHit is not null || inlineHash;
                byte[] outBytes = GetOutBytes(data, result);
                result.Verified =
                    result.HashInBinary &&
                    outBytes.Length >= 4 &&
                    outBytes[0] == 0x7F && outBytes[1] == (byte)'E' &&
                    outBytes[2] == (byte)'L' && outBytes[3] == (byte)'F';
            }
            assets.Add(result);
        }

        return (assets, log);
    }
}