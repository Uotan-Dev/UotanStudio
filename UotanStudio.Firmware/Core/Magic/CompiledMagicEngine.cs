using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.Core.Magic.Generated;

namespace UotanStudio.Firmware.Core.Magic;

/// <summary>
/// Compiled magic engine: decodes the generator's rule tables and matches file bytes against them.
/// <para>编译型魔数引擎：解码生成器产出的规则表，并用它对文件字节执行匹配。</para>
/// </summary>
public static class CompiledMagicEngine
{
    private const int MaxDepth = 256;

    /// <summary>Diagnostic switch: every improvement of the best top-level hit is appended to
    /// <see cref="DebugMatches"/>; intended for tests. <para>诊断开关：每次最佳结果的顶层命中规则改进都会追加到
    /// <see cref="DebugMatches"/>，仅用于测试。</para></summary>
    public static bool DebugMatch;

    /// <summary>Diagnostic lines recorded while <see cref="DebugMatch"/> is enabled, one per improved hit;
    /// never cleared by the engine itself. <para>在 <see cref="DebugMatch"/> 开启期间记录的诊断行，每次改进的命中一条；
    /// 引擎自身不会清空。</para></summary>
    public static readonly List<string> DebugMatches = new();

    private static readonly List<CompiledRule> TopLevel = new();
    private static FrozenDictionary<string, CompiledRule> Named = FrozenDictionary<string, CompiledRule>.Empty;
    private static FrozenDictionary<(int Offset, byte FirstByte), CompiledRule[]> Indexed =
        FrozenDictionary<(int Offset, byte FirstByte), CompiledRule[]>.Empty;
    private static int[] IndexedOffsets = [];
    private static CompiledRule[] FallbackRules = [];
    private static CompiledRule[] AllTopLevelRules = [];
    private static int _indexedRuleCount;
    private static bool _loaded;
    private static readonly Lock Gate = new();

    private static readonly SearchValues<byte> TrimBytes = SearchValues.Create(" \t\r\n\v\f"u8);

    /// <summary>Loaded top-level rule count, valid after <see cref="EnsureLoaded"/> has run.
    /// <para>已加载的顶层规则数，在 <see cref="EnsureLoaded"/> 执行后有效。</para></summary>
    /// <value>
    /// Number of top-level entries in the rule table, <c>0</c> before loading.
    /// <para>规则表顶层条目数量，加载前为 <c>0</c>。</para>
    /// </value>
    public static int TopLevelCount => TopLevel.Count;

    /// <summary>
    /// Decodes and loads the generator's rule tables (idempotent).
    /// <para>解码并装载生成器规则表（幂等）。</para>
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
            var count = br.ReadUInt32();
            for (uint i = 0; i < count; i++)
            {
                TopLevel.Add(ReadRule(br));
            }

            var named = new Dictionary<string, CompiledRule>(StringComparer.Ordinal);
            BuildNameIndex(TopLevel, named);
            Named = named.ToFrozenDictionary(StringComparer.Ordinal);
            BuildMatchIndex();
            _loaded = true;
        }
    }

    private static void BuildMatchIndex()
    {
        var buckets = new Dictionary<(int Offset, byte FirstByte), List<CompiledRule>>();
        var fallback = new List<CompiledRule>();
        var all = new List<CompiledRule>();
        for (var order = 0; order < TopLevel.Count; order++)
        {
            var rule = TopLevel[order];
            rule.TopLevelOrder = order;
            if (rule.Name is not null)
            {
                continue;
            }

            all.Add(rule);
            if (!TryGetIndexKey(rule, out var key))
            {
                fallback.Add(rule);
                continue;
            }

            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = [];
                buckets[key] = bucket;
            }

            bucket.Add(rule);
        }

        Indexed = buckets.ToFrozenDictionary(static pair => pair.Key, static pair => pair.Value.ToArray());
        IndexedOffsets = [.. buckets.Keys.Select(static key => key.Offset).Distinct().Order()];
        FallbackRules = [.. fallback];
        AllTopLevelRules = [.. all];
        _indexedRuleCount = all.Count - fallback.Count;
    }

    private static bool TryGetIndexKey(CompiledRule rule, out (int Offset, byte FirstByte) key)
    {
        key = default;
        const MagicFlags unsupported =
            MagicFlags.Indirect | MagicFlags.FromEnd | MagicFlags.Default;
        if ((rule.Flags & unsupported) != 0 || rule.Relation != MagicRelation.Equal)
        {
            return false;
        }

        if (rule.Offset < 0 || rule.Offset > int.MaxValue - 8)
        {
            return false;
        }

        var offset = (int)rule.Offset;
        if (rule.Kind == MagicKind.String)
        {
            if (rule.Pattern is not { Length: > 0 } pattern)
            {
                return false;
            }

            if ((rule.StringFlags & (MagicStringFlags.CompactWhitespace | MagicStringFlags.OptionalWhitespace)) != 0)
            {
                return false;
            }

            key = (offset, ToLower(pattern[0]));
            return true;
        }

        if (rule.Kind == MagicKind.Number && !rule.HasMask && !rule.MaskInvert)
        {
            var width = WidthOf(rule.NumberType);
            if (width is <= 0 or > 8)
            {
                return false;
            }

            var mask = width >= 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1UL;
            var expected = (ulong)rule.Value & mask;
            var first = IsBigEndian(rule.NumberType) ? (byte)(expected >> ((width - 1) * 8)) : (byte)expected;
            key = (offset, ToLower(first));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Number of top-level rules carried by the fixed-offset index, valid after <see cref="EnsureLoaded"/>.
    /// <para>固定偏移索引承载的顶层规则数量，在 <see cref="EnsureLoaded"/> 执行后有效。</para>
    /// </summary>
    internal static int IndexedRuleCount
    {
        get
        {
            EnsureLoaded();
            return _indexedRuleCount;
        }
    }

    /// <summary>
    /// Number of top-level rules that stay in the ordered fallback list, valid after <see cref="EnsureLoaded"/>.
    /// <para>保留在有序兜底列表中的顶层规则数量，在 <see cref="EnsureLoaded"/> 执行后有效。</para>
    /// </summary>
    internal static int FallbackRuleCount
    {
        get
        {
            EnsureLoaded();
            return FallbackRules.Length;
        }
    }

    /// <summary>
    /// Number of distinct offsets addressed by the fixed-offset index, valid after <see cref="EnsureLoaded"/>.
    /// <para>固定偏移索引涉及的不同偏移数量，在 <see cref="EnsureLoaded"/> 执行后有效。</para>
    /// </summary>
    internal static int IndexedOffsetCount
    {
        get
        {
            EnsureLoaded();
            return IndexedOffsets.Length;
        }
    }

    private static void BuildNameIndex(List<CompiledRule> rules, Dictionary<string, CompiledRule> named)
    {
        foreach (var r in rules)
        {
            if (r.Name is not null)
            {
                named[r.Name] = r;
            }

            BuildNameIndex(r.Children, named);
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

        var plen = br.ReadInt32();
        if (plen > 0)
        {
            r.Pattern = br.ReadBytes(plen);
        }

        var childCount = br.ReadInt32();
        for (var c = 0; c < childCount; c++)
        {
            r.Children.Add(ReadRule(br));
        }

        return r;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// Matches the bytes, returning the best hit (strength first, then load order).
    /// <para>对文件字节执行匹配，返回最佳命中（强度优先，其次加载顺序）。</para>
    /// </summary>
    /// <param name="data">
    /// File bytes to probe; the span is copied into a pooled buffer and may be a slice of a larger
    /// buffer. An empty span yields no match. <para>待探测的文件字节；内部会复制到复用缓冲区，因此可以是更大缓冲区的切片。空区间不产生匹配。</para>
    /// </param>
    /// <returns>
    /// The best hit, ranked by rule strength and, on a tie, by load order, or <see langword="null"/>
    /// when no unnamed top-level rule produces a non-blank description. Evaluation stops early once a
    /// rule with strength of at least <c>100</c> is reached.
    /// <para>按规则强度排序、强度相同时按加载顺序取得的最佳命中；没有无名顶层规则产生非空白描述时返回
    /// <see langword="null"/>。一旦出现强度不小于 <c>100</c> 的规则即提前停止。</para>
    /// </returns>
    /// <example>
    /// <code>
    /// MagicInfo? info = CompiledMagicEngine.Match(header);
    /// string? mime = info?.MimeType;
    /// </code>
    /// </example>
    public static MagicInfo? Match(ReadOnlySpan<byte> data) => MatchCore(data, useIndex: true);

    /// <summary>
    /// Matches every top-level rule in load order without consulting the fixed-offset index; retained for the
    /// equivalence test that compares both evaluation paths.
    /// <para>不查询固定偏移索引、按加载顺序逐条匹配全部顶层规则；保留给比较两种求值路径的等价性测试。</para>
    /// </summary>
    /// <param name="data">
    /// File bytes to probe, with the same empty-span and ranking rules as <see cref="Match"/>.
    /// <para>待探测的文件字节，空区间与排序规则与 <see cref="Match"/> 相同。</para>
    /// </param>
    /// <returns>The same result the indexed path must produce for identical input.
    /// <para>对相同输入，索引路径必须产出的同一结果。</para></returns>
    internal static MagicInfo? MatchWithoutIndex(ReadOnlySpan<byte> data) => MatchCore(data, useIndex: false);

    private static MagicInfo? MatchCore(ReadOnlySpan<byte> data, bool useIndex)
    {
        EnsureLoaded();
        if (data.IsEmpty)
        {
            return null;
        }

        var len = data.Length;
        var file = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            data.CopyTo(file);
            return MatchRules(file, len, useIndex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(file);
        }
    }

    private static MagicInfo? MatchRules(byte[] file, int len, bool useIndex)
    {
        MagicInfo? best = null;
        var context = new MatchContext(file, len);
        if (!useIndex)
        {
            foreach (var rule in AllTopLevelRules)
            {
                if (TryEvaluateCandidate(rule, context, ref best))
                {
                    break;
                }
            }

            return best;
        }

        var bucket = CollectIndexedCandidates(file, len);
        var bucketCount = bucket?.Count ?? 0;
        var fallback = FallbackRules;
        var fi = 0;
        var bi = 0;
        while (fi < fallback.Length && bi < bucketCount)
        {
            var rule = fallback[fi].TopLevelOrder < bucket![bi].TopLevelOrder
                ? fallback[fi++]
                : bucket[bi++];
            if (TryEvaluateCandidate(rule, context, ref best))
            {
                return best;
            }
        }

        while (fi < fallback.Length)
        {
            if (TryEvaluateCandidate(fallback[fi++], context, ref best))
            {
                return best;
            }
        }

        while (bi < bucketCount)
        {
            if (TryEvaluateCandidate(bucket![bi++], context, ref best))
            {
                return best;
            }
        }

        return best;
    }

    private static bool TryEvaluateCandidate(CompiledRule rule, MatchContext ctx, ref MagicInfo? best)
    {
        ctx.Reset();
        if (!TryEvaluate(rule, ctx, parentStart: 0, parentEnd: 0, top: true, depth: 0))
        {
            return false;
        }

        if (ctx.Parts.Count == 0)
        {
            return false;
        }

        var desc = string.Concat(ctx.Parts);
        if (string.IsNullOrEmpty(desc.Trim()))
        {
            return false;
        }

        var strength = ctx.Strength;
        if (best is not null && strength <= best.Strength)
        {
            return false;
        }

        var resolvedDesc = ResolveConditionals(desc)!;
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

        return strength >= 100;
    }

    private static List<CompiledRule>? CollectIndexedCandidates(byte[] file, int len)
    {
        List<CompiledRule>? candidates = null;
        var offsets = IndexedOffsets;
        for (var i = 0; i < offsets.Length; i++)
        {
            var offset = offsets[i];
            if (offset >= len)
            {
                continue;
            }

            if (Indexed.TryGetValue((offset, ToLower(file[offset])), out var rules))
            {
                (candidates ??= []).AddRange(rules);
            }
        }

        candidates?.Sort(static (left, right) => left.TopLevelOrder.CompareTo(right.TopLevelOrder));
        return candidates;
    }

    /// <summary>
    /// Returns just the MIME type.
    /// <para>仅返回 MIME 类型。</para>
    /// </summary>
    /// <param name="data">
    /// File bytes to probe, subject to the same rules as <see cref="Match"/>.
    /// <para>待探测的文件字节，规则与 <see cref="Match"/> 相同。</para>
    /// </param>
    /// <returns>The declared MIME type of the best hit, or <see langword="null"/> when nothing matches or
    /// the matched rule declares no type. <para>最佳命中声明的 MIME 类型；无匹配或所命中的规则未声明类型时为 <see langword="null"/>。</para></returns>
    public static string? MimeType(ReadOnlySpan<byte> data) => Match(data)?.MimeType;

    private static string? FirstNonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string? ResolveConditionals(string? s)
    {
        if (s is null || s.IndexOf("${", StringComparison.Ordinal) < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var start = s.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(s, i, s.Length - i);
                break;
            }

            sb.Append(s, i, start - i);
            var end = s.IndexOf('}', start + 2);
            if (end < 0)
            {
                sb.Append(s, start, s.Length - start);
                break;
            }

            var inner = s[(start + 2)..end];
            var q = inner.IndexOf('?');
            var colon = q < 0 ? -1 : inner.IndexOf(':', q);
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
        foreach (var c in s)
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

            ctx.ActiveUses ??= new HashSet<string>(StringComparer.Ordinal);
            if (!ctx.ActiveUses.Add(rule.UseName))
            {
                return false;
            }

            try
            {
                var baseStart = rule.Indent > 0 ? parentStart + (int)rule.Offset : (int)rule.Offset;
                var baseEnd = (rule.Flags & MagicFlags.RelativeToEnd) != 0 ? parentEnd + (int)rule.Offset : baseStart;
                return EvaluateOrdered(named.Children, ctx, baseStart, baseEnd, depth + 1);
            }
            finally
            {
                ctx.ActiveUses.Remove(rule.UseName);
            }
        }

        if (!ResolveOffset(rule, ctx, parentStart, parentEnd, out var offset, out var indirectReadSize))
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

            ctx.LastLevelMatched = true;
            Emit(rule, ctx, parentStart, strValue: null, intValue: null, defaultFormat: true);
            EvaluateChildren(rule, ctx, parentStart, parentEnd, depth);
            _ = indirectReadSize;
            return true;
        }

        if (rule.Name is not null)
        {
            ctx.LastLevelMatched = true;
            var any = EvaluateChildren(rule, ctx, parentStart, parentEnd, depth);
            return any || rule.Children.Count == 0;
        }

        if (!TryValueMatch(rule, ctx.File, offset, ctx.Length, out var strValue, out var intValue, out var matchEnd))
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
        EvaluateChildren(rule, ctx, parentStart, matchEnd, depth);
        _ = indirectReadSize;
        return true;
    }

    private static bool EvaluateChildren(CompiledRule rule, MatchContext ctx, int parentStart, int parentEnd, int depth)
        => EvaluateOrdered(rule.Children, ctx, parentStart, parentEnd, depth + 1);

    private static bool EvaluateOrdered(List<CompiledRule> children, MatchContext ctx, int parentStart, int parentEnd, int depth)
    {
        HashSet<long>? matchedConcreteOffsets = null;
        var any = false;

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

        var desc = defaultFormat && string.IsNullOrEmpty(rule.Description)
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
        var f = ctx.File;

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

            var target = raw + rule.IndirectAddend + rule.Value;
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
            var o = ctx.Length + rule.Offset;
            offset = (int)o;
            return o >= 0 && o <= ctx.Length;
        }

        if (rule.Indent > 0)
        {
            long baseOff = (rule.Flags & MagicFlags.RelativeToEnd) != 0 ? parentEnd : parentStart;
            var o = baseOff + rule.Offset;
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

        if (rule.Relation == MagicRelation.Any && rule.Kind is not MagicKind.None)
        {
            if (rule.Kind == MagicKind.Number)
            {
                if (!TryReadNumber(f, offset, rule.NumberType, len, out var anyVal))
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

            var cap = f.AsSpan(offset, len - offset).IndexOfAny((byte)0, (byte)'\n');
            matchEnd = cap < 0 ? len : offset + cap;
            strValue = DecodeString(rule, f, offset, matchEnd - offset);
            return true;
        }

        switch (rule.Kind)
        {
            case MagicKind.Number:
                {
                    if (!TryReadNumber(f, offset, rule.NumberType, len, out var actual))
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

                    var expected = rule.Value;
                    var ok = CompareNumeric(rule, actual, expected);
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
        var width = WidthOf(rule.NumberType);
        if (width is <= 0 or > 8)
        {
            width = 8;
        }

        var mask = width >= 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1UL;
        var unsigned = IsUnsignedType(rule.NumberType);
        var ua = (ulong)actual & mask;
        var ue = (ulong)expected & mask;

        static long SignExtend(ulong v, int width)
        {
            var shift = 64 - (width * 8);
            return shift <= 0 ? (long)v : ((long)v << shift) >> shift;
        }

        var sa = unsigned ? (long)ua : SignExtend(ua, width);
        var se = unsigned ? (long)ue : SignExtend(ue, width);

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
        var p = rule.Pattern ?? [];
        int i = 0, j = 0;
        var ic = (rule.StringFlags & MagicStringFlags.IgnoreCase) != 0;

        while (i < p.Length)
        {
            var b = p[i];

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

            var d = f[offset + j];
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
        var p = rule.Pattern ?? [];
        if (p.Length == 0)
        {
            return false;
        }

        var range = rule.SearchRange > 0 ? rule.SearchRange : 100;
        var limit = (int)Math.Min((long)len, (long)offset + range);
        var ic = (rule.StringFlags & MagicStringFlags.IgnoreCase) != 0;
        if (!ic && !p.AsSpan().Contains((byte)0xFF))
        {
            var found = f.AsSpan(offset, limit - offset).IndexOf(p);
            if (found < 0)
            {
                return false;
            }

            var start = offset + found;
            matchEnd = start + p.Length;
            strValue = DecodeString(rule, f, start, p.Length);
            return true;
        }

        for (var s = offset; s + p.Length <= limit; s++)
        {
            var ok = true;
            for (var k = 0; k < p.Length; k++)
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
        var lb = rule.PStringLengthBytes is (1 or 2 or 4) ? rule.PStringLengthBytes : 1;
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

        var start = offset + lb;
        if (slen < 0 || start + slen > len)
        {
            return false;
        }

        var p = rule.Pattern ?? [];
        var n = (int)slen;
        if (p.Length > n)
        {
        }

        var ic = (rule.StringFlags & MagicStringFlags.IgnoreCase) != 0;
        for (var k = 0; k < p.Length; k++)
        {
            var a = p[k];
            var b = k < n ? f[start + k] : (byte)0;
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
        var p = rule.Pattern ?? [];
        var units = p.Length / 2;
        var be = rule.Kind == MagicKind.String16Be;
        for (var u = 0; u < units; u++)
        {
            var src = offset + u * 2;
            if (src + 2 > len || src + u * 0 >= len)
            {
                return false;
            }

            var got = be ? BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(src, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(src, 2));
            var want = be ? (ushort)((p[u * 2] << 8) | p[u * 2 + 1]) : (ushort)(p[u * 2] | (p[u * 2 + 1] << 8));
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
        var region = Encoding.Latin1.GetString(f, offset, len - offset);
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
            return true;
        }

        for (var k = 0; k < 16; k++)
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
        var w = WidthOf(t);
        if (offset < 0 || offset + w > len)
        {
            return false;
        }

        var span = f.AsSpan(offset, w);
        var be = IsBigEndian(t);
        switch (w)
        {
            case 1:
                {
                    var b = f[offset];
                    value = SignedByte(t) ? (sbyte)b : b;
                    return true;
                }
            case 2:
                {
                    var u = be ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);
                    value = SignedShort(t) ? (short)u : u;
                    return true;
                }
            case 4:
                {
                    var u = be ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
                    value = SignedLong(t) ? (int)u : u;
                    return true;
                }
            case 8:
                {
                    var u = be ? BinaryPrimitives.ReadUInt64BigEndian(span) : BinaryPrimitives.ReadUInt64LittleEndian(span);
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

    /// <summary>
    /// Width in bytes of a magic number type, used to size the read from file bytes.
    /// <para>魔数数值类型的字节宽度，用于确定从文件字节读取的长度。</para>
    /// </summary>
    /// <param name="t">
    /// Numeric magic type to measure; anything that is not one, two or four bytes wide is treated as
    /// eight bytes. <para>待测量的魔数数值类型；不属于一、二、四字节宽度的类型一律按八字节处理。</para>
    /// </param>
    /// <returns>
    /// <c>1</c> for byte types, <c>2</c> for short types, <c>4</c> for long and date types, and <c>8</c>
    /// otherwise. <para>字节类型为 <c>1</c>，short 类型为 <c>2</c>，long 与 date 类型为 <c>4</c>，其余为 <c>8</c>。</para>
    /// </returns>
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
        var window = f.AsSpan(offset, count);
        var n = window.LastIndexOfAnyExcept((byte)0) + 1;
        if ((rule.StringFlags & MagicStringFlags.Trim) != 0 && n > 0)
        {
            n = window[..n].LastIndexOfAnyExcept(TrimBytes) + 1;
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
        var mask = width >= 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1UL;
        return ((ulong)value & mask).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatDescription(CompiledRule rule, byte[] f, int offset, int len, long? intValue, string? strValue)
    {
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
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c != '%')
            {
                sb.Append(c);
                i++;
                continue;
            }

            var j = i + 1;
            var width = 0;
            var left = false;
            var alternate = false;
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

            var spec = template[j];
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

        public void Reset()
        {
            Parts.Clear();
            Source = null;
            Mime = null;
            Ext = null;
            Encoding = null;
            LastLevelMatched = false;
            Strength = 0;
            ActiveUses?.Clear();
        }
    }
}