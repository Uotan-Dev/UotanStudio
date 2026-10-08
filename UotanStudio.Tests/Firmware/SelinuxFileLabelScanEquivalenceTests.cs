using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Pins the indexed rule lookup against a re-implementation of the previous linear regex scan.</summary>
public sealed class SelinuxFileLabelScanEquivalenceTests : IDisposable
{
    private const string FallbackLabel = "u:object_r:rootfs:s0";

    private static readonly string[] RuleCorpus =
    [
        "/system/bin/new -- u:object_r:exact_file:s0",
        "/system/bin/new -d u:object_r:exact_dir:s0",
        "/system/bin/older -- u:object_r:superseded_file:s0",
        "/system/bin/ord -- u:object_r:ord_first:s0",
        "/system/bin/ord -d u:object_r:ord_dir_last:s0",
        @"/system/escaped\.dot -- u:object_r:escaped_exact:s0",
        "/system/bin(/.*)? u:object_r:optional_group:s0",
        "/system/bin/.* -- u:object_r:bin_any_file:s0",
        "/system/bin/.* -d u:object_r:bin_any_dir:s0",
        "/system/.*\\.so -- u:object_r:shared_object:s0",
        "/system/b?n/x -- u:object_r:quantified_char:s0",
        "/system/bin/x* -- u:object_r:star_tails:s0",
        "/system/etc/[a-z]+ -- u:object_r:class_range:s0",
        "/system/bin/[[:digit:]]+ -- u:object_r:posix_digit:s0",
        @"/system/bin/\w+ -- u:object_r:perl_word:s0",
        "/(vendor|system)/bin/alt -- u:object_r:alternation:s0",
        "/system/abc[0-9]* -- u:object_r:class_tail:s0",
        "/system/deep/nested/[0-9a-f]*\\.conf -- u:object_r:nested_conf:s0",
        "/system/utf8/é* -- u:object_r:utf8_rule:s0",
        "/system/exact/leaf -- u:object_r:last_exact:s0",
    ];

    private static readonly string[] ProbePaths =
    [
        "bin/new", "bin/older", "bin/ord", "bin/sh", "bin/alt", "bin/x55", "bin/abc",
        "bin/", "bin/x", "bin/xx", "bin/12", "bin/a.b", "bin/a.so", "bin/nested/leaf.so", "bin/é",
        "escaped/dot", "escaped.dot", "bi/n/x", "etc/ab", "etc/12", "deep/nested/aa.conf", "deep/nested/zz.txt",
        "utf8/", "utf8/e", "utf8/é", "exact/leaf", "unmatched/deep/path", "",
    ];

    private readonly string root = Path.Combine(Path.GetTempPath(), "uotan-selinux-scan-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> projection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreservedInodeEntry> originals = new(StringComparer.Ordinal)
    {
        [""] = Entry("", "rootfs", true),
        ["bin"] = Entry("bin", "donor_file", true),
    };

    [Fact]
    public void IndexedLookup_MatchesLinearRegexScan_ForEveryPathAndFileKind()
    {
        AssertNoDivergence(RuleCorpus, ProbePaths);
    }

    [Fact]
    public void IndexedLookup_MatchesLinearRegexScan_ForQuantifiedNonAsciiTails()
    {
        string[] corpus =
        [
            "/system/utf8/é* -- u:object_r:utf8_star:s0",
            "/system/utf8/é/leaf -- u:object_r:utf8_leaf:s0",
            "/system/utf8/ö+ u:object_r:utf8_plus:s0",
            "/system/utf8/naïve -- u:object_r:utf8_exact:s0",
        ];
        string[] paths = ["utf8/", "utf8/é", "utf8/éé", "utf8/é/leaf", "utf8/é/x", "utf8/x", "utf8/ö", "utf8/öx", "utf8/naïve", "utf8/naive"];
        AssertNoDivergence(corpus, paths);
    }

    [Fact]
    public void IndexedLookup_PrefersLastLiteralRuleOverLaterRegexRule()
    {
        WritePolicy(RuleCorpus);
        var oracle = BuildOracle(RuleCorpus);
        var inference = new SelinuxFileLabelInference(root, originals, projection, "system");
        Assert.Equal(OracleMatch(oracle, ByteText("/system/bin/new"), 0x8000), Decode(inference.Infer("bin/new", false)));
        Assert.Equal(OracleMatch(oracle, ByteText("/system/bin/new"), 0x4000), Decode(inference.Infer("bin/new", true)));
        Assert.Equal(OracleMatch(oracle, ByteText("/system/bin/ord"), 0x4000), Decode(inference.Infer("bin/ord", true)));
        Assert.Equal(OracleMatch(oracle, ByteText("/system/bin/é"), 0x8000), Decode(inference.Infer("bin/é", false)));
        Assert.Equal(OracleMatch(oracle, ByteText("/system/escaped.dot"), 0x8000), Decode(inference.Infer("escaped.dot", false)));
    }

    [Fact]
    public void IndexedLookup_FallsBackToInheritanceWhenNoRuleMatches()
    {
        WritePolicy(RuleCorpus);
        var oracle = BuildOracle(RuleCorpus);
        var inference = new SelinuxFileLabelInference(root, originals, projection, "system");
        Assert.Null(OracleMatch(oracle, ByteText("/system/unmatched/deep/path"), 0x8000));
        Assert.Equal(FallbackLabel, Decode(inference.Infer("unmatched/deep/path", false)));
    }

    private void AssertNoDivergence(string[] corpus, string[] paths)
    {
        WritePolicy(corpus);
        var oracle = BuildOracle(corpus);
        var inference = new SelinuxFileLabelInference(root, originals, projection, "system");
        var policyLabels = corpus.Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1]).ToHashSet(StringComparer.Ordinal);
        var divergences = new List<string>();
        foreach (var path in paths)
        {
            foreach (var directory in new[] { false, true })
            {
                var expected = OracleMatch(oracle, ByteText("/system/" + path), directory ? 0x4000 : 0x8000);
                var actual = Decode(inference.Infer(path, directory));
                if (expected is not null && actual != expected)
                {
                    divergences.Add($"{path} directory={directory} expected={expected} actual={actual}");
                }
                if (expected is null && actual is not null && policyLabels.Contains(actual))
                {
                    divergences.Add($"{path} directory={directory} expected=inheritance actual={actual}");
                }
            }
        }
        Assert.Equal(string.Empty, string.Join(" | ", divergences));
    }

    private void WritePolicy(IEnumerable<string> lines, string name = "plat_file_contexts")
    {
        var relative = "etc/selinux/" + name;
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join(Environment.NewLine, lines), new UTF8Encoding(false));
        projection[relative] = "file:policy";
    }

    private static List<OracleRule> BuildOracle(IEnumerable<string> lines)
    {
        var result = new List<OracleRule>();
        var order = 0;
        foreach (var raw in lines)
        {
            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var kind = parts.Length == 2 ? 0 : parts[1] switch
            {
                "--" => 0x8000,
                "-d" => 0x4000,
                _ => throw new InvalidOperationException(parts[1]),
            };
            var translated = SelinuxPolicyRegexTranslator.Translate(parts[0], "plat_file_contexts");
            var literal = !Regex.IsMatch(translated, @"(?<!\\)[.\^$?*+|\[({]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var bytes = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(translated));
            var pattern = new Regex("\\A(?:" + bytes + ")\\z", RegexOptions.NonBacktracking | RegexOptions.Singleline
                | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            result.Add(new OracleRule(pattern, literal, kind, parts[^1], order));
            order++;
        }
        return result;
    }

    private static string? OracleMatch(List<OracleRule> rules, string key, int kind)
    {
        var match = rules.LastOrDefault(r => r.Literal && (r.Kind == 0 || r.Kind == kind) && r.Pattern.IsMatch(key))
            ?? rules.LastOrDefault(r => !r.Literal && (r.Kind == 0 || r.Kind == kind) && r.Pattern.IsMatch(key));
        return match?.Label;
    }

    private static string ByteText(string value) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(value));

    private static string? Decode(byte[]? label) => label is null ? null : Encoding.UTF8.GetString(label).TrimEnd('\0');

    private static PreservedInodeEntry Entry(string path, string type, bool directory) => new()
    {
        Path = path,
        Mode = (ushort)(directory ? 0x41ED : 0x81A4),
        Xattrs = new() { ["security.selinux"] = Encoding.UTF8.GetBytes("u:object_r:" + type + ":s0\0") },
    };

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private sealed record OracleRule(Regex Pattern, bool Literal, int Kind, string Label, int Order);
}