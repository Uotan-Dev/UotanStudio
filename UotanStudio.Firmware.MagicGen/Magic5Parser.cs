using System.Globalization;
using System.Text;
using UotanStudio.Firmware.Core.Magic;

namespace UotanStudio.Firmware.MagicGen;

/// <summary>
/// Parses a magic(5) fragment into a <see cref="RuleAst"/> tree. Line-oriented; a failing line
/// is counted under a category in <see cref="Stats"/> and skipped, never aborting the corpus.
/// <para>把 magic(5) 文本片段解析为 <see cref="RuleAst"/> 树。逐行解析，异常行按类别计入
/// <see cref="Stats"/> 并跳过，绝不因单行失败中断整个语料。</para>
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

    /// <summary>
    /// Identifies the current parse segment for diagnostics; set by the caller before parsing.
    /// <para>当前解析片段的标识，用于诊断；由调用方在解析前设置。</para>
    /// </summary>
    public string CurrentSource = string.Empty;

    /// <summary>
    /// Parses one fragment's text into its top-level rule list.
    /// <para>解析单个片段文本，返回顶层规则列表。</para>
    /// </summary>
    /// <param name="text">
    /// Fragment text read line by line; empty lines and <c>#</c> comments are skipped, malformed lines are
    /// counted and dropped, and empty text yields an empty list. A <see langword="null"/> text is rejected
    /// before reading.
    /// <para>逐行读取的片段文本；空行与 <c>#</c> 注释被跳过，格式非法的行被计数后丢弃，空文本返回空列表；
    /// <see langword="null"/> 文本在读取前即被拒绝。</para>
    /// </param>
    public List<RuleAst> ParseFragment(string text)
    {
        var tops = new List<RuleAst>();
        var stack = new Stack<(int level, RuleAst rule)>();
        RuleAst? lastEmitted = null;

        using var sr = new StringReader(text);
        string? line;
        var lineNo = 0;
        while ((line = sr.ReadLine()) is not null)
        {
            lineNo++;
            var trimmed = line.TrimStart('\t', ' ');
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

        var i = 2;
        var j = i;
        while (j < trimmed.Length && trimmed[j] != '\t' && trimmed[j] != ' ')
        {
            j++;
        }

        var key = trimmed[i..j];
        var value = j < trimmed.Length ? trimmed[(j + 1)..].Trim() : string.Empty;

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
                if (TryParseLong(value, out var s))
                {
                    target.Strength = (int)Math.Clamp(s, 0, 200);
                }

                break;
            default:
                break;
        }
    }

    private static string NormalizeExt(string value)
    {
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
        var level = 0;
        var i = 0;
        while (i < line.Length && line[i] == '>')
        {
            level++;
            i++;
        }

        while (i < line.Length && (line[i] == '\t' || line[i] == ' '))
        {
            i++;
        }

        var rest = line[i..];
        var fields = SplitFields(rest, out var description);
        if (fields.Count == 0)
        {
            return null;
        }

        var offsetTok = fields.Count > 0 ? fields[0] : string.Empty;
        var typeTok = fields.Count > 1 ? fields[1] : string.Empty;
        var valueTok = fields.Count > 2 ? fields[2] : string.Empty;

        var rule = new RuleAst { Indent = level };

        ParseOffset(rule, offsetTok);

        if (typeTok.Length == 0)
        {
            _stats.SkippedMalformed++;
            return null;
        }

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
            tok = tok[1..];
        }

        if (TryParseLong(tok, out var off))
        {
            rule.Offset = off;
        }
    }

    private static void ParseIndirectOffset(RuleAst rule, string inner)
    {
        var dot = inner.IndexOf('.');
        if (dot < 0)
        {
            rule.IndirectBaseOffset = 0;
            return;
        }

        if (TryParseLong(inner[..dot], out var baseOff))
        {
            rule.IndirectBaseOffset = baseOff;
        }

        var remainder = inner[(dot + 1)..];
        var invert = remainder.EndsWith("~", StringComparison.Ordinal);
        if (invert)
        {
            rule.IndirectMaskInvert = true;
            remainder = remainder[..^1];
        }

        var plus = remainder.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            if (TryParseLong(remainder[(plus + 1)..], out var add))
            {
                rule.IndirectAddend = (int)add;
            }

            remainder = remainder[..plus];
        }
        else
        {
            var amp = remainder.IndexOf('&', StringComparison.Ordinal);
            if (amp >= 0)
            {
                rule.HasIndirectMask = true;
                rule.IndirectMaskOp = MagicMaskOp.And;
                if (TryParseLong(remainder[(amp + 1)..], out var m))
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
        var baseType = typeTok;
        var modifiers = string.Empty;
        var slash = typeTok.IndexOf('/', StringComparison.Ordinal);
        var stringFamily = typeTok.StartsWith("string", StringComparison.Ordinal)
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
                return SetPattern(rule, valueTok);

            case "ustring":
                rule.Kind = MagicKind.String;
                return SetPattern(rule, valueTok);

            case "search":
                rule.Kind = MagicKind.Search;
                ParseStringModifiers(rule, modifiers);
                rule.SearchRange = ExtractDigits(modifiers);
                return SetPattern(rule, valueTok);

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
                _stats.UnknownTypes.TryGetValue(baseType, out var c);
                _stats.UnknownTypes[baseType] = c + 1;
                return false;
        }
    }

    private static void ExtractTypeMask(RuleAst rule, string baseType)
    {
        var idx = -1;
        for (var k = 0; k < baseType.Length; k++)
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

        var op = baseType[idx];
        var maskStr = baseType[(idx + 1)..];
        var invert = maskStr.StartsWith("~", StringComparison.Ordinal);
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

        if (TryParseLong(maskStr, out var m))
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
            rule.Pattern = [];
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
            rule.Pattern = [];
            return true;
        }

        if (valueTok.Contains('['))
        {
            rule._skip = SkipKind.Range;
            return false;
        }

        var raw = ParseStringPattern(valueTok);
        var b = new List<byte>(raw.Length * 2);
        foreach (var x in raw)
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

        rule.Pattern = [.. b];
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
        var v = valueTok;
        if (v.Length > 0)
        {
            var first = v[0];
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

        if (v.Length >= 3 && v[0] == '\'' && v[^1] == '\'')
        {
            if (TryCharLiteral(v[1..^1], out var code))
            {
                rule.Value = code;
                return true;
            }

            return false;
        }

        if (TryParseLong(v, out var n))
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
            var e = inner[1];
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
        var d = description;
        if (d.StartsWith("\\b", StringComparison.Ordinal))
        {
            rule.Flags |= MagicFlags.NoSpace;
            d = d[2..];
        }

        return d;
    }

    private static void ParseStringModifiers(RuleAst rule, string modifiers)
    {
        foreach (var c in modifiers)
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
        foreach (var c in modifiers)
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
        foreach (var c in modifiers)
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
        foreach (var c in s)
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
        var clean = value.Trim('{', '}').Replace("-", string.Empty, StringComparison.Ordinal);
        if (clean.Length != 32)
        {
            return null;
        }

        var bytes = new byte[16];
        for (var k = 0; k < 16; k++)
        {
            var hi = clean[k * 2];
            var lo = clean[(k * 2) + 1];
            bytes[k] = (hi == 'x' || hi == 'X' || lo == 'x' || lo == 'X')
                ? (byte)0xFF
                : (byte)((HexVal(hi) << 4) | HexVal(lo));
        }

        return bytes;
    }

    private static string UnescapeRegex(string v)
    {
        if (v.IndexOf('\\') < 0)
        {
            return v;
        }

        var sb = new StringBuilder(v.Length);
        for (var i = 0; i < v.Length; i++)
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
        var i = 0;
        var fieldsWanted = 3;
        var consumed = 0;
        var descStart = rest.Length;

        while (i < rest.Length)
        {
            var c = rest[i];
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
                        var k = i;
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
        var i = 0;
        while (i < token.Length)
        {
            var c = token[i];
            if (c == '\\' && i + 1 < token.Length)
            {
                var next = token[i + 1];
                if (next is >= '0' and <= '7')
                {
                    var value = 0;
                    var digits = 0;
                    var k = i + 1;
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

        return [.. bytes];
    }

    private static bool TryParseLong(string token, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        token = token.Trim();

        if (token.Length > 1 && (char.IsDigit(token[^1]) is false && (token[^1] is 'l' or 'L' or 'u' or 'U')))
        {
            var end = token.Length;
            while (end > 0 && (token[end - 1] is 'l' or 'L' or 'u' or 'U'))
            {
                end--;
            }

            var minLen = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
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