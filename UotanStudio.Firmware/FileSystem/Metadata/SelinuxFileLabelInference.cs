using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.Core.Text;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.FileSystem.Metadata;

/// <summary>Infers file object contexts without inventing process domains or executable entrypoint types.
/// <para>推断文件对象上下文，且不虚构进程域或可执行入口类型。</para></summary>
internal sealed class SelinuxFileLabelInference
{
    private readonly Dictionary<string, PreservedInodeEntry> originals;
    private readonly Dictionary<(string Parent, int Kind), PreservedInodeEntry[]> siblings;
    private readonly List<PolicyIndex> policies = [];
    private readonly string? mount;
    private readonly bool systemAsRoot;
    private readonly bool labeled;
    private sealed record Rule(Regex Pattern, bool Literal, int Kind, string Label, int Order, string Prefix, string? Exact);

    internal SelinuxFileLabelInference(string root, Dictionary<string, PreservedInodeEntry> originals,
        Dictionary<string, string> projection, string? partition, Dictionary<string, string>? imagePaths = null)
    {
        this.originals = originals;
        siblings = originals.Where(p => p.Key.Length != 0).GroupBy(p => (Parent(p.Key), p.Value.Mode & 0xF000))
            .ToDictionary(g => g.Key, g => g.Select(p => p.Value).ToArray());
        labeled = originals.Values.Any(e => e.Xattrs?.ContainsKey("security.selinux") == true);
        if (partition?.EndsWith("_a", StringComparison.Ordinal) == true || partition?.EndsWith("_b", StringComparison.Ordinal) == true)
            partition = partition[..^2];
        mount = partition switch
        {
            "system" or "vendor" or "product" or "system_ext" or "odm" or "oem"
                or "vendor_dlkm" or "odm_dlkm" or "system_dlkm" => "/" + partition,
            "userdata" => "/data",
            "root" or "ramdisk" => "",
            _ => null
        };
        systemAsRoot = partition == "system" && originals.ContainsKey("system") && !originals.ContainsKey("bin");
        if (mount is null) return;
        var originalPaths = imagePaths?.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        long budget = 8 * 1024 * 1024;
        var count = 0;
        foreach (var hostPath in projection.Keys.Order(StringComparer.Ordinal))
        {
            var path = originalPaths?.GetValueOrDefault(hostPath, hostPath) ?? hostPath;
            var name = FileSystemEditPlan.Name(path);
            if (!(path == "file_contexts" || (path.StartsWith("etc/selinux/", StringComparison.Ordinal)
                || path.StartsWith("system/etc/selinux/", StringComparison.Ordinal))
                && (name == "file_contexts" || name.EndsWith("_file_contexts", StringComparison.Ordinal)))) continue;
            if (!projection[hostPath].StartsWith("file:", StringComparison.Ordinal)) continue;
            var fullPath = Path.Combine(root, hostPath.Replace('/', Path.DirectorySeparatorChar));
            using var stream = File.OpenRead(fullPath);
            budget -= stream.Length;
            if (budget < 0) throw new FileSystemCorruptedException("SELinux text policies exceed the 8 MiB limit.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var rules = new List<Rule>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 16384) throw new FileSystemCorruptedException("SELinux rule exceeds the line limit.");
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (++count > 32768) throw new FileSystemCorruptedException("Too many SELinux path rules.");
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length is not (2 or 3) || !parts[0].StartsWith('/'))
                    throw new FileSystemCorruptedException($"Invalid SELinux rule in {path}.");
                var kind = parts.Length == 2 ? 0 : parts[1] switch
                {
                    "--" => 0x8000,
                    "-d" => 0x4000,
                    "-l" => 0xA000,
                    "-b" => 0x6000,
                    "-c" => 0x2000,
                    "-p" => 0x1000,
                    "-s" => 0xC000,
                    _ => throw new FileSystemCorruptedException($"Invalid SELinux file kind in {path}.")
                };
                var label = parts[^1];
                if (label != "<<none>>" && !IsObjectContext(label))
                    throw new FileSystemCorruptedException($"Invalid SELinux file object context in {path}: {label}");
                var pattern = parts[0];
                var translated = SelinuxPolicyRegexTranslator.Translate(pattern, path);
                var literal = !Regex.IsMatch(translated, @"(?<!\\)[.\^$?*+|\[({]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                try
                {
                    var (prefix, exact) = AnalyzeLiteral(translated);
                    var bytes = AsByteText(translated);
                    rules.Add(new(new Regex("\\A(?:" + bytes + ")\\z", RegexOptions.NonBacktracking | RegexOptions.Singleline
                        | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), literal, kind, label, rules.Count,
                        AsByteText(prefix), exact is null ? null : AsByteText(exact)));
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException)
                {
                    throw new FileSystemUnsupportedFeatureException($"Unsupported SELinux regex in {path}: {pattern}", error);
                }
            }
            policies.Add(BuildIndex(rules));
        }
    }

    internal byte[]? Infer(string path, bool directory, bool executable = false)
    {
        var kind = directory ? 0x4000 : 0x8000;
        var runtimePath = (systemAsRoot ? "" : mount) + "/" + path;
        var key = AsByteText(runtimePath);
        string? policyLabel = null;
        foreach (var policy in policies)
        {
            var match = policy.Match(key, kind);
            if (match is null)
            {
                continue;
            }
            if (policyLabel is null)
            {
                policyLabel = match.Label;
            }
            else if (!string.Equals(policyLabel, match.Label, StringComparison.Ordinal))
            {
                throw new FileSystemCorruptedException($"Conflicting SELinux policy sources for {runtimePath}.");
            }
        }
        if (policyLabel is not null)
        {
            if (policyLabel == "<<none>>") throw new InvalidDataException($"SELinux policy explicitly excludes labeling {runtimePath}.");
            return Encode(policyLabel);
        }
        var parent = Parent(path);
        if (siblings.TryGetValue((parent, kind), out var donors))
        {
            var extension = Path.GetExtension(path);
            var compatible = executable ? donors.Where(e => (e.Mode & 0x49) != 0) : donors.AsEnumerable();
            var label = Consensus(compatible.Where(e => Path.GetExtension(e.Path) == extension)) ?? Consensus(compatible);
            if (label is not null) return Encode(label);
        }
        while (true)
        {
            if (originals.TryGetValue(parent, out var entry) && SafeLabel(entry) is { } label) return Encode(label);
            if (parent.Length == 0) break;
            parent = Parent(parent);
        }
        if (labeled) throw new InvalidDataException($"No unambiguous SELinux file context for {path}; add a firmware file_contexts rule.");
        return null;
    }

    private static string? Consensus(IEnumerable<PreservedInodeEntry> entries)
    {
        var labels = entries.Select(SafeLabel).Where(l => l is not null).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return labels.Length == 1 ? labels[0] : null;
    }
    private static string? SafeLabel(PreservedInodeEntry entry)
    {
        if (entry.Xattrs?.TryGetValue("security.selinux", out var raw) != true || raw is null) return null;
        var label = Encoding.UTF8.GetStringTrimNul(raw);
        if (!IsObjectContext(label)) return null;
        var type = label.Split(':')[2];
        return type.EndsWith("_exec", StringComparison.Ordinal) || type.EndsWith("_exec_t", StringComparison.Ordinal)
            || type is "unlabeled" or "unlabeled_t" ? null : label;
    }
    private static bool IsObjectContext(string label) => Regex.IsMatch(label,
        @"\A[A-Za-z0-9_]+:object_r:[A-Za-z0-9_]+:s[0-9]+(?:[,:\-A-Za-z0-9_.]*)\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static byte[] Encode(string label) => Encoding.UTF8.GetBytes(label + '\0');
    private static string Parent(string path) => path.LastIndexOf('/') is var i && i >= 0 ? path[..i] : "";

    /// <summary>
    /// Maps a runtime path onto the single-byte alphabet the compiled patterns use, keeping pure ASCII paths
    /// identity-mapped so lookups allocate nothing.
    /// <para>把运行时路径映射到已编译模式使用的单字节字符表；纯 ASCII 路径保持原样，使查找不产生分配。</para>
    /// </summary>
    private static string AsByteText(string value)
    {
        foreach (var c in value)
        {
            if (c > 0x7F)
            {
                return Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(value));
            }
        }
        return value;
    }

    /// <summary>
    /// Builds the per-policy lookup used by <see cref="Infer"/>: exact patterns indexed by their literal text and
    /// every remaining pattern kept in descending file order so the first acceptance is also the last match.
    /// <para>构建 <see cref="Infer"/> 使用的按策略索引：字面量模式按其文本建索引，其余模式按文件顺序倒序保留，
    /// 使第一个被接受的模式恰好就是最后一个匹配项。</para>
    /// </summary>
    private static PolicyIndex BuildIndex(List<Rule> rules)
    {
        Dictionary<string, List<Rule>>? exact = null;
        List<Rule> literalScan = [];
        List<Rule> regexScan = [];
        for (var index = rules.Count - 1; index >= 0; index--)
        {
            var rule = rules[index];
            if (rule.Exact is not null)
            {
                exact ??= new(StringComparer.Ordinal);
                if (!exact.TryGetValue(rule.Exact, out var bucket))
                {
                    bucket = [];
                    exact[rule.Exact] = bucket;
                }
                bucket.Add(rule);
            }
            else if (rule.Literal)
            {
                literalScan.Add(rule);
            }
            else
            {
                regexScan.Add(rule);
            }
        }
        return new PolicyIndex
        {
            Exact = exact,
            LiteralScan = [.. literalScan],
            RegexScan = [.. regexScan],
        };
    }

    /// <summary>
    /// Splits a translated pattern into the literal text every match must start with and, when the whole pattern is
    /// literal, that same text as the only string it accepts. Quantified tail characters are dropped because they may
    /// be absent; escaped letters, digits and whitespace are treated as unknown and end the prefix.
    /// <para>把已翻译模式拆成每个匹配都必须具备的字面量前缀，以及模式整体为字面量时唯一可匹配的文本。量词修饰的末尾字符
    /// 会被丢弃，因为它可能不出现；转义的字母、数字与空白按未知处理并终止前缀。</para>
    /// </summary>
    private static (string Prefix, string? Exact) AnalyzeLiteral(string pattern)
    {
        StringBuilder prefix = new(pattern.Length);
        var index = 0;
        while (index < pattern.Length)
        {
            var c = pattern[index];
            if (c == '\\')
            {
                if (index + 1 >= pattern.Length || !IsQuotedLiteral(pattern[index + 1]))
                {
                    break;
                }
                prefix.Append(pattern[index + 1]);
                index += 2;
                continue;
            }
            if (IsMetacharacter(c))
            {
                if (c is '*' or '?' or '{' && prefix.Length > 0)
                {
                    prefix.Length--;
                }
                break;
            }
            prefix.Append(c);
            index++;
        }
        return (prefix.ToString(), index == pattern.Length ? prefix.ToString() : null);
    }
    private static bool IsMetacharacter(char c) => c is '.' or '$' or '^' or '?' or '*' or '+' or '|' or '[' or ']'
        or '(' or ')' or '{' or '}' or '<' or '>';
    private static bool IsQuotedLiteral(char c) => c == '\\' || c >= '!' && c <= '~' && !char.IsAsciiLetterOrDigit(c);

    /// <summary>
    /// Resolves the rule a policy contributes for one runtime path, keeping the original precedence: the last literal
    /// rule wins, and regular rules are consulted only when no literal rule matches.
    /// <para>解析某策略对一个运行时路径贡献的规则，保持原有优先级：最后一个字面量规则生效，只有在没有任何字面量规则
    /// 匹配时才查阅正则规则。</para>
    /// </summary>
    private sealed class PolicyIndex
    {
        internal required Dictionary<string, List<Rule>>? Exact { get; init; }
        internal required Rule[] LiteralScan { get; init; }
        internal required Rule[] RegexScan { get; init; }

        internal Rule? Match(string key, int kind) => BestLiteral(key, kind) ?? BestScan(RegexScan, key, kind);

        private Rule? BestLiteral(string key, int kind)
        {
            Rule? best = null;
            if (Exact is not null && Exact.TryGetValue(key, out var bucket))
            {
                foreach (var rule in bucket)
                {
                    if (rule.Kind == 0 || rule.Kind == kind)
                    {
                        best = rule;
                        break;
                    }
                }
            }
            var scanned = BestScan(LiteralScan, key, kind);
            if (scanned is not null && (best is null || scanned.Order > best.Order))
            {
                best = scanned;
            }
            return best;
        }

        private static Rule? BestScan(Rule[] rules, string key, int kind)
        {
            foreach (var rule in rules)
            {
                if (rule.Kind != 0 && rule.Kind != kind)
                {
                    continue;
                }
                if (key.StartsWith(rule.Prefix, StringComparison.Ordinal) && rule.Pattern.IsMatch(key))
                {
                    return rule;
                }
            }
            return null;
        }
    }
}