using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher.Ksud;

/// <summary>
/// <para>表示从 KernelSU 模块 ELF 中定位到的一个资源条目及其还原结果。</para>
/// Represents a single asset located in the KernelSU module ELF together with its restored result.
/// </summary>
public sealed class Asset
{
    public string Name { get; set; } = string.Empty;
    public ulong FnVa { get; set; }
    public long TableOff { get; set; }
    public int? DataOff { get; set; }
    public int? CompLen { get; set; }
    public int? Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public string Method { get; set; } = string.Empty;
    public ulong? HashRefVa { get; set; }
    public bool HashInBinary { get; set; }
    public string OutFile { get; set; } = string.Empty;

    public Asset() { }

    /// <summary>
    /// <para>复制另一个资源条目的全部字段。</para>
    /// Copies all fields of another asset entry.
    /// </summary>
    public Asset(Asset other)
    {
        Name = other.Name; FnVa = other.FnVa; TableOff = other.TableOff;
        DataOff = other.DataOff; CompLen = other.CompLen; Size = other.Size;
        Sha256 = other.Sha256; Verified = other.Verified; Method = other.Method;
        HashRefVa = other.HashRefVa; HashInBinary = other.HashInBinary; OutFile = other.OutFile;
    }
}

public sealed class SkippedAsset
{
    public string Name { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// <para>汇总一次 KernelSU 资源提取的结果、跳过的条目与日志。</para>
/// Aggregates the result, skipped entries and log of a KernelSU asset extraction run.
/// </summary>
public sealed class Report
{
    public string Source { get; set; } = string.Empty;
    public ushort Machine { get; set; }
    public int TotalEntries { get; set; }
    public int Extracted { get; set; }
    public List<Asset> Assets { get; set; } = new();
    public List<SkippedAsset> Skipped { get; set; } = new();
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
            if ((insn & 0x9F000000) == 0x90000000)               // ADRP
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
                    if ((a & 0xFF800000) == 0x91000000)          // ADD (immediate, 64-bit)
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
                if ((modrm & 0xC7) == 0x05)                      // mod=00, rm=101 -> RIP+disp32
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
    public static List<ulong> ScanRefs(ElfFile elf, ulong fnVa, int window = 0x1000)
    {
        return elf.Machine switch
        {
            183 => ScanAarch64Refs(elf, fnVa, window),           // EM_AARCH64
            62 => ScanX86_64Refs(elf, fnVa, window),             // EM_X86_64
            _ => throw new InvalidOperationException($"Unsupported ELF machine type: {elf.Machine}"),
        };
    }

    /// <summary>
    /// <para>在 ELF 视图数据中查找 KernelSU 资源表条目，返回名称、工厂函数地址与表偏移。</para>
    /// Finds KernelSU asset table entries in the ELF view data, returning their name, factory function address and table offset.
    /// </summary>
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
    public static byte[] GetOutBytes(byte[] data, Asset asset)
    {
        if (asset.DataOff is null)
            return Array.Empty<byte>();
        var got = TryDeflate(data, asset.DataOff.Value);
        return got?.Output ?? Array.Empty<byte>();
    }

    /// <summary>
    /// <para>净化资源名称：把反斜杠归一化为正斜杠、去掉路径前缀并移除无效字符。</para>
    /// Sanitizes an asset name by normalizing backslashes to forward slashes, stripping path prefixes and removing invalid characters.
    /// </summary>
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