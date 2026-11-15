using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Obfuscation;

/// <summary>
/// One detected encrypted-string candidate with the identified scheme, recovered key and preview.
/// <para>一个检测到的加密字符串候选，含识别出的方案、恢复的密钥与还原预览。</para></summary>
public sealed record EncryptedStringCandidate
{
    /// <summary>Index of the string in the pool. <para>字符串在池中的索引。</para></summary>
    public int StringIndex { get; init; }

    /// <summary>The original pool string. <para>池中的原始字符串。</para></summary>
    public string Original { get; init; } = string.Empty;

    /// <summary>Identified scheme: <c>base64</c>, <c>xor1</c> (single-byte key) or <c>xorN</c> (N-byte repeating key).
    /// <para>识别出的方案：<c>base64</c>、<c>xor1</c>（单字节密钥）或 <c>xorN</c>（N 字节循环密钥）。</para></summary>
    public string Scheme { get; init; } = string.Empty;

    /// <summary>Hex-encoded recovered key; empty for base64. <para>恢复密钥的十六进制；base64 为空。</para></summary>
    public string KeyHex { get; init; } = string.Empty;

    /// <summary>The decrypted preview (printable-safe). <para>解密预览（仅可打印字符）。</para></summary>
    public string Decrypted { get; init; } = string.Empty;

    /// <summary>How many <c>const-string</c> instructions reference this string.
    /// <para>引用该字符串的 <c>const-string</c> 指令数量。</para></summary>
    public int InstructionSites { get; init; }
}

/// <summary>
/// The analysis outcome. <para>分析结果。</para></summary>
public sealed class DexStringDecryptReport
{
    /// <summary>Candidates ordered by string index. <para>按字符串索引排序的候选。</para></summary>
    public IReadOnlyList<EncryptedStringCandidate> Candidates { get; init; } = [];

    /// <summary>How many const-string operands were rewritten when the caller requested a rewrite.
    /// <para>调用方要求改写时被改写的 const-string 操作数数量。</para></summary>
    public int RewrittenSites { get; init; }

    /// <summary>Whether the model was rewritten. <para>模型是否被改写。</para></summary>
    public bool Rewritten { get; init; }
}

/// <summary>
/// Detects and (optionally) rewrites commonly encrypted string constants inside a DEX pool.
/// Recognized patterns: Base64-shaped strings and XOR encryption with a single-byte or short
/// repeating key, scored by the printable ratio of the recovered bytes. Schemes that need code
/// emulation (AES with keys woven into methods, custom packers) are out of scope by design and are
/// simply not reported.
/// <para>检测并可选改写 DEX 池中常见的加密字符串常量。识别模式：Base64 形状字符串与单字节/短循环密钥
/// 的 XOR 加密（按还原字节的可打印率评分）。需要代码模拟的方案（密钥织入方法的 AES、自定义加固）明确不在
/// 支持范围内，不做报告。</para>
/// </summary>
public static class DexStringDecryptAnalyzer
{
    private const double MinimumPrintableRatio = 0.92;
    private static readonly int[] KeyLengths = [2, 3, 4, 5, 6, 7, 8];
    private static readonly string[] Cribs = ["Ljava/lang/", "Ljava/lang/String;", "http://", "https://", "Ljava/io/", "java.lang.", "android.", "Exception", "content://"];

    /// <summary>Analyzes the string pool of the model.
    /// <para>分析模型的字符串池。</para></summary>
    /// <param name="dex">Model to analyze (not modified). <para>被分析的模型（不被修改）。</para></param>
    /// <returns>The candidate list. <para>候选列表。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is <see langword="null"/>.
    /// <para><paramref name="dex"/> 为 <see langword="null"/>。</para></exception>
    public static IReadOnlyList<EncryptedStringCandidate> Analyze(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        var siteCounts = CountStringSites(dex);
        var candidates = new List<EncryptedStringCandidate>();
        for (var i = 0; i < dex.Strings.Count; i++)
        {
            var value = dex.Strings[i];
            if (value.Length < 8 || value.Length > 4096) continue;
            if (TryBase64(value) is { } decoded)
            {
                candidates.Add(new EncryptedStringCandidate
                {
                    StringIndex = i,
                    Original = value,
                    Scheme = "base64",
                    Decrypted = Preview(decoded),
                    InstructionSites = siteCounts.TryGetValue(i, out var sites) ? sites : 0
                });
                continue;
            }
            if (TryXor(value) is { } hit)
            {
                candidates.Add(new EncryptedStringCandidate
                {
                    StringIndex = i,
                    Original = value,
                    Scheme = hit.scheme,
                    KeyHex = hit.key,
                    Decrypted = Preview(hit.plain),
                    InstructionSites = siteCounts.TryGetValue(i, out var sites) ? sites : 0
                });
            }
        }
        return candidates;
    }

    /// <summary>Optionally rewrites the model: detected XOR/Base64 candidates become their decrypted text, and every const-string operand pointing at them is repointed.
    /// <para>可选改写模型：检测出的 XOR/Base64 候选替换为其解密文本，指向它们的 const-string 操作数一并重指。</para></summary>
    /// <param name="dex">Model to modify in place. <para>就地修改的模型。</para></param>
    /// <param name="candidateLimit">Maximum candidates to rewrite, smallest string index first.
    /// <para>最多改写的候选数，按字符串索引从小到大。</para></param>
    /// <returns>The rewrite report. <para>改写报告。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is <see langword="null"/>.
    /// <para><paramref name="dex"/> 为 <see langword="null"/>。</para></exception>
    public static DexStringDecryptReport Rewrite(DexFile dex, int candidateLimit = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(dex);
        var candidates = Analyze(dex).Take(candidateLimit).ToDictionary(c => c.StringIndex);
        if (candidates.Count == 0)
        {
            return new DexStringDecryptReport { Candidates = Analyze(dex) };
        }

        var replacements = new Dictionary<int, string>();
        foreach (var candidate in candidates.Values)
        {
            var plain = candidate.Scheme switch
            {
                "base64" => TryBase64(candidate.Original) is { } decoded ? Encoding.ASCII.GetString(decoded) : null,
                "xor1" or "xor2" or "xor3" or "xor4" or "xor5" or "xor6" or "xor7" or "xor8" => TryXor(candidate.Original)?.plain is { } xored ? Encoding.ASCII.GetString(xored) : null,
                _ => null
            };
            if (plain is not null)
            {
                replacements[candidate.StringIndex] = plain;
            }
        }
        if (replacements.Count == 0)
        {
            return new DexStringDecryptReport { Candidates = Analyze(dex) };
        }

        var allocation = new Dictionary<int, int>();
        foreach (var (index, plain) in replacements)
        {
            allocation[index] = AllocateString(dex, plain);
        }

        var rewrittenSites = 0;
        foreach (var cls in dex.Classes)
        {
            foreach (var method in cls.AllMethods)
            {
                var code = method.Code;
                if (code is null) continue;
                rewrittenSites += RewriteConstStrings(code, allocation);
            }
        }
        return new DexStringDecryptReport
        {
            Candidates = Analyze(dex),
            Rewritten = true,
            RewrittenSites = rewrittenSites
        };
    }

    /// <summary>Counts how many const-string instructions reference each pool string.
    /// <para>统计每条池字符串被 const-string 指令引用的次数。</para></summary>
    private static Dictionary<int, int> CountStringSites(DexFile dex)
    {
        var counts = new Dictionary<int, int>();
        foreach (var cls in dex.Classes)
        {
            foreach (var method in cls.AllMethods)
            {
                var code = method.Code;
                if (code is null) continue;
                var insns = code.RawInsns;
                var i = 0;
                while (i < insns.Length)
                {
                    var unit = insns[i];
                    if (unit is 0x0100 or 0x0200 or 0x0300)
                    {
                        i += PayloadSize(insns, i);
                        continue;
                    }
                    var opcode = (byte)unit;
                    if (opcode == 0x1A && i + 1 < insns.Length)
                    {
                        var index = insns[i + 1];
                        counts.TryGetValue(index, out var current);
                        counts[index] = current + 1;
                    }
                    else if (opcode == 0x1B && i + 2 < insns.Length)
                    {
                        var index = (int)(insns[i + 1] | ((uint)insns[i + 2] << 16));
                        counts.TryGetValue(index, out var current);
                        counts[index] = current + 1;
                    }
                    i += OpCodes.GetSize(opcode);
                }
            }
        }
        return counts;
    }

    /// <summary>Repoints const-string operands that reference replaced strings; returns how many sites changed.
    /// <para>重指引用被替换字符串的 const-string 操作数；返回改变的位置数。</para></summary>
    private static int RewriteConstStrings(CodeItem code, Dictionary<int, int> allocation)
    {
        var insns = code.RawInsns;
        var changed = 0;
        var i = 0;
        while (i < insns.Length)
        {
            var unit = insns[i];
            if (unit is 0x0100 or 0x0200 or 0x0300)
            {
                i += PayloadSize(insns, i);
                continue;
            }
            var opcode = (byte)unit;
            if (opcode == 0x1A && i + 1 < insns.Length && allocation.TryGetValue(insns[i + 1], out var replacement))
            {
                insns[i + 1] = (ushort)replacement;
                changed++;
            }
            else if (opcode == 0x1B && i + 2 < insns.Length)
            {
                var index = (int)(insns[i + 1] | ((uint)insns[i + 2] << 16));
                if (allocation.TryGetValue(index, out var jumboReplacement) && jumboReplacement <= 0xFFFF)
                {
                    insns[i + 1] = (ushort)jumboReplacement;
                    insns[i + 2] = 0;
                    changed++;
                }
            }
            i += OpCodes.GetSize(opcode);
        }
        return changed;
    }

    /// <summary>Computes the payload span in code units, mirroring the decoder's per-kind layouts and falling back to one unit on malformed headers.
    /// <para>按解码器的各类布局计算载荷占用的代码单元数；头部畸形时回退为 1。</para></summary>
    private static int PayloadSize(ushort[] insns, int position)
    {
        if (position + 1 >= insns.Length) return 1;
        long required;
        switch (insns[position])
        {
            case 0x0100:
                required = 4L + insns[position + 1] * 2L;
                break;
            case 0x0200:
                required = 2L + insns[position + 1] * 4L;
                break;
            case 0x0300:
                if (position + 3 >= insns.Length) return 1;
                var count = (uint)insns[position + 2] | ((uint)insns[position + 3] << 16);
                required = 4L + (insns[position + 1] * (long)count + 1L) / 2L;
                break;
            default:
                return 1;
        }
        return required > 0 && required <= insns.Length - position ? (int)required : 1;
    }

    /// <summary>Decodes a Base64-shaped string whose bytes are mostly printable.
    /// <para>解码 Base64 形状且还原字节大多可打印的字符串。</para></summary>
    private static byte[]? TryBase64(string value)
    {
        if (value.Length < 8 || value.Length % 4 != 0) return null;
        foreach (var ch in value)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=')) return null;
        }
        try
        {
            var decoded = Convert.FromBase64String(value);
            return PrintableRatio(decoded) >= MinimumPrintableRatio ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Tries single-byte and short repeating XOR keys, scoring by printable ratio.
    /// <para>尝试单字节与短循环 XOR 密钥，按可打印率评分。</para></summary>
    private static (string scheme, string key, byte[] plain)? TryXor(string value)
    {
        var bytes = new byte[value.Length];
        var usable = true;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] > 0xFF)
            {
                usable = false;
                break;
            }
            bytes[i] = (byte)value[i];
        }
        if (!usable || bytes.Length < 8) return null;

        for (var key = 1; key <= 0xFF; key++)
        {
            var plain = Xor(bytes, [(byte)key]);
            if (PrintableRatio(plain) >= MinimumPrintableRatio && LooksLikeText(plain))
            {
                return ("xor1", key.ToString("X2"), plain);
            }
        }

        foreach (var length in KeyLengths)
        {
            var recovered = RecoverKeyByCrib(bytes, length);
            if (recovered is null) continue;
            var plain = Xor(bytes, recovered);
            if (PrintableRatio(plain) >= MinimumPrintableRatio && LooksLikeText(plain))
            {
                var hex = Convert.ToHexString(recovered);
                return ($"xor{length}", hex, plain);
            }
        }
        return null;
    }

    /// <summary>Recovers a repeating key by deriving it from one crib occurrence and validating at least two crib hits; returns null when no derivation fits.
    /// <para>由一个 crib 命中推导循环密钥并验证至少两个 crib 命中；推导不成立时返回 null。</para></summary>
    private static byte[]? RecoverKeyByCrib(byte[] encrypted, int keyLength)
    {
        foreach (var crib in Cribs)
        {
            var cribBytes = Encoding.ASCII.GetBytes(crib);
            if (cribBytes.Length < keyLength) continue;
            for (var offset = 0; offset + cribBytes.Length <= encrypted.Length; offset++)
            {
                var key = new byte[keyLength];
                for (var i = 0; i < keyLength; i++)
                {
                    key[i] = (byte)(encrypted[offset + i] ^ cribBytes[i]);
                }
                if (ValidatesAgainstCribs(encrypted, key)) return key;
            }
        }
        return null;
    }

    /// <summary>Counts how many distinct cribs decrypt correctly under the key; two independent hits are required so a single coincidental alignment cannot fabricate a key.
    /// <para>统计在该密钥下正确解密的独立 crib 数；要求两个命中以防单次巧合对齐伪造密钥。</para></summary>
    private static bool ValidatesAgainstCribs(byte[] encrypted, byte[] key)
    {
        var hits = 0;
        foreach (var crib in Cribs)
        {
            var cribBytes = Encoding.ASCII.GetBytes(crib);
            for (var offset = 0; offset + cribBytes.Length <= encrypted.Length; offset++)
            {
                var matches = true;
                for (var i = 0; i < cribBytes.Length; i++)
                {
                    if ((byte)(encrypted[offset + i] ^ key[(offset + i) % key.Length]) != cribBytes[i])
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches)
                {
                    hits++;
                    break;
                }
            }
        }
        return hits >= 2;
    }

    /// <summary>XORs the bytes with the repeating key. <para>用循环密钥 XOR 字节。</para></summary>
    private static byte[] Xor(byte[] input, byte[] key)
    {
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = (byte)(input[i] ^ key[i % key.Length]);
        }
        return output;
    }

    /// <summary>Printable ASCII/newline/tab ratio. <para>可打印 ASCII/换行/制表占比。</para></summary>
    private static double PrintableRatio(byte[] bytes)
    {
        var printable = 0;
        foreach (var b in bytes)
        {
            if (b is 0x0A or 0x0D or 0x09 or (>= 0x20 and <= 0x7E))
            {
                printable++;
            }
        }
        return bytes.Length == 0 ? 0 : (double)printable / bytes.Length;
    }

    /// <summary>Requires at least one crib substring so pure numeric/comma noise stays out.
    /// <para>要求至少含一个常见子串，避免纯数字/逗号噪声。</para></summary>
    private static bool LooksLikeText(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        return Cribs.Any(text.Contains);
    }

    /// <summary>Allocates the decrypted string and returns its pool index.
    /// <para>分配解密字符串并返回池索引。</para></summary>
    private static int AllocateString(DexFile dex, string value)
    {
        var existing = dex.Strings.IndexOf(value);
        if (existing >= 0) return existing;
        dex.Strings.Add(value);
        return dex.Strings.Count - 1;
    }

    /// <summary>Reduces the decrypted bytes to a printable-safe preview.
    /// <para>把解密字节收敛为可打印安全的预览。</para></summary>
    private static string Preview(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            builder.Append(b is (>= 0x20 and <= 0x7E) ? (char)b : '.');
        }
        return builder.ToString();
    }
}