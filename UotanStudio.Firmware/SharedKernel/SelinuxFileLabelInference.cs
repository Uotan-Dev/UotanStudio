using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>Infers file object contexts without inventing process domains or executable entrypoint types.</summary>
internal sealed class SelinuxFileLabelInference
{
    private readonly Dictionary<string, PreservedInodeEntry> originals;
    private readonly Dictionary<(string Parent, int Kind), PreservedInodeEntry[]> siblings;
    private readonly List<Rule[]> policies = [];
    private readonly string? mount;
    private readonly bool systemAsRoot;
    private readonly bool labeled;
    private sealed record Rule(Regex Pattern, bool Literal, int Kind, string Label);

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
        if (mount is null) return; // An unknown mount is not guessed from the host directory name.
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
            if (budget < 0) throw new InvalidDataException("SELinux text policies exceed the 8 MiB limit.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var rules = new List<Rule>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 16384) throw new InvalidDataException("SELinux rule exceeds the line limit.");
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (++count > 32768) throw new InvalidDataException("Too many SELinux path rules.");
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length is not (2 or 3) || !parts[0].StartsWith('/'))
                    throw new InvalidDataException($"Invalid SELinux rule in {path}.");
                var kind = parts.Length == 2 ? 0 : parts[1] switch
                {
                    "--" => 0x8000,
                    "-d" => 0x4000,
                    "-l" => 0xA000,
                    "-b" => 0x6000,
                    "-c" => 0x2000,
                    "-p" => 0x1000,
                    "-s" => 0xC000,
                    _ => throw new InvalidDataException($"Invalid SELinux file kind in {path}.")
                };
                var label = parts[^1];
                if (label != "<<none>>" && !IsObjectContext(label))
                    throw new InvalidDataException($"Invalid SELinux file object context in {path}: {label}");
                var pattern = parts[0];
                // Keep a portable PCRE subset: no lookarounds, backreferences, Unicode classes,
                // POSIX classes or engine-specific escapes. Never silently ignore an unsupported rule.
                if (pattern.Contains("(?", StringComparison.Ordinal) || pattern.Contains("[[:", StringComparison.Ordinal)
                    || pattern.Contains("&&", StringComparison.Ordinal) || pattern.Contains("-[", StringComparison.Ordinal)
                    || Regex.IsMatch(pattern, @"\\[a-zA-Z0-9]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    throw new InvalidDataException($"Unsupported SELinux regex in {path}: {pattern}");
                var literal = !Regex.IsMatch(pattern, @"(?<!\\)[.\^$?*+|\[({]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                try
                {
                    // libselinux matches UTF-8 bytes with PCRE_DOTALL, not UTF-16 characters.
                    var bytes = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(pattern));
                    rules.Add(new(new Regex("\\A(?:" + bytes + ")\\z", RegexOptions.NonBacktracking | RegexOptions.Singleline
                        | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), literal, kind, label));
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException)
                {
                    throw new InvalidDataException($"Unsupported SELinux regex in {path}: {pattern}", error);
                }
            }
            policies.Add(rules.ToArray());
        }
    }

    internal byte[]? Infer(string path, bool directory, bool executable = false)
    {
        var kind = directory ? 0x4000 : 0x8000;
        var runtimePath = (systemAsRoot ? "" : mount) + "/" + path;
        var key = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(runtimePath));
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in policies)
        {
            // libselinux gives literals priority, then evaluates regex rules in reverse source order.
            var match = policy.LastOrDefault(r => r.Literal && (r.Kind == 0 || r.Kind == kind) && r.Pattern.IsMatch(key))
                ?? policy.LastOrDefault(r => !r.Literal && (r.Kind == 0 || r.Kind == kind) && r.Pattern.IsMatch(key));
            if (match is not null) matches.Add(match.Label);
        }
        if (matches.Count > 1) throw new InvalidDataException($"Conflicting SELinux policy sources for {runtimePath}.");
        if (matches.Count == 1)
        {
            var label = matches.Single();
            if (label == "<<none>>") throw new InvalidDataException($"SELinux policy explicitly excludes labeling {runtimePath}.");
            return Encode(label);
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
        var label = Encoding.UTF8.GetString(raw).TrimEnd('\0');
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
}