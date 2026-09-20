using System.Globalization;
using System.Text;
using UotanStudio.Firmware.SharedKernel.Magic;

namespace UotanStudio.Firmware.MagicGen;

/// <summary>
/// <para>把上游 magic(5) 文本片段解析为 <see cref="RuleAst"/> 树。逐行解析，异常行按类别计入
/// <see cref="Stats"/> 并跳过，绝不因单行失败中断整个语料。</para>
/// Parses an upstream magic(5) fragment into a <see cref="RuleAst"/> tree. Line-oriented; a failing line
/// is counted under a category in <see cref="Stats"/> and skipped, never aborting the corpus.
/// </summary>
internal sealed class Magic5Parser
{
    public sealed class Stats
    {
        public int Rules;
        public int SkippedRange;
        public int SkippedDer;
        public int SkippedIndirectKeyword;
        public int SkippedTransform;
        public int SkippedUnknownType;
        public int SkippedMalformed;
        public int Annotations;
        public readonly Dictionary<string, int> UnknownTypes = new(StringComparer.Ordinal);
    }

    private readonly Stats _stats = new();
    public Stats Statistics => _stats;

    /// <summary>当前片段标识（诊断用），由调用方在解析前设置。</summary>
    public string CurrentSource = string.Empty;

    /// <summary>
    /// <para>解析单个片段文本，返回顶层规则列表。</para>
    /// Parses one fragment's text into its top-level rule list.
    /// </summary>
    public List<RuleAst> ParseFragment(string text)
    {
        var tops = new List<RuleAst>();
        var stack = new Stack<(int level, RuleAst rule)>();
        RuleAst? lastEmitted = null;

        using var sr = new StringReader(text);
        string? line;
        int lineNo = 0;
        while ((line = sr.ReadLine()) is not null)
        {
            lineNo++;
            string trimmed = line.TrimStart('\t', ' ');
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed.StartsWith("!:", StringComparison.Ordinal))
            {
                ApplyAnnotation(lastEmitted, trimmed);
                continue;
            }

            RuleAst? rule;
            try
            {
                rule = ParseLine(line, ref lastEmitted);
            }
            catch (Exception)
            {
                _stats.SkippedMalformed++;
                continue;
            }

            if (rule is null)
            {
                continue;
            }

            rule.Source = $"{CurrentSource}:{lineNo}";

            while (stack.Count > 0 && stack.Peek().level >= rule.Indent)
            {
                stack.Pop();
            }

            if (stack.Count > 0)
            {
                stack.Peek().rule.Children.Add(rule);
            }
            else
            {
                tops.Add(rule);
            }

            stack.Push((rule.Indent, rule));
            lastEmitted = rule;
        }

        return tops;
    }

    private void ApplyAnnotation(RuleAst? target, string trimmed)
    {
        if (target is null)
        {
            return;
        }

        // Form: `!:kind<TAB or space>value`.
        int i = 2; // skip "!:"
        int j = i;
        while (j < trimmed.Length && trimmed[j] != '\t' && trimmed[j] != ' ')
        {
            j++;
        }

        string key = trimmed[i..j];
        string value = j < trimmed.Length ? trimmed[(j + 1)..].Trim() : string.Empty;

        switch (key)
        {
            case "mime":
                if (value.Length > 0)
                {
                    target.MimeType = value;
                    _stats.Annotations++;
                }

                break;
            case "ext":
                if (value.Length > 0)
                {
                    target.Extensions = NormalizeExt(value);
                    _stats.Annotations++;
                }

                break;
            case "encoding":
                if (value.Length > 0)
                {
                    target.Encoding = value;
                }

                break;
            case "strength":
                if (TryParseLong(value, out long s))
                {
                    target.Strength = (int)Math.Clamp(s, 0, 200);
                }

                break;
            default:
                // apple/transform/enc/etc. recorded but not modeled as routing signals yet.
                break;
        }
    }

    private static string NormalizeExt(string value)
    {
        // `!/exe` marks exclusions upstream; keep only positive extensions, comma-normalized.
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p.StartsWith('!') || p.Length == 0)
            {
                continue;
            }

            kept.Add(p);
        }

        return string.Join(',', kept);
    }

    private RuleAst? ParseLine(string line, ref RuleAst? lastEmitted)
    {
        int level = 0;
        int i = 0;
        while (i < line.Length && line[i] == '>')
        {
            level++;
            i++;
        }

        while (i < line.Length && (line[i] == '\t' || line[i] == ' '))
        {
            i++;
        }

        string rest = line[i..];
        var fields = SplitFields(rest, out string description);
        if (fields.Count == 0)
        {
            return null;
        }

        string offsetTok = fields.Count > 0 ? fields[0] : string.Empty;
        string typeTok = fields.Count > 1 ? fields[1] : string.Empty;
        string valueTok = fields.Count > 2 ? fields[2] : string.Empty;

        // Directives handled by keyword in the TYPE slot (name/use/default/skip/strength/clear/der/...).
        var rule = new RuleAst { Indent = level };

        ParseOffset(rule, offsetTok);

        if (typeTok.Length == 0)
        {
            _stats.SkippedMalformed++;
            return null;
        }

        // name template: `offset name templatename`
        if (typeTok == "name")
        {
            rule.Kind = MagicKind.None;
            rule.Relation = MagicRelation.Any;
            rule.Name = valueTok;
            _stats.Rules++;
            return rule;
        }

        if (typeTok == "use")
        {
            rule.Kind = MagicKind.None;
            rule.Relation = MagicRelation.Any;
            rule.UseName = valueTok;
            _stats.Rules++;
            return rule;
        }

        if (typeTok == "default")
        {
            rule.Kind = MagicKind.None;
            rule.Flags |= MagicFlags.Default;
            rule.Relation = MagicRelation.Any;
            rule.Description = DescriptionOf(rule, description);
            _stats.Rules++;
            return rule;
        }

        if (typeTok is "skip")
        {
            rule.Kind = MagicKind.None;
            rule.Flags |= MagicFlags.Consume;
            _stats.Rules++;
            return rule;
        }

        if (typeTok is "strength" or "clear" or "regex-not" or "transform" or "copy")
        {
            _stats.SkippedTransform++;
            return null;
        }

        if (typeTok == "der")
        {
            _stats.SkippedDer++;
            return null;
        }

        if (typeTok == "indirect")
        {
            // `indirect` as a TYPE is the CDF/OLE indirection template mechanism; not modeled yet.
            _stats.SkippedIndirectKeyword++;
            return null;
        }

        if (ParseTypeAndValue(rule, typeTok, valueTok, description))
        {
            if (rule.Description.Length == 0)
            {
                rule.Description = DescriptionOf(rule, description);
            }

            _stats.Rules++;
            return rule;
        }

        if (rule._skip == SkipKind.Range)
        {
            _stats.SkippedRange++;
        }

        return null;
    }

    private void ParseOffset(RuleAst rule, string tok)
    {
        if (tok.Length == 0)
        {
            return;
        }

        // Indirect: `(...)` optionally preceded by `&` (relative to end).
        if (tok.StartsWith("&", StringComparison.Ordinal))
        {
            rule.Flags |= MagicFlags.RelativeToEnd;
            tok = tok[1..];
        }

        if (tok.StartsWith("(", StringComparison.Ordinal) && tok.EndsWith(")", StringComparison.Ordinal))
        {
            ParseIndirectOffset(rule, tok[1..^1]);
            rule.Flags |= MagicFlags.Indirect;
            return;
        }

        if (tok.StartsWith("-", StringComparison.Ordinal))
        {
            rule.Flags |= MagicFlags.FromEnd;
            tok = tok[1..];
        }

        if (tok.StartsWith("&", StringComparison.Ordinal))
        {
            // another leading & after level handling: relative-to-end numeric
            tok = tok[1..];
        }

        if (TryParseLong(tok, out long off))
        {
            rule.Offset = off;
        }
    }

    private static void ParseIndirectOffset(RuleAst rule, string inner)
    {
        int dot = inner.IndexOf('.');
        if (dot < 0)
        {
            rule.IndirectBaseOffset = 0;
            return;
        }

        if (TryParseLong(inner[..dot], out long baseOff))
        {
            rule.IndirectBaseOffset = baseOff;
        }

        string remainder = inner[(dot + 1)..];
        bool invert = remainder.EndsWith("~", StringComparison.Ordinal);
        if (invert)
        {
            rule.IndirectMaskInvert = true;
            remainder = remainder[..^1];
        }

        int plus = remainder.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            if (TryParseLong(remainder[(plus + 1)..], out long add))
            {
                rule.IndirectAddend = (int)add;
            }

            remainder = remainder[..plus];
        }
        else
        {
            int amp = remainder.IndexOf('&', StringComparison.Ordinal);
            if (amp >= 0)
            {
                rule.HasIndirectMask = true;
                rule.IndirectMaskOp = MagicMaskOp.And;
                if (TryParseLong(remainder[(amp + 1)..], out long m))
                {
                    rule.IndirectMask = m;
                }

                remainder = remainder[..amp];
            }
        }

        if (MapNumberType(remainder, out MagicNumberType nt))
        {
            rule.IndirectType = nt;
        }
    }

    private bool ParseTypeAndValue(RuleAst rule, string typeTok, string valueTok, string description)
    {
        string baseType = typeTok;
        string modifiers = string.Empty;
        int slash = typeTok.IndexOf('/', StringComparison.Ordinal);
        bool stringFamily = typeTok.StartsWith("string", StringComparison.Ordinal)
            || typeTok.StartsWith("ustring", StringComparison.Ordinal)
            || typeTok.StartsWith("search", StringComparison.Ordinal)
            || typeTok.StartsWith("pstring", StringComparison.Ordinal)
            || typeTok.StartsWith("lestring16", StringComparison.Ordinal)
            || typeTok.StartsWith("bestring16", StringComparison.Ordinal)
            || typeTok.StartsWith("regex", StringComparison.Ordinal);

        if (slash >= 0)
        {
            baseType = typeTok[..slash];
            modifiers = typeTok[(slash + 1)..];
        }

        if (!stringFamily)
        {
            ExtractTypeMask(rule, baseType);
            baseType = rule.TempBaseType;
        }

        switch (baseType)
        {
            case "string":
                rule.Kind = MagicKind.String;
                ParseStringModifiers(rule, modifiers);
                if (SetPattern(rule, valueTok))
                {
                    return true;
                }

                return false;

            case "ustring":
                rule.Kind = MagicKind.String;
                if (SetPattern(rule, valueTok))
                {
                    return true;
                }

                return false;

            case "search":
                rule.Kind = MagicKind.Search;
                ParseStringModifiers(rule, modifiers);
                rule.SearchRange = ExtractDigits(modifiers);
                if (SetPattern(rule, valueTok))
                {
                    return true;
                }

                return false;

            case "pstring":
                rule.Kind = MagicKind.PString;
                ParsePStringModifiers(rule, modifiers);
                return SetPattern(rule, valueTok);

            case "lestring16":
                rule.Kind = MagicKind.String16Le;
                ParseStringModifiers(rule, modifiers);
                return SetPattern16(rule, valueTok, bigEndian: false);

            case "bestring16":
                rule.Kind = MagicKind.String16Be;
                ParseStringModifiers(rule, modifiers);
                return SetPattern16(rule, valueTok, bigEndian: true);

            case "regex":
                rule.Kind = MagicKind.Regex;
                ParseRegexModifiers(rule, modifiers);
                rule.RegexPattern = valueTok.Length == 0 ? "x" : UnescapeRegex(valueTok);
                rule.Description = DescriptionOf(rule, description);
                return true;

            case "guid":
                rule.Kind = MagicKind.Guid;
                rule.Pattern = ParseGuid(valueTok);
                rule.Description = DescriptionOf(rule, description);
                return true;

            default:
                if (MapNumberType(baseType, out MagicNumberType nt))
                {
                    rule.Kind = MagicKind.Number;
                    rule.NumberType = nt;
                    if (!ParseNumericValue(rule, valueTok))
                    {
                        // Value could not be modelled (range set or malformed); drop rather than emit Value=0
                        // which would false-match any file whose bytes at this offset are zero.
                        if (rule._skip != SkipKind.Range)
                        {
                            _stats.SkippedMalformed++;
                        }

                        return false;
                    }

                    rule.Description = DescriptionOf(rule, description);
                    return true;
                }

                _stats.SkippedUnknownType++;
                _stats.UnknownTypes.TryGetValue(baseType, out int c);
                _stats.UnknownTypes[baseType] = c + 1;
                return false;
        }
    }

    private static void ExtractTypeMask(RuleAst rule, string baseType)
    {
        int idx = -1;
        for (int k = 0; k < baseType.Length; k++)
        {
            if (baseType[k] is '&' or '|' or '^' or '+' or '*' or '%' or '-')
            {
                idx = k;
                break;
            }
        }

        if (idx < 0)
        {
            rule.TempBaseType = baseType;
            return;
        }

        char op = baseType[idx];
        string maskStr = baseType[(idx + 1)..];
        bool invert = maskStr.StartsWith("~", StringComparison.Ordinal);
        if (invert)
        {
            maskStr = maskStr[1..];
        }

        rule.HasMask = true;
        rule.MaskInvert = invert;
        rule.MaskOp = op switch
        {
            '&' => MagicMaskOp.And,
            '|' => MagicMaskOp.Or,
            '^' => MagicMaskOp.Xor,
            '+' => MagicMaskOp.Add,
            '*' => MagicMaskOp.Multiply,
            '%' => MagicMaskOp.Modulo,
            _ => MagicMaskOp.Subtract,
        };

        if (TryParseLong(maskStr, out long m))
        {
            rule.Mask = m;
        }

        rule.TempBaseType = baseType[..idx];
    }

    private static bool SetPattern(RuleAst rule, string valueTok)
    {
        if (valueTok.Length == 1 && valueTok[0] == 'x')
        {
            rule.Relation = MagicRelation.Any;
            rule.Pattern = Array.Empty<byte>();
            return true;
        }

        if (valueTok.Contains('['))
        {
            rule._skip = SkipKind.Range;
            return false;
        }

        rule.Pattern = ParseStringPattern(valueTok);
        DetectRelation(rule, valueTok);
        return true;
    }

    private static bool SetPattern16(RuleAst rule, string valueTok, bool bigEndian)
    {
        if (valueTok.Length == 1 && valueTok[0] == 'x')
        {
            rule.Relation = MagicRelation.Any;
            rule.Pattern = Array.Empty<byte>();
            return true;
        }

        if (valueTok.Contains('['))
        {
            rule._skip = SkipKind.Range;
            return false;
        }

        byte[] raw = ParseStringPattern(valueTok);
        var b = new List<byte>(raw.Length * 2);
        foreach (byte x in raw)
        {
            if (bigEndian)
            {
                b.Add(0);
                b.Add(x);
            }
            else
            {
                b.Add(x);
                b.Add(0);
            }
        }

        rule.Pattern = b.ToArray();
        return true;
    }

    private static void DetectRelation(RuleAst rule, string valueTok)
    {
        if (valueTok.Length > 0)
        {
            switch (valueTok[0])
            {
                case '!':
                    rule.Relation = MagicRelation.NotEqual;
                    return;
                case '<':
                    rule.Relation = MagicRelation.LessThan;
                    return;
                case '>':
                    rule.Relation = MagicRelation.GreaterThan;
                    return;
                case '=':
                    rule.Relation = MagicRelation.Equal;
                    return;
            }
        }
    }

    private static bool ParseNumericValue(RuleAst rule, string valueTok)
    {
        string v = valueTok;
        if (v.Length > 0)
        {
            char first = v[0];
            if (first == '!')
            {
                rule.Relation = MagicRelation.NotEqual;
                v = v[1..];
            }
            else if (first == '<')
            {
                rule.Relation = MagicRelation.LessThan;
                v = v[1..];
            }
            else if (first == '>')
            {
                rule.Relation = MagicRelation.GreaterThan;
                v = v[1..];
            }
            else if (first == '=')
            {
                rule.Relation = MagicRelation.Equal;
                v = v[1..];
            }
            else if (first == '&')
            {
                rule.Relation = MagicRelation.BitAnd;
                v = v[1..];
            }
            else if (first == '^')
            {
                rule.Relation = MagicRelation.BitXor;
                v = v[1..];
            }
        }

        if (v.Length == 1 && v[0] == 'x')
        {
            rule.Relation = MagicRelation.Any;
            return true;
        }

        if (v.Contains('['))
        {
            rule._skip = SkipKind.Range;
            return false;
        }

        // Character literal value, e.g. 'A' or '\0' for a byte/short operand.
        if (v.Length >= 3 && v[0] == '\'' && v[^1] == '\'')
        {
            if (TryCharLiteral(v[1..^1], out long code))
            {
                rule.Value = code;
                return true;
            }

            return false;
        }

        if (TryParseLong(v, out long n))
        {
            rule.Value = n;
            return true;
        }

        return false;
    }

    private static bool TryCharLiteral(string inner, out long value)
    {
        value = 0;
        if (inner.Length == 1)
        {
            value = inner[0];
            return true;
        }

        if (inner.Length >= 2 && inner[0] == '\\')
        {
            char e = inner[1];
            value = e switch
            {
                '0' => 0,
                'a' => 7,
                'b' => 8,
                'f' => 12,
                'n' => 10,
                'r' => 13,
                't' => 9,
                'v' => 11,
                _ => e,
            };
            return true;
        }

        return false;
    }

    private static string DescriptionOf(RuleAst rule, string description)
    {
        string d = description;
        if (d.StartsWith("\\b", StringComparison.Ordinal))
        {
            rule.Flags |= MagicFlags.NoSpace;
            d = d[2..];
        }

        return d;
    }

    private static void ParseStringModifiers(RuleAst rule, string modifiers)
    {
        foreach (char c in modifiers)
        {
            switch (c)
            {
                case 'c' or 'C':
                    rule.StringFlags |= MagicStringFlags.IgnoreCase;
                    break;
                case 'W':
                    rule.StringFlags |= MagicStringFlags.CompactWhitespace;
                    break;
                case 'w':
                    rule.StringFlags |= MagicStringFlags.OptionalWhitespace;
                    break;
                case 'f':
                    rule.StringFlags |= MagicStringFlags.FullWord;
                    break;
                case 'T':
                    rule.StringFlags |= MagicStringFlags.Trim;
                    break;
            }
        }
    }

    private static void ParsePStringModifiers(RuleAst rule, string modifiers)
    {
        rule.PStringLengthBytes = 1;
        rule.PStringBigEndian = false;
        foreach (char c in modifiers)
        {
            switch (c)
            {
                case 'B':
                    rule.PStringLengthBytes = 1;
                    break;
                case 'H':
                    rule.PStringLengthBytes = 2;
                    rule.PStringBigEndian = true;
                    break;
                case 'h':
                    rule.PStringLengthBytes = 2;
                    break;
                case 'L':
                    rule.PStringLengthBytes = 4;
                    rule.PStringBigEndian = true;
                    break;
                case 'l':
                    rule.PStringLengthBytes = 4;
                    break;
                case 'c' or 'C':
                    rule.StringFlags |= MagicStringFlags.IgnoreCase;
                    break;
            }
        }
    }

    private static void ParseRegexModifiers(RuleAst rule, string modifiers)
    {
        foreach (char c in modifiers)
        {
            switch (c)
            {
                case 'i':
                    rule.RegexFlags |= MagicRegexFlags.IgnoreCase;
                    break;
                case 's':
                    rule.RegexFlags |= MagicRegexFlags.SingleLine;
                    break;
                case 'm':
                    rule.RegexFlags |= MagicRegexFlags.Multiline;
                    break;
                case 'c' or 'C':
                    rule.RegexFlags |= MagicRegexFlags.IgnoreCase;
                    break;
            }
        }
    }

    private static int ExtractDigits(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (char.IsAsciiDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.Length == 0 ? 0 : int.Parse(sb.ToString(), CultureInfo.InvariantCulture);
    }

    private static byte[]? ParseGuid(string value)
    {
        // {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx} or with wildcards 'x'.
        string clean = value.Trim('{', '}').Replace("-", string.Empty, StringComparison.Ordinal);
        if (clean.Length != 32)
        {
            return null;
        }

        var bytes = new byte[16];
        for (int k = 0; k < 16; k++)
        {
            char hi = clean[k * 2];
            char lo = clean[(k * 2) + 1];
            bytes[k] = (hi == 'x' || hi == 'X' || lo == 'x' || lo == 'X')
                ? (byte)0xFF
                : (byte)((HexVal(hi) << 4) | HexVal(lo));
        }

        return bytes;
    }

    private static string UnescapeRegex(string v)
    {
        // Upstream uses \xNN and \0NN in regex text; keep most verbatim, decode \xNN/\0NN to C# equivalents.
        if (v.IndexOf('\\') < 0)
        {
            return v;
        }

        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length && v[i + 1] == 'x' && i + 3 < v.Length)
            {
                sb.Append("\\x").Append(v[i + 2]).Append(v[i + 3]);
                i += 3;
            }
            else
            {
                sb.Append(v[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>Split a rule line into offset/type/value tokens (whitespace-aware, keeps \X) and the raw remainder as description.</summary>
    private static List<string> SplitFields(string rest, out string description)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        int i = 0;
        int fieldsWanted = 3;
        int consumed = 0;
        int descStart = rest.Length;

        while (i < rest.Length)
        {
            char c = rest[i];
            if (c == '\\' && i + 1 < rest.Length)
            {
                sb.Append('\\');
                sb.Append(rest[++i]);
                i++;
                continue;
            }

            if (c == '\t' || c == ' ')
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    consumed++;
                    if (consumed == fieldsWanted)
                    {
                        // Skip further whitespace to the description start.
                        int k = i;
                        while (k < rest.Length && (rest[k] == ' ' || rest[k] == '\t'))
                        {
                            k++;
                        }

                        descStart = k;
                        i = k;
                        break;
                    }
                }

                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        description = tokens.Count >= fieldsWanted ? rest[descStart..].TrimEnd() : string.Empty;
        return tokens;
    }

    private static byte[] ParseStringPattern(string token)
    {
        var bytes = new List<byte>(token.Length);
        int i = 0;
        // Strip a leading relation prefix if present for numeric-like string tests? Strings keep value verbatim.
        while (i < token.Length)
        {
            char c = token[i];
            if (c == '\\' && i + 1 < token.Length)
            {
                char next = token[i + 1];
                if (next is >= '0' and <= '7')
                {
                    int value = 0;
                    int digits = 0;
                    int k = i + 1;
                    while (k < token.Length && digits < 3 && token[k] is >= '0' and <= '7')
                    {
                        value = (value << 3) | (token[k] - '0');
                        digits++;
                        k++;
                    }

                    bytes.Add(unchecked((byte)value));
                    i = k;
                    continue;
                }

                switch (next)
                {
                    case 'x' when i + 3 < token.Length && IsHex(token[i + 2]) && IsHex(token[i + 3]):
                        bytes.Add((byte)((HexVal(token[i + 2]) << 4) | HexVal(token[i + 3])));
                        i += 4;
                        continue;
                    case 'a': bytes.Add(7); i += 2; continue;
                    case 'b': bytes.Add(8); i += 2; continue;
                    case 'f': bytes.Add(12); i += 2; continue;
                    case 'n': bytes.Add(10); i += 2; continue;
                    case 'r': bytes.Add(13); i += 2; continue;
                    case 't': bytes.Add(9); i += 2; continue;
                    case 'v': bytes.Add(11); i += 2; continue;
                    case '\\': bytes.Add((byte)'\\'); i += 2; continue;
                    case ' ': bytes.Add((byte)' '); i += 2; continue;
                    default: bytes.Add((byte)'\\'); i++; continue;
                }
            }

            bytes.Add(unchecked((byte)c));
            i++;
        }

        return bytes.ToArray();
    }

    private static bool TryParseLong(string token, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        token = token.Trim();

        // Strip C-style integer literal suffixes (L/U/LL/UL/...) so constants like
        // 0x1b031336L or 100u parse. Only trailing l/L/u/U are removed and a digit must remain.
        if (token.Length > 1 && (char.IsDigit(token[^1]) is false && (token[^1] is 'l' or 'L' or 'u' or 'U')))
        {
            int end = token.Length;
            while (end > 0 && (token[end - 1] is 'l' or 'L' or 'u' or 'U'))
            {
                end--;
            }

            int minLen = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
            if (end >= minLen)
            {
                token = token[..end];
            }
        }

        try
        {
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = long.Parse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return true;
            }

            if (token.StartsWith('&'))
            {
                token = token[1..];
            }

            if (token.Length > 1 && token[0] == '0')
            {
                value = Convert.ToInt64(token, 8);
                return true;
            }

            value = long.Parse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool MapNumberType(string t, out MagicNumberType nt)
    {
        // Handles base names and u/be/le/ube/ule/bu/lu prefixes and the single-letter indirect codes.
        switch (t)
        {
            case "byte" or "B" or "b": nt = MagicNumberType.Byte; return true;
            case "ubyte" or "UB" or "UBYTE": nt = MagicNumberType.UByte; return true;
            case "short": nt = MagicNumberType.Short; return true;
            case "ushort": nt = MagicNumberType.UShort; return true;
            case "leshort": nt = MagicNumberType.Leshort; return true;
            case "uleshort": nt = MagicNumberType.Uleshort; return true;
            case "beshort": nt = MagicNumberType.Beshort; return true;
            case "ubeshort": nt = MagicNumberType.Ubeshort; return true;
            case "long": nt = MagicNumberType.Long; return true;
            case "ulong": nt = MagicNumberType.ULong; return true;
            case "lelong": nt = MagicNumberType.Lelong; return true;
            case "ulelong": nt = MagicNumberType.Ulelong; return true;
            case "belong": nt = MagicNumberType.Belong; return true;
            case "ubelong": nt = MagicNumberType.Ubelong; return true;
            case "quad": nt = MagicNumberType.Quad; return true;
            case "uquad": nt = MagicNumberType.Uquad; return true;
            case "lequad": nt = MagicNumberType.Lequad; return true;
            case "ulequad": nt = MagicNumberType.Ulequad; return true;
            case "bequad": nt = MagicNumberType.Bequad; return true;
            case "ubequad": nt = MagicNumberType.Ubequad; return true;
            case "date": nt = MagicNumberType.Date; return true;
            case "ledate": nt = MagicNumberType.Ledate; return true;
            case "bedate": nt = MagicNumberType.Bedate; return true;
            case "ldate" or "leldate": nt = MagicNumberType.Ledate; return true;
            case "bldate" or "beldate": nt = MagicNumberType.Bedate; return true;
            // single-letter indirect codes
            case "h": nt = MagicNumberType.Leshort; return true;
            case "H": nt = MagicNumberType.Beshort; return true;
            case "s": nt = MagicNumberType.Short; return true;
            case "S": nt = MagicNumberType.Beshort; return true;
            case "l": nt = MagicNumberType.Lelong; return true;
            case "L": nt = MagicNumberType.Belong; return true;
            case "m": nt = MagicNumberType.Belong; return true;
            case "q": nt = MagicNumberType.Lequad; return true;
            case "Q": nt = MagicNumberType.Bequad; return true;
            case "c": nt = MagicNumberType.UByte; return true;
            case "C": nt = MagicNumberType.UByte; return true;
            default:
                // tolerate u/be/le prefix forms like "ube" -> treat as unknown
                nt = MagicNumberType.Long;
                return false;
        }
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };
}