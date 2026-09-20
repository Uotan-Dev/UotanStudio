using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.SharedKernel.Magic;

/// <summary>
/// <para>编译型魔数引擎：解码生成器产出的规则表并对文件字节执行匹配。取代旧的运行时文本解析引擎。</para>
/// Compiled magic engine: decodes the generator's rule tables and matches file bytes. Replaces the old
/// runtime text-parsing engine.
/// </summary>
public static class CompiledMagicEngine
{
    // Backstop against stack overflow from malformed/self-nesting rule trees (libmagic caps nesting too).
    private const int MaxDepth = 256;

    /// <summary>诊断开关：记录每次 best 改进的顶层命中规则（仅测试用）。</summary>
    public static bool DebugMatch;
    public static readonly List<string> DebugMatches = new();

    private static readonly List<CompiledRule> TopLevel = new();
    private static readonly Dictionary<string, CompiledRule> Named = new(StringComparer.Ordinal);
    private static bool _loaded;
    private static readonly object Gate = new();

    /// <summary>已加载的顶层规则数。 / Loaded top-level rule count.</summary>
    public static int TopLevelCount => TopLevel.Count;

    /// <summary>
    /// <para>解码并装载生成器规则表（幂等）。</para>
    /// Decodes and loads the generator's rule tables (idempotent).
    /// </summary>
    public static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            using var ms = new MemoryStream(CompiledMagicData.Rules, writable: false);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
            uint count = br.ReadUInt32();
            for (uint i = 0; i < count; i++)
            {
                TopLevel.Add(ReadRule(br));
            }

            BuildNameIndex(TopLevel);
            _loaded = true;
        }
    }

    private static void BuildNameIndex(List<CompiledRule> rules)
    {
        foreach (var r in rules)
        {
            if (r.Name is not null)
            {
                Named[r.Name] = r;
            }

            BuildNameIndex(r.Children);
        }
    }

    private static CompiledRule ReadRule(BinaryReader br)
    {
        var r = new CompiledRule
        {
            Kind = (MagicKind)br.ReadByte(),
            NumberType = (MagicNumberType)br.ReadByte(),
            Relation = (MagicRelation)br.ReadByte(),
            MaskOp = (MagicMaskOp)br.ReadByte(),
            StringFlags = (MagicStringFlags)br.ReadUInt16(),
            RegexFlags = (MagicRegexFlags)br.ReadByte(),
            IndirectMaskOp = (MagicMaskOp)br.ReadByte(),
            IndirectType = (MagicNumberType)br.ReadByte(),
            PStringLengthBytes = br.ReadByte(),
            Flags = (MagicFlags)br.ReadUInt16(),
            Indent = br.ReadInt32(),
            Offset = br.ReadInt64(),
            Value = br.ReadInt64(),
            Mask = br.ReadInt64(),
            IndirectMask = br.ReadInt64(),
            IndirectBaseOffset = br.ReadInt64(),
            IndirectAddend = br.ReadInt32(),
            SearchRange = br.ReadInt32(),
            Strength = br.ReadInt32(),
            HasMask = br.ReadBoolean(),
            MaskInvert = br.ReadBoolean(),
            HasIndirectMask = br.ReadBoolean(),
            IndirectMaskInvert = br.ReadBoolean(),
            PStringBigEndian = br.ReadBoolean(),
            Description = br.ReadString(),
            MimeType = br.ReadString(),
            Extensions = br.ReadString(),
            Encoding = br.ReadString(),
            Name = NullIfEmpty(br.ReadString()),
            UseName = NullIfEmpty(br.ReadString()),
            Source = br.ReadString(),
            RegexPattern = NullIfEmpty(br.ReadString()),
        };

        int plen = br.ReadInt32();
        if (plen > 0)
        {
            r.Pattern = br.ReadBytes(plen);
        }

        int childCount = br.ReadInt32();
        for (int c = 0; c < childCount; c++)
        {
            r.Children.Add(ReadRule(br));
        }

        return r;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// <para>对文件字节执行匹配，返回最佳命中（强度优先，其次加载顺序）。</para>
    /// Matches the bytes, returning the best hit (strength first, then load order).
    /// </summary>
    public static MagicInfo? Match(ReadOnlySpan<byte> data)
    {
        EnsureLoaded();
        if (data.IsEmpty)
        {
            return null;
        }

        byte[] file = data.ToArray();
        int len = file.Length;
        MagicInfo? best = null;

        foreach (var rule in TopLevel)
        {
            // A `name` template is a macro invoked only via `use`; it never matches on its own.
            if (rule.Name is not null)
            {
                continue;
            }

            var ctx = new MatchContext(file, len);
            if (TryEvaluate(rule, ctx, parentStart: 0, parentEnd: 0, top: true, depth: 0))
            {
                if (ctx.Parts.Count == 0)
                {
                    continue;
                }

                string desc = string.Concat(ctx.Parts);
                if (string.IsNullOrEmpty(desc.Trim()))
                {
                    continue;
                }

                int strength = ctx.Strength;
                if (best is null || strength > best.Strength)
                {
                    string resolvedDesc = ResolveConditionals(desc)!;
                    best = new MagicInfo
                    {
                        Description = Sanitize(resolvedDesc),
                        Source = ctx.Source,
                        MimeType = ResolveConditionals(FirstNonEmpty(ctx.Mime)),
                        Extensions = ResolveConditionals(FirstNonEmpty(ctx.Ext)),
                        Encoding = FirstNonEmpty(ctx.Encoding),
                        IsText = FirstNonEmpty(ctx.Encoding) is { } e && e is not ("binary" or "unknown"),
                        Strength = strength,
                    };

                    if (DebugMatch)
                    {
                        DebugMatches.Add($"[{strength}] src={rule.Source} kind={rule.Kind} num={rule.NumberType} rel={rule.Relation} off={rule.Offset} val={rule.Value} desc='{rule.Description}' => '{Sanitize(resolvedDesc)}'");
                    }

                    if (strength >= 100)
                    {
                        break;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// <para>仅返回 MIME 类型。</para>
    /// Returns just the MIME type.
    /// </summary>
    public static string? MimeType(ReadOnlySpan<byte> data) => Match(data)?.MimeType;

    private static string? FirstNonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // magic(5) `${x?TRUE:FALSE}` conditional conversions. The TRUE branch depends on a flag our
    // engine does not model (e.g. ELF DF_1_PIE from the dynamic section), so we render the FALSE
    // branch, which is the correct default for every input we classify (an ET_DYN without PIE).
    // Non-conditional `${name}`/`${counter}` inline expansions (2 upstream files) are left verbatim.
    private static string? ResolveConditionals(string? s)
    {
        if (s is null || s.IndexOf("${", StringComparison.Ordinal) < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            int start = s.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(s, i, s.Length - i);
                break;
            }

            sb.Append(s, i, start - i);
            int end = s.IndexOf('}', start + 2);
            if (end < 0)
            {
                sb.Append(s, start, s.Length - start);
                break;
            }

            string inner = s[(start + 2)..end];
            int q = inner.IndexOf('?');
            int colon = q < 0 ? -1 : inner.IndexOf(':', q);
            if (q < 0 || colon < 0)
            {
                sb.Append(s, start, end - start + 1);
            }
            else
            {
                sb.Append(inner[(colon + 1)..]);
            }

            i = end + 1;
        }

        return sb.ToString();
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\0')
            {
                break;
            }

            if (char.IsControl(c) && c != '\t')
            {
                sb.Append('.');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    private static bool TryEvaluate(CompiledRule rule, MatchContext ctx, int parentStart, int parentEnd, bool top, int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        if (rule.UseName is not null)
        {
            if (!Named.TryGetValue(rule.UseName, out var named))
            {
                return false;
            }

            // A template that re-enters itself on the current path is a cycle; skip it (libmagic
            // likewise forbids recursive templates). Guards against stack overflow on malformed trees.
            ctx.ActiveUses ??= new HashSet<string>(StringComparer.Ordinal);
            if (!ctx.ActiveUses.Add(rule.UseName))
            {
                return false;
            }

            try
            {
                int baseStart = rule.Indent > 0 ? parentStart + (int)rule.Offset : (int)rule.Offset;
                int baseEnd = (rule.Flags & MagicFlags.RelativeToEnd) != 0 ? parentEnd + (int)rule.Offset : baseStart;
                return EvaluateOrdered(named.Children, ctx, baseStart, baseEnd, depth + 1);
            }
            finally
            {
                ctx.ActiveUses.Remove(rule.UseName);
            }
        }

        if (!ResolveOffset(rule, ctx, parentStart, parentEnd, out int offset, out int indirectReadSize))
        {
            return false;
        }

        if ((rule.Flags & MagicFlags.Default) != 0)
        {
            if (top)
            {
                _ = indirectReadSize;
                return false;
            }

            // `default` is a fallback container: it prints its own message (if any) and its children
            // are tried relative to the inherited base offset (not the default's own position), which is
            // how libmagic resolves the `>N default` + `>>N <test>` idiom (e.g. PNG's "PNG image data").
            ctx.LastLevelMatched = true;
            Emit(rule, ctx, parentStart, strValue: null, intValue: null, defaultFormat: true);
            EvaluateChildren(rule, ctx, parentStart, parentEnd, depth);
            _ = indirectReadSize;
            return true;
        }

        if (rule.Name is not null)
        {
            // A name/regex template rule itself always passes; its body children decide.
            // Children inherit this template's base offset (absolute-from-header), not its read position.
            ctx.LastLevelMatched = true;
            bool any = EvaluateChildren(rule, ctx, parentStart, parentEnd, depth);
            return any || rule.Children.Count == 0;
        }

        if (!TryValueMatch(rule, ctx.File, offset, ctx.Length, out string? strValue, out long? intValue, out int matchEnd))
        {
            ctx.LastLevelMatched = false;
            return false;
        }

        if ((rule.Flags & MagicFlags.Negate) != 0)
        {
            ctx.LastLevelMatched = false;
            return false;
        }

        ctx.LastLevelMatched = true;
        ctx.Strength = Math.Max(ctx.Strength, top ? rule.Strength : ctx.Strength);
        if (top && ctx.Source is null)
        {
            ctx.Source = string.IsNullOrEmpty(rule.Source) ? null : rule.Source;
        }

        Emit(rule, ctx, offset, strValue, intValue, defaultFormat: false);
        // Children are based on the inherited base offset (absolute-from-header), while `&` (relative-to-end)
        // children still anchor to this rule's match end.
        EvaluateChildren(rule, ctx, parentStart, matchEnd, depth);
        _ = indirectReadSize;
        return true;
    }

    private static bool EvaluateChildren(CompiledRule rule, MatchContext ctx, int parentStart, int parentEnd, int depth)
        => EvaluateOrdered(rule.Children, ctx, parentStart, parentEnd, depth + 1);

    private static bool EvaluateOrdered(List<CompiledRule> children, MatchContext ctx, int parentStart, int parentEnd, int depth)
    {
        // `default` is the fallback for the block of tests sharing its offset: it fires only when no concrete
        // (non-default) sibling at the SAME offset matched. Evaluate concrete children first, then defaults whose
        // offset had no concrete hit (keeps ELF from printing "unknown arch" after a known machine, and PNG from
        // printing "invalid color type" after RGB).
        HashSet<long>? matchedConcreteOffsets = null;
        bool any = false;

        foreach (var child in children)
        {
            if ((child.Flags & MagicFlags.Default) != 0)
            {
                continue;
            }

            if (TryEvaluate(child, ctx, parentStart, parentEnd, top: false, depth))
            {
                any = true;
                (matchedConcreteOffsets ??= new HashSet<long>()).Add(child.Offset);
            }
        }

        foreach (var child in children)
        {
            if ((child.Flags & MagicFlags.Default) == 0)
            {
                continue;
            }

            if (matchedConcreteOffsets is not null && matchedConcreteOffsets.Contains(child.Offset))
            {
                continue;
            }

            if (TryEvaluate(child, ctx, parentStart, parentEnd, top: false, depth))
            {
                any = true;
            }
        }

        return any;
    }

    private static void Emit(CompiledRule rule, MatchContext ctx, int offset, string? strValue, long? intValue, bool defaultFormat)
    {
        if ((rule.Flags & MagicFlags.Consume) != 0)
        {
            return;
        }

        string desc = defaultFormat && string.IsNullOrEmpty(rule.Description)
            ? (intValue is not null ? FormatValue(rule, intValue.Value) : strValue ?? string.Empty)
            : FormatDescription(rule, ctx.File, offset, ctx.Length, intValue, strValue);

        if (!string.IsNullOrEmpty(desc))
        {
            if (ctx.Parts.Count > 0 && (rule.Flags & MagicFlags.NoSpace) == 0)
            {
                ctx.Parts.Add(" ");
            }

            ctx.Parts.Add(desc);
        }

        if (!string.IsNullOrEmpty(rule.MimeType))
        {
            ctx.Mime ??= rule.MimeType;
        }

        if (!string.IsNullOrEmpty(rule.Extensions))
        {
            ctx.Ext ??= rule.Extensions;
        }

        if (!string.IsNullOrEmpty(rule.Encoding))
        {
            ctx.Encoding ??= rule.Encoding;
        }
    }

    private static bool ResolveOffset(CompiledRule rule, MatchContext ctx, int parentStart, int parentEnd, out int offset, out int indirectReadSize)
    {
        indirectReadSize = 0;
        byte[] f = ctx.File;

        if ((rule.Flags & MagicFlags.Indirect) != 0)
        {
            long raw;
            if (!TryReadNumber(f, (int)rule.Offset, rule.IndirectType, ctx.Length, out raw))
            {
                offset = 0;
                return false;
            }

            if (rule.HasIndirectMask)
            {
                raw = ApplyOp(raw, rule.IndirectMaskOp, rule.IndirectMask);
            }

            if (rule.IndirectMaskInvert)
            {
                raw = ~raw;
            }

            long target = raw + rule.IndirectAddend + rule.Value;
            if (rule.Indent > 0)
            {
                target += (rule.Flags & MagicFlags.RelativeToEnd) != 0 ? parentEnd : parentStart;
            }

            if (target < 0 || target > ctx.Length)
            {
                offset = 0;
                return false;
            }

            offset = (int)target;
            return true;
        }

        if ((rule.Flags & MagicFlags.FromEnd) != 0)
        {
            long o = ctx.Length + rule.Offset;
            offset = (int)o;
            return o >= 0 && o <= ctx.Length;
        }

        if (rule.Indent > 0)
        {
            long baseOff = (rule.Flags & MagicFlags.RelativeToEnd) != 0 ? parentEnd : parentStart;
            long o = baseOff + rule.Offset;
            offset = (int)o;
            return o >= 0 && o <= ctx.Length;
        }

        offset = (int)rule.Offset;
        return offset >= 0 && offset <= ctx.Length;
    }

    private static bool TryValueMatch(CompiledRule rule, byte[] f, int offset, int len, out string? strValue, out long? intValue, out int matchEnd)
    {
        strValue = null;
        intValue = null;
        matchEnd = offset;

        // Match-any (`x` value): numeric prints the value; string-family captures a printable field for %s.
        if (rule.Relation == MagicRelation.Any && rule.Kind is not MagicKind.None)
        {
            if (rule.Kind == MagicKind.Number)
            {
                if (!TryReadNumber(f, offset, rule.NumberType, len, out long anyVal))
                {
                    return false;
                }

                if (rule.HasMask)
                {
                    anyVal = ApplyOp(anyVal, rule.MaskOp, rule.Mask);
                }

                if (rule.MaskInvert)
                {
                    anyVal = ~anyVal;
                }

                intValue = anyVal;
                matchEnd = offset + WidthOf(rule.NumberType);
                return true;
            }

            int cap = offset;
            while (cap < len && f[cap] != 0 && f[cap] != (byte)'\n')
            {
                cap++;
            }

            strValue = DecodeString(rule, f, offset, cap - offset);
            matchEnd = cap;
            return true;
        }

        switch (rule.Kind)
        {
            case MagicKind.Number:
                {
                    if (!TryReadNumber(f, offset, rule.NumberType, len, out long actual))
                    {
                        return false;
                    }

                    if (rule.HasMask)
                    {
                        actual = ApplyOp(actual, rule.MaskOp, rule.Mask);
                    }

                    if (rule.MaskInvert)
                    {
                        actual = ~actual;
                    }

                    intValue = actual;
                    matchEnd = offset + WidthOf(rule.NumberType);
                    if (rule.Relation == MagicRelation.Any)
                    {
                        return true;
                    }

                    long expected = rule.Value;
                    bool ok = CompareNumeric(rule, actual, expected);
                    return ok;
                }

            case MagicKind.String:
                return TryMatchString(rule, f, offset, len, out strValue, out matchEnd);

            case MagicKind.Search:
                return TryMatchSearch(rule, f, offset, len, out strValue, out matchEnd);

            case MagicKind.PString:
                return TryMatchPString(rule, f, offset, len, out strValue, out matchEnd);

            case MagicKind.String16Le:
            case MagicKind.String16Be:
                return TryMatchString16(rule, f, offset, len, out strValue, out matchEnd);

            case MagicKind.Regex:
                return TryMatchRegex(rule, f, offset, len, out strValue, out matchEnd);

            case MagicKind.Guid:
                return TryMatchGuid(rule, f, offset, len, out strValue, out matchEnd);

            case MagicKind.None:
            default:
                return rule.Relation == MagicRelation.Any;
        }
    }

    private static bool CompareNumeric(CompiledRule rule, long actual, long expected)
    {
        // libmagic compares within the number type's bit width: a value like `0xed26ff3a` in a
        // (signed) lelong is the 32-bit pattern, not the sign-extended negative, so mask both operands
        // to the width first. Signedness only decides ordering (`<`/`>`), never equality or bit tests.
        int width = WidthOf(rule.NumberType);
        if (width is <= 0 or > 8)
        {
            width = 8;
        }

        ulong mask = width >= 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1UL;
        bool unsigned = IsUnsignedType(rule.NumberType);
        ulong ua = (ulong)actual & mask;
        ulong ue = (ulong)expected & mask;

        static long SignExtend(ulong v, int width)
        {
            int shift = 64 - (width * 8);
            return shift <= 0 ? (long)v : ((long)v << shift) >> shift;
        }

        long sa = unsigned ? (long)ua : SignExtend(ua, width);
        long se = unsigned ? (long)ue : SignExtend(ue, width);

        return rule.Relation switch
        {
            MagicRelation.Equal => ua == ue,
            MagicRelation.NotEqual => ua != ue,
            MagicRelation.LessThan => unsigned ? ua < ue : sa < se,
            MagicRelation.GreaterThan => unsigned ? ua > ue : sa > se,
            MagicRelation.BitAnd => (ua & ue) == ue,
            MagicRelation.BitXor => (ua ^ ue) == ue,
            _ => true,
        };
    }

    private static bool TryMatchString(CompiledRule rule, byte[] f, int offset, int len, out string? strValue, out int matchEnd)
    {
        strValue = null;
        matchEnd = offset;
        byte[] p = rule.Pattern ?? [];
        int i = 0, j = 0;
        bool ic = (rule.StringFlags & MagicStringFlags.IgnoreCase) != 0;

        while (i < p.Length)
        {
            byte b = p[i];

            if ((rule.StringFlags & MagicStringFlags.CompactWhitespace) != 0 && IsSpace(b))
            {
                if (offset + j >= len || !IsSpace(f[offset + j]))
                {
                    return false;
                }

                while (j < len - offset && IsSpace(f[offset + j]))
                {
                    j++;
                }

                i++;
                continue;
            }

            if ((rule.StringFlags & MagicStringFlags.OptionalWhitespace) != 0 && IsSpace(b))
            {
                while (j < len - offset && IsSpace(f[offset + j]))
                {
                    j++;
                }

                i++;
                continue;
            }

            if (offset + j >= len)
            {
                return false;
            }

            byte d = f[offset + j];
            if (ic)
            {
                b = ToLower(b);
                d = ToLower(d);
            }

            if (b != d)
            {
                return false;
            }

            i++;
            j++;
        }

        if ((rule.StringFlags & MagicStringFlags.FullWord) != 0 && offset + j < len && !IsSpace(f[offset + j]))
        {
            return false;
        }

        if (rule.Relation == MagicRelation.NotEqual)
        {
            return false;
        }

        matchEnd = offset + j;
        strValue = DecodeString(rule, f, offset, j);
        return true;
    }

    private static bool TryMatchSearch(CompiledRule rule, byte[] f, int offset, int len, out string? strValue, out int matchEnd)
    {
        strValue = null;
        matchEnd = offset;
        byte[] p = rule.Pattern ?? [];
        if (p.Length == 0)
        {
            return false;
        }

        int range = rule.SearchRange > 0 ? rule.SearchRange : 100;
        int limit = Math.Min(len, offset + range);
        bool ic = (rule.StringFlags & MagicStringFlags.IgnoreCase) != 0;
        for (int s = offset; s + p.Length <= limit; s++)
        {
            bool ok = true;
            for (int k = 0; k < p.Length; k++)
            {
                byte a = p[k], b = f[s + k];
                if (a == 0xFF)
                {
                    continue;
                }

                if (ic)
                {
                    a = ToLower(a);
                    b = ToLower(b);
                }

                if (a != b)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                matchEnd = s + p.Length;
                strValue = DecodeString(rule, f, s, p.Length);
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchPString(CompiledRule rule, byte[] f, int offset, int len, out string? strValue, out int matchEnd)
    {
        strValue = null;
        matchEnd = offset;
        int lb = rule.PStringLengthBytes is (1 or 2 or 4) ? rule.PStringLengthBytes : 1;
        if (offset + lb > len)
        {
            return false;
        }

        long slen = lb switch
        {
            1 => f[offset],
            2 => rule.PStringBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(offset, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(offset, 2)),
            _ => rule.PStringBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(offset, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(offset, 4)),
        };

        int start = offset + lb;
        if (slen < 0 || start + slen > len)
        {
            return false;
        }

        byte[] p = rule.Pattern ?? [];
        int n = (int)slen;
        if (p.Length > n)
        {
            // pattern longer than actual string cannot match upstream equality
        }

        bool ic = (rule.StringFlags & MagicStringFlags.IgnoreCase) != 0;
        for (int k = 0; k < p.Length; k++)
        {
            byte a = p[k];
            byte b = k < n ? f[start + k] : (byte)0;
            if (ic)
            {
                a = ToLower(a);
                b = ToLower(b);
            }

            if (a != b)
            {
                return false;
            }
        }

        matchEnd = start + n;
        strValue = DecodeString(rule, f, start, n);
        return true;
    }

    private static bool TryMatchString16(CompiledRule rule, byte[] f, int offset, int len, out string? strValue, out int matchEnd)
    {
        strValue = null;
        matchEnd = offset;
        byte[] p = rule.Pattern ?? [];
        int units = p.Length / 2;
        bool be = rule.Kind == MagicKind.String16Be;
        for (int u = 0; u < units; u++)
        {
            int src = offset + u * 2;
            if (src + 2 > len || src + u * 0 >= len)
            {
                return false;
            }

            ushort got = be ? BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(src, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(src, 2));
            ushort want = be ? (ushort)((p[u * 2] << 8) | p[u * 2 + 1]) : (ushort)(p[u * 2] | (p[u * 2 + 1] << 8));
            if (got != want)
            {
                return false;
            }
        }

        matchEnd = offset + units * 2;
        strValue = Encoding.Unicode.GetString(f, offset, units * 2);
        return true;
    }

    private static bool TryMatchRegex(CompiledRule rule, byte[] f, int offset, int len, out string? strValue, out int matchEnd)
    {
        strValue = null;
        matchEnd = offset;
        if (string.IsNullOrEmpty(rule.RegexPattern))
        {
            return false;
        }

        Regex rx = rule.CompiledRegex ??= CompileRegex(rule);
        string region = Encoding.Latin1.GetString(f, offset, len - offset);
        var m = rx.Match(region);
        if (!m.Success)
        {
            return false;
        }

        matchEnd = offset + m.Index + m.Length;
        strValue = m.Value;
        return true;
    }

    private static Regex CompileRegex(CompiledRule rule)
    {
        var opt = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if ((rule.RegexFlags & MagicRegexFlags.IgnoreCase) != 0)
        {
            opt |= RegexOptions.IgnoreCase;
        }

        if ((rule.RegexFlags & MagicRegexFlags.SingleLine) != 0)
        {
            opt |= RegexOptions.Singleline;
        }

        if ((rule.RegexFlags & MagicRegexFlags.Multiline) != 0)
        {
            opt |= RegexOptions.Multiline;
        }

        try
        {
            return new Regex(rule.RegexPattern!, opt);
        }
        catch (ArgumentException)
        {
            return new Regex(Regex.Escape(rule.RegexPattern!), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
    }

    private static bool TryMatchGuid(CompiledRule rule, byte[] f, int offset, int len, out string? strValue, out int matchEnd)
    {
        strValue = null;
        matchEnd = offset;
        if (offset + 16 > len)
        {
            return false;
        }

        if (rule.Pattern is not { Length: 16 } want)
        {
            return true; // any guid
        }

        for (int k = 0; k < 16; k++)
        {
            if (want[k] != 0xFF && want[k] != f[offset + k])
            {
                return false;
            }
        }

        matchEnd = offset + 16;
        strValue = new Guid(f.AsSpan(offset, 16)).ToString("B");
        return true;
    }

    private static bool TryReadNumber(byte[] f, int offset, MagicNumberType t, int len, out long value)
    {
        value = 0;
        int w = WidthOf(t);
        if (offset < 0 || offset + w > len)
        {
            return false;
        }

        var span = f.AsSpan(offset, w);
        bool be = IsBigEndian(t);
        switch (w)
        {
            case 1:
                {
                    byte b = f[offset];
                    value = SignedByte(t) ? (sbyte)b : b;
                    return true;
                }
            case 2:
                {
                    ushort u = be ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);
                    value = SignedShort(t) ? (short)u : u;
                    return true;
                }
            case 4:
                {
                    uint u = be ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
                    value = SignedLong(t) ? (int)u : u;
                    return true;
                }
            case 8:
                {
                    ulong u = be ? BinaryPrimitives.ReadUInt64BigEndian(span) : BinaryPrimitives.ReadUInt64LittleEndian(span);
                    value = SignedQuad(t) ? (long)u : (long)u;
                    return true;
                }
            default:
                return false;
        }
    }

    private static long ApplyOp(long v, MagicMaskOp op, long m) => op switch
    {
        MagicMaskOp.And => v & m,
        MagicMaskOp.Or => v | m,
        MagicMaskOp.Xor => v ^ m,
        MagicMaskOp.Add => v + m,
        MagicMaskOp.Subtract => v - m,
        MagicMaskOp.Multiply => v * m,
        MagicMaskOp.Divide => m != 0 ? v / m : v,
        MagicMaskOp.Modulo => m != 0 ? v % m : v,
        _ => v,
    };

    private static bool IsBigEndian(MagicNumberType t) => t switch
    {
        MagicNumberType.Beshort or MagicNumberType.Ubeshort or MagicNumberType.Belong or MagicNumberType.Ubelong
            or MagicNumberType.Bequad or MagicNumberType.Ubequad or MagicNumberType.Bedate => true,
        _ => false,
    };

    private static bool SignedByte(MagicNumberType t) => t == MagicNumberType.Byte;
    private static bool SignedShort(MagicNumberType t) => t is MagicNumberType.Short or MagicNumberType.Leshort or MagicNumberType.Beshort;
    private static bool SignedLong(MagicNumberType t) => t is MagicNumberType.Long or MagicNumberType.Lelong or MagicNumberType.Belong or MagicNumberType.Date or MagicNumberType.Ledate or MagicNumberType.Bedate;
    private static bool SignedQuad(MagicNumberType t) => t is MagicNumberType.Quad or MagicNumberType.Lequad or MagicNumberType.Bequad;
    private static bool IsUnsignedType(MagicNumberType t) => t is MagicNumberType.UByte or MagicNumberType.UShort or MagicNumberType.ULong
        or MagicNumberType.Uleshort or MagicNumberType.Ubeshort or MagicNumberType.Ulelong or MagicNumberType.Ubelong
        or MagicNumberType.Ulequad or MagicNumberType.Ubequad or MagicNumberType.Uquad;

    /// <summary>宽度（字节）。</summary>
    public static int WidthOf(MagicNumberType t) => t switch
    {
        MagicNumberType.Byte or MagicNumberType.UByte => 1,
        MagicNumberType.Short or MagicNumberType.UShort or MagicNumberType.Leshort or MagicNumberType.Uleshort
            or MagicNumberType.Beshort or MagicNumberType.Ubeshort => 2,
        MagicNumberType.Long or MagicNumberType.ULong or MagicNumberType.Lelong or MagicNumberType.Ulelong
            or MagicNumberType.Belong or MagicNumberType.Ubelong or MagicNumberType.Date or MagicNumberType.Ledate or MagicNumberType.Bedate => 4,
        _ => 8,
    };

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\v' or (byte)'\f';
    private static byte ToLower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    private static string DecodeString(CompiledRule rule, byte[] f, int offset, int count)
    {
        // strip trailing NULs and optional trim
        int n = count;
        while (n > 0 && f[offset + n - 1] == 0)
        {
            n--;
        }

        if ((rule.StringFlags & MagicStringFlags.Trim) != 0)
        {
            while (n > 0 && IsSpace(f[offset + n - 1]))
            {
                n--;
            }
        }

        return Encoding.UTF8.GetString(f, offset, n);
    }

    private static string FormatValue(CompiledRule rule, long value)
    {
        if (IsDateType(rule.NumberType))
        {
            return FormatDate(value);
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsDateType(MagicNumberType t) => t is MagicNumberType.Date or MagicNumberType.Ledate or MagicNumberType.Bedate;

    private static string FormatUnsigned(long value, int width)
    {
        ulong mask = width >= 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1UL;
        return ((ulong)value & mask).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatDescription(CompiledRule rule, byte[] f, int offset, int len, long? intValue, string? strValue)
    {
        // A rule with an empty message is a pure test/gate in libmagic and contributes nothing to the
        // output. Value printing requires an explicit message with a %d/%s/... placeholder (or the
        // dedicated `default` path handled in Emit).
        if (string.IsNullOrEmpty(rule.Description))
        {
            return string.Empty;
        }

        return SubstitutePlaceholders(rule.Description, IsDateType(rule.NumberType) ? intValue : intValue, strValue, isDate: IsDateType(rule.NumberType), rule);
    }

    private static string FormatDate(long value)
    {
        try
        {
            DateTimeOffset dt = value >= 0 ? DateTimeOffset.FromUnixTimeSeconds(value) : DateTimeOffset.UnixEpoch.AddSeconds(value);
            return dt.UtcDateTime.ToString("ddd MMM  d HH:mm:ss yyyy", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string SubstitutePlaceholders(string template, long? intValue, string? strValue, bool isDate, CompiledRule rule)
    {
        if (!template.Contains('%'))
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '%')
            {
                sb.Append(c);
                i++;
                continue;
            }

            int j = i + 1;
            int width = 0;
            bool left = false;
            bool alternate = false;
            while (j < template.Length && (template[j] == '-' || template[j] == '#' || char.IsDigit(template[j])))
            {
                if (template[j] == '-')
                {
                    left = true;
                }
                else if (template[j] == '#')
                {
                    alternate = true;
                }
                else
                {
                    width = width * 10 + (template[j] - '0');
                }

                j++;
            }

            // Skip printf length modifiers (l, L, h, q, j, z, t) so the conversion char is reached.
            while (j < template.Length && (template[j] is 'l' or 'L' or 'h' or 'q' or 'j' or 'z' or 't'))
            {
                j++;
            }

            if (j >= template.Length)
            {
                sb.Append('%');
                i++;
                continue;
            }

            char spec = template[j];
            string seg;
            if (isDate && spec is 's' or 'd')
            {
                seg = intValue is not null ? FormatDate(intValue.Value) : "?";
            }
            else
            {
                seg = spec switch
                {
                    's' => strValue ?? intValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    'd' or 'i' => (intValue ?? 0).ToString(CultureInfo.InvariantCulture),
                    'u' => FormatUnsigned(intValue ?? 0, WidthOf(rule.NumberType)),
                    'x' => alternate
                        ? "0x" + (intValue ?? 0).ToString("x", CultureInfo.InvariantCulture)
                        : (intValue ?? 0).ToString("x", CultureInfo.InvariantCulture),
                    'X' => alternate
                        ? "0X" + (intValue ?? 0).ToString("X", CultureInfo.InvariantCulture)
                        : (intValue ?? 0).ToString("X", CultureInfo.InvariantCulture),
                    'o' => alternate
                        ? "0" + Convert.ToString(intValue ?? 0, 8)
                        : Convert.ToString(intValue ?? 0, 8),
                    'c' => intValue is >= 0 and <= 0xFF ? ((char)intValue!.Value).ToString() : "?",
                    '%' => "%",
                    _ => "%" + spec,
                };
            }

            if (width > 0 && spec != '%')
            {
                seg = left ? seg.PadRight(width) : seg.PadLeft(width);
            }

            sb.Append(seg);
            i = j + 1;
        }

        return sb.ToString();
    }

    private sealed class MatchContext(byte[] file, int length)
    {
        public byte[] File { get; } = file;
        public int Length { get; } = length;
        public List<string> Parts { get; } = new();
        public string? Source;
        public string? Mime;
        public string? Ext;
        public string? Encoding;
        public bool LastLevelMatched;
        public int Strength;
        public HashSet<string>? ActiveUses;
    }
}