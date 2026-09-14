using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher.Ksud;

/// <summary>KernelSU ELF extraction and patching details.</summary>
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

    /// <summary>KernelSU ELF extraction and patching details.</summary>
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

/// <summary>KernelSU ELF extraction and patching details.</summary>
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
/// <summary>KernelSU ELF extraction and patching details.</summary>
/// <summary>KernelSU ELF extraction and patching details.</summary>
public static partial class Extractor
{
    // Binary parsing and patching implementation detail.

    /// <summary>
    /// <summary>KernelSU ELF extraction and patching details.</summary>
    ///   ADRP  Xd, label        (0x90000000 / mask 0x9F000000)
    /// <summary>KernelSU ELF extraction and patching details.</summary>
    /// <summary>KernelSU ELF extraction and patching details.</summary>
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
                int imm = (int)((immhi << 2) | immlo);
                if ((imm & 0x80000) != 0)
                    imm -= 0x100000;
                ulong pcVa = elf.OffToVa(i) ?? fnVa;
                ulong page = (pcVa >> 12) << 12;
                ulong target = (page + ((ulong)imm << 12)) & 0xFFFFFFFFFFFFFFFFUL;
                // Binary parsing and patching implementation detail.
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
                            if (rva is not null && !elf.IsExecVa(rva.Value))
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
    /// <summary>KernelSU ELF extraction and patching details.</summary>
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
                    if (rva is not null && !elf.IsExecVa(rva.Value))
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

    /// <summary>KernelSU ELF extraction and patching details.</summary>
    public static List<ulong> ScanRefs(ElfFile elf, ulong fnVa, int window = 0x1000)
    {
        return elf.Machine switch
        {
            183 => ScanAarch64Refs(elf, fnVa, window),           // EM_AARCH64
            62 => ScanX86_64Refs(elf, fnVa, window),             // EM_X86_64
            _ => throw new InvalidOperationException($"Unsupported ELF machine type: {elf.Machine}"),
        };
    }

    // Binary parsing and patching implementation detail.

    /// <summary>
    /// <summary>KernelSU ELF extraction and patching details.</summary>
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
            // rust-embed tables can sit next to unrelated (&str, value) data.
            // Keep only names that are provably KernelSU module assets.
            if (!name.EndsWith("_kernelsu.ko", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Regex.IsMatch(name, @"(?i)(?:^|/)(?:android\d+(?:\.\d+)?[-_]\d+\.\d+|\d+\.\d+(?:\.\d+)?[-_]android\d+)_kernelsu\.ko$"))
                continue;
            hits.Add((p, name, v3));
        }

        // Binary parsing and patching implementation detail.
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

    // Binary parsing and patching implementation detail.

    /// <summary>
    /// <summary>KernelSU ELF extraction and patching details.</summary>
    /// <summary>KernelSU ELF extraction and patching details.</summary>
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

    // Binary parsing and patching implementation detail.

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

    /// <summary>KernelSU ELF extraction and patching details.</summary>
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
    /// <summary>KernelSU ELF extraction and patching details.</summary>
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

    // Binary parsing and patching implementation detail.

    /// <summary>KernelSU ELF extraction and patching details.</summary>
    public static byte[] GetOutBytes(byte[] data, Asset asset)
    {
        if (asset.DataOff is null)
            return Array.Empty<byte>();
        var got = TryDeflate(data, asset.DataOff.Value);
        return got?.Output ?? Array.Empty<byte>();
    }

    /// <summary>KernelSU ELF extraction and patching details.</summary>
    public static string SanitizeName(string name)
    {
        name = name.Replace('\\', '/').TrimStart('/');
        return InvalidCharsRegex().Replace(name, "_");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[^A-Za-z0-9._+@%\-]")]
    private static partial System.Text.RegularExpressions.Regex InvalidCharsRegex();

    // Binary parsing and patching implementation detail.

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));



    /// <summary>
    /// <summary>KernelSU ELF extraction and patching details.</summary>
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
            // Binary parsing and patching implementation detail.
            ulong nxt = entryFns.FirstOrDefault(f => f > fnVa);
            if (nxt == 0)
                nxt = fnVa + 0x200;
            int extWindow = (int)Math.Min(0x400UL, nxt - fnVa);
            var refs = ScanRefs(elf, fnVa, extWindow);
            var hashRefs = refs.Where(r => !elf.IsWritableVa(r)).ToList();
            var dataRefs = refs.Where(elf.IsWritableVa).ToList();

            var result = new Asset { Name = name, FnVa = fnVa, TableOff = tableOff };

            // Binary parsing and patching implementation detail.
            // Binary parsing and patching implementation detail.
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

            // Binary parsing and patching implementation detail.
            // Binary parsing and patching implementation detail.
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

            // Binary parsing and patching implementation detail.

            // Binary parsing and patching implementation detail.
            {
                byte[] hb = Convert.FromHexString(result.Sha256);
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
                // A digest merely occurring somewhere in the ELF is not enough
                // to associate it with this resource. It must be reached by a
                // reference from the same resource factory.
                result.HashInBinary = false;
                byte[] outBytes = GetOutBytes(data, result);
                result.Verified =
                    hashHit is not null &&
                    outBytes.Length >= 4 &&
                    outBytes[0] == 0x7F && outBytes[1] == (byte)'E' &&
                    outBytes[2] == (byte)'L' && outBytes[3] == (byte)'F';
            }
            assets.Add(result);
        }

        return (assets, log);
    }
}