using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
using UotanStudio.Firmware.CrossCutting.Compress;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher;

/// <summary>Supported root patch package families.</summary>
public enum BootPatchKind { Unknown, Magisk, KernelSu, APatch, KernelSuLkm }

/// <summary>Optional controls exposed by a Magisk boot patch script.</summary>
public enum BootPatchOption { KeepVerity, KeepForceEncryption, PatchVbmetaFlag, RecoveryMode, LegacySar, PreInitDevice }

/// <summary>Result of inspecting a patch package.</summary>
public sealed record KernelSuKmiAsset(string Name, string? Kmi, string? Architecture, string Method, bool HashVerified, long Size);

public sealed record KernelSuPackageInfo(IReadOnlyList<string> Architectures, IReadOnlyList<string> Kmis,
    IReadOnlyList<KernelSuKmiAsset> Assets);

public sealed record BootPatchDetection(BootPatchKind Kind, IReadOnlyList<string> Architectures, string? Version = null,
    IReadOnlySet<BootPatchOption>? SupportedOptions = null, IReadOnlyList<string>? Kmis = null)
{
    public bool IsSupported => Kind != BootPatchKind.Unknown && Architectures.Count > 0;
}

/// <summary>Options used by the native boot patcher.</summary>
public sealed record BootPatchOptions
{
    public bool KeepVerity { get; init; } = true;
    public bool KeepForceEncryption { get; init; } = true;
    /// <summary>Requests the Magisk PATCHVBMETAFLAG configuration.</summary>
    public bool PatchVbmetaFlag { get; init; }
    public bool RecoveryMode { get; init; }
    /// <summary>Requests Magisk's legacy SAR rootfs kernel patch.</summary>
    /// <remarks>Magisk defaults this option to false; it is only needed for legacy SAR images.</remarks>
    public bool ForceRootfs { get; init; }
    public string? PreInitDevice { get; init; }
    public string? Architecture { get; init; }
    /// <summary>Explicit KernelSU KMI selection; null means use the boot kernel KMI.</summary>
    public string? KernelSuKmi { get; init; }
    public string? SuperPassword { get; init; }
    public IProgress<double>? Progress { get; init; }
}

/// <summary>Validation rules used by APatch for a user supplied SuperKey.</summary>
public static class APatchSuperKey
{
    /// <summary>APatch accepts 8-63 character keys containing at least one letter and digit.</summary>
    public static bool IsValid(string? value)
        => value is { Length: >= 8 and <= 63 }
           && value.Any(char.IsLetter)
           && value.Any(char.IsDigit);
}

/// <summary>Result returned after a patch operation.</summary>
public sealed record BootPatchResult(string OutputPath, BootPatchDetection Detection, IReadOnlyList<string> Changes);

/// <summary>A parsed operation from Magisk/APatch shell patch scripts.</summary>
public sealed record BootPatchScriptOperation(string Command, IReadOnlyList<string> Arguments, int LineNumber);

/// <summary>
/// Small, deliberately conservative parser for the shell DSL shipped inside root packages.
/// It understands the file operations used by Magisk's <c>util_functions.sh</c> and APatch
/// installer scripts without executing shell code.
/// </summary>
public static partial class BootPatchScriptParser
{
    public static IReadOnlyList<BootPatchScriptOperation> Parse(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var result = new List<BootPatchScriptOperation>();
        var lineNo = 0;
        foreach (var raw in script.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            line = ShellCommentRegex().Replace(line, string.Empty).Trim();
            var match = OperationRegex().Match(line);
            if (!match.Success) continue;
            var args = SplitArguments(match.Groups[2].Value);
            result.Add(new BootPatchScriptOperation(match.Groups[1].Value, args, lineNo));
        }
        return result;
    }

    private static List<string> SplitArguments(string value)
    {
        var list = new List<string>();
        foreach (Match m in ArgumentRegex().Matches(value))
            list.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        return list;
    }

    [GeneratedRegex(@"^\s*(?:command\s+)?(cpio|patch|hexpatch|set_perm|set_perm_recursive|install_module|apply_patch)\b(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OperationRegex();
    [GeneratedRegex(@"(?:'([^']*)'|""([^""]*)""|(\S+))")]
    private static partial Regex ArgumentRegex();
    [GeneratedRegex(@"\s+#.*$")]
    private static partial Regex ShellCommentRegex();
}

/// <summary>Detects package family and native architectures from APK/ZIP/KO files.</summary>
public static class BootPatchAnalyzer
{
    /// <summary>Returns all ABI/KMI metadata discovered in a KernelSU APK or ZIP.</summary>
    public static async Task<KernelSuPackageInfo> AnalyzeKernelSuPackageAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var detection = await AnalyzeAsync(path, stream, cancellationToken).ConfigureAwait(false);
        if (detection.Kind != BootPatchKind.KernelSu)
            return new([], [], []);
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var assets = new List<KernelSuKmiAsset>();
        foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith("libksud.so", StringComparison.OrdinalIgnoreCase)
            || e.Name.EndsWith("libkernelsu.so", StringComparison.OrdinalIgnoreCase)
            || e.Name.EndsWith("libsu.so", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { assets.AddRange(KernelSuEmbeddedExtractor.ExtractDetailed(await ReadBoundedAsync(entry.Open(), cancellationToken).ConfigureAwait(false))); }
            catch { }
        }
        return new(detection.Architectures, detection.Kmis ?? [], assets);
    }

    /// <summary>Detects the CPU architecture from an Android boot kernel image.</summary>
    public static async Task<string?> DetectBootArchitectureAsync(string bootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootPath);
        using var parser = new BootImageParser();
        parser.ParseFile(bootPath);
        cancellationToken.ThrowIfCancellationRequested();
        var kernel = parser.Header.KernelCompression == CompressionType.None
            ? parser.Kernel
            : BootCompressor.TryDecompress(parser.Kernel, parser.Header.KernelCompression);
        return kernel is null ? null : DetectKernelArchitecture(kernel);
    }

    public static Task<string?> DetectBootKmiAsync(string bootPath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var parser = new BootImageParser();
            parser.ParseFile(bootPath);
            cancellationToken.ThrowIfCancellationRequested();
            var kernel = parser.Header.KernelCompression == CompressionType.None
                ? parser.Kernel
                : BootCompressor.TryDecompress(parser.Kernel, parser.Header.KernelCompression);
            return kernel is null ? null : KernelVersionExtractor.ExtractKmi(kernel);
        }, cancellationToken);

    internal static string? DetectKernelArchitecture(ReadOnlySpan<byte> kernel)
    {
        var headerLength = Math.Min(kernel.Length, 1024 * 1024);
        var header = kernel[..headerLength].ToArray();
        var match = ManagedMagic.DetectWithRules(header, header.Length);
        if (match.IsMatch && TryMapArchitecture(match.Description) is { } detected) return detected;

        // Boot images may contain an uncompressed executable ELF at a non-zero
        // offset. Use the same Firmware magic engine for each bounded candidate.
        for (var offset = 0; offset <= kernel.Length - 4 && offset < 64 * 1024 * 1024; offset++)
        {
            if (!ManagedMagic.IsElf(kernel[offset..])) continue;
            var length = Math.Min(kernel.Length - offset, 4096);
            var elfHeader = kernel.Slice(offset, length).ToArray();
            var description = ManagedMagic.DetectFirmwareMagic(elfHeader);
            if (description is not null && TryMapArchitecture(description) is { } elfArchitecture) return elfArchitecture;
        }
        return null;
    }

    private static string? TryMapArchitecture(string description)
    {
        if (description.Contains("aarch64", StringComparison.OrdinalIgnoreCase) || description.Contains("arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
        if (description.Contains("risc-v", StringComparison.OrdinalIgnoreCase) || description.Contains("riscv", StringComparison.OrdinalIgnoreCase)) return "riscv64";
        if (description.Contains("x86-64", StringComparison.OrdinalIgnoreCase) || description.Contains("x86_64", StringComparison.OrdinalIgnoreCase)) return "x64";
        if (description.Contains("80386", StringComparison.OrdinalIgnoreCase) || description.Contains("i386", StringComparison.OrdinalIgnoreCase)) return "x86";
        if (description.Contains("ARM", StringComparison.OrdinalIgnoreCase)) return "arm32";
        return null;
    }

    public static async Task<BootPatchDetection> AnalyzeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return await AnalyzeAsync(path, stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<BootPatchDetection> AnalyzeAsync(string path, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var ext = Path.GetExtension(path);
        if (ext.Equals(".ko", StringComparison.OrdinalIgnoreCase))
            return new(BootPatchKind.KernelSuLkm, [DetectElfArchitecture(await ReadBoundedAsync(stream, cancellationToken))]);

        if (!stream.CanSeek) throw new NotSupportedException("Patch package inspection requires a seekable stream.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var names = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.FullName.Replace('\\', '/')).ToArray();
        var abis = names.Select(TryGetAbi).Where(static a => a is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Classification must be driven by family-exclusive components only.
        // util_functions.sh and magiskboot are shared by KernelSU forks and
        // general root-package tooling; libsu.so is a generic root-Activity API
        // library bundled by many ordinary applications, so none of those may
        // mark a package as a patcher by itself.
        var hasMagisk = names.Any(n => n.Contains("libmagiskinit", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.Contains("libmagisk.", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.Contains("libmagisk32", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.Contains("libmagisk64", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.Contains("libmagiskpolicy", StringComparison.OrdinalIgnoreCase));
        var hasKsu = names.Any(n => n.EndsWith("libksud.so", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("libkernelsu.so", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("libksud", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("ksuinit", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("kernelsu.ko", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("ksu.ko", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("susfs.ko", StringComparison.OrdinalIgnoreCase));
        // KernelSU variants such as SukiSU also ship a KPM image named
        // `kpimg`; that marker alone must not classify them as APatch.  APatch
        // packages carry the kpatch native library or an APatch-specific
        // installer script, which are stronger family identifiers.
        var hasApatch = names.Any(n => n.EndsWith("libkptools.so", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("libkpatch.so", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("kpatch", StringComparison.OrdinalIgnoreCase));
        if (!hasApatch && names.Any(n => n.EndsWith("boot_patch.sh", StringComparison.OrdinalIgnoreCase)))
        {
            // Both Magisk and APatch ship a script with this name.  Inspect the
            // script body before classifying it; APatch scripts mention kpimg or
            // the SuperKey while Magisk scripts mention magiskinit/magiskboot.
            var script = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("boot_patch.sh", StringComparison.OrdinalIgnoreCase));
            if (script is not null)
            {
                using var reader = new StreamReader(script.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                hasApatch = body.Contains("kpimg", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("superkey", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("kpatch", StringComparison.OrdinalIgnoreCase);
            }
        }
        hasApatch = hasApatch || names.Any(n => n.EndsWith("kpatch", StringComparison.OrdinalIgnoreCase));
        // KernelSU variants often carry Magisk compatibility scripts and shared
        // util_functions.sh files. Native KSU markers are stronger evidence than
        // those compatibility names, so classify KSU before falling back to Magisk.
        var kind = hasApatch ? BootPatchKind.APatch : hasKsu ? BootPatchKind.KernelSu : hasMagisk ? BootPatchKind.Magisk : BootPatchKind.Unknown;
        var kmiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (kind == BootPatchKind.KernelSu)
        {
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith("libksud.so", StringComparison.OrdinalIgnoreCase)
                || e.Name.EndsWith("libkernelsu.so", StringComparison.OrdinalIgnoreCase)
                || e.Name.EndsWith("libsu.so", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var bytes = await ReadBoundedAsync(entry.Open(), cancellationToken);
                    foreach (var asset in KernelSuEmbeddedExtractor.ExtractDetailed(bytes))
                        if (!string.IsNullOrWhiteSpace(asset.Kmi)) kmiNames.Add(asset.Kmi!);
                    foreach (var kmi in KernelSuEmbeddedExtractor.FindKmisInBinary(bytes))
                        kmiNames.Add(kmi);
                }
                catch { /* malformed ABI member is reported during patching */ }
            }
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase)))
                if (KernelSuEmbeddedExtractor.TryGetKmi(entry.FullName, out var kmi)) kmiNames.Add(kmi!);
        }
        string? version = null;
        var supportedOptions = new HashSet<BootPatchOption>();
        if (kind == BootPatchKind.Magisk)
        {
            var util = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("util_functions.sh", StringComparison.OrdinalIgnoreCase));
            if (util is not null)
            {
                using var reader = new StreamReader(util.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var match = Regex.Match(body, "(?m)^\\s*MAGISK_VER\\s*=\\s*['\\\"]([^'\\\"]+)", RegexOptions.CultureInvariant);
                version = match.Success ? match.Groups[1].Value : null;
            }
            var patchScript = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("boot_patch.sh", StringComparison.OrdinalIgnoreCase));
            if (patchScript is not null)
            {
                using var scriptReader = new StreamReader(patchScript.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var script = await scriptReader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var executableScript = Regex.Replace(script, @"(?m)^\s*#.*$", string.Empty);
                AddSupportedOption(executableScript, supportedOptions, BootPatchOption.KeepVerity, "KEEPVERITY");
                AddSupportedOption(executableScript, supportedOptions, BootPatchOption.KeepForceEncryption, "KEEPFORCEENCRYPT");
                AddSupportedOption(executableScript, supportedOptions, BootPatchOption.PatchVbmetaFlag, "PATCHVBMETAFLAG");
                AddSupportedOption(executableScript, supportedOptions, BootPatchOption.RecoveryMode, "RECOVERYMODE");
                AddSupportedOption(executableScript, supportedOptions, BootPatchOption.LegacySar, "LEGACYSAR");
                AddSupportedOption(executableScript, supportedOptions, BootPatchOption.PreInitDevice, "PREINITDEVICE");
            }
            // Very old scripts did not declare every environment variable but
            // still implement the two mandatory preservation flags.
            supportedOptions.Add(BootPatchOption.KeepVerity);
            supportedOptions.Add(BootPatchOption.KeepForceEncryption);
        }
        if (abis.Count == 0)
        {
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase)))
            {
                await using var es = entry.Open();
                var header = await ReadBoundedAsync(es, cancellationToken, 128);
                if (header.Length >= 20 && header[0] == 0x7f && header[1] == (byte)'E')
                {
                    var abi = DetectElfArchitecture(header);
                    if (!string.Equals(abi, "unknown", StringComparison.OrdinalIgnoreCase) && !abi.StartsWith("elf-", StringComparison.OrdinalIgnoreCase))
                        abis.Add(abi);
                }
            }
        }
        return new(kind, abis, version, kind == BootPatchKind.Magisk ? supportedOptions : null,
            kmiNames.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void AddSupportedOption(string script, HashSet<BootPatchOption> options, BootPatchOption option, string token)
    {
        if (script.Contains(token, StringComparison.OrdinalIgnoreCase)) options.Add(option);
    }

    internal static string? TryGetAbi(string name)
    {
        var parts = name.Split('/');
        var lib = Array.FindIndex(parts, p => p.Equals("lib", StringComparison.OrdinalIgnoreCase));
        if (lib < 0 || lib + 1 >= parts.Length) return null;
        return parts[lib + 1] switch
        {
            "arm64-v8a" => "arm64",
            "armeabi-v7a" => "arm32",
            "x86_64" => "x64",
            "x86" => "x86",
            "mips" => "mips",
            "mips64" => "mips64",
            var value => value,
        };
    }

    internal static string DetectElfArchitecture(byte[] data)
    {
        if (data.Length < 20 || data[0] != 0x7f || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F') return "unknown";
        var machine = BitConverter.ToUInt16(data, 18);
        return machine switch { 183 => "arm64", 40 => "arm32", 62 => "x64", 3 => "x86", 8 => "mips", 10 => "mips64", _ => $"elf-{machine}" };
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct, int max = 256 * 1024 * 1024)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        while (output.Length < max)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, max - output.Length)), ct).ConfigureAwait(false);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        if (output.Length >= max && stream.ReadByte() >= 0) throw new InvalidDataException("Patch payload exceeds the bounded limit.");
        return output.ToArray();
    }
}

/// <summary>Extracts ELF relocatable kernel modules embedded in libksud.so.</summary>
public static class KernelSuEmbeddedExtractor
{
    public static IReadOnlyList<string> FindKmisInBinary(ReadOnlySpan<byte> data)
    {
        var text = Encoding.Latin1.GetString(data);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A KMI is only considered advertised when the complete resource name
        // is present. Do not consume adjacent strings from a merged .rodata
        // section and do not infer a module from a bare KMI token.
        foreach (Match match in Regex.Matches(text, @"(?i)(android\d+(?:\.\d+)?[-_]\d+\.\d+)_kernelsu\.ko"))
            found.Add(match.Groups[1].Value.Replace('_', '-'));
        return found.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static byte[]? ExtractForKmi(ReadOnlySpan<byte> so, string kmi)
    {
        try
        {
            var elf = new Ksud.ElfFile(so.ToArray());
            var report = Ksud.Extractor.ExtractAssets(elf, verbose: false);
            foreach (var asset in report.Assets)
            {
                if (!TryGetKmi(asset.Name, out var assetKmi) || !string.Equals(assetKmi, kmi, StringComparison.OrdinalIgnoreCase)) continue;
                if (!asset.Verified) continue;
                var data = Ksud.Extractor.GetOutBytes(elf.Data, asset);
                if (data.Length > 0 && data.AsSpan().StartsWith("\x7fELF"u8)) return data;
            }
        }
        catch { }
        return null;
    }

    public static IReadOnlyList<KernelSuKmiAsset> ExtractDetailed(ReadOnlySpan<byte> so)
    {
        var assets = new List<KernelSuKmiAsset>();
        try
        {
            var elf = new Ksud.ElfFile(so.ToArray());
            var report = Ksud.Extractor.ExtractAssets(elf, verbose: false);
            foreach (var asset in report.Assets)
            {
                if (!asset.Verified) continue;
                var data = Ksud.Extractor.GetOutBytes(elf.Data, asset);
                if (data.Length == 0) continue;
                var name = asset.Name.Replace('\\', '/');
                if (!name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase)
                    && !data.AsSpan().StartsWith("\x7fELF"u8)) continue;
                TryGetKmi(name, out var kmi);
                var arch = TryGetArchitecture(name) ?? BootPatchAnalyzer.DetectElfArchitecture(data);
                assets.Add(new KernelSuKmiAsset(name, kmi, arch, asset.Method, asset.Verified, data.Length));
            }
        }
        catch { }
        return assets;
    }

    public static bool TryGetKmi(string name, out string? kmi)
    {
        kmi = null;
        var normalized = name.Replace('\\', '/');
        // The module suffix is a resource delimiter, not part of the KMI.
        // Strip it before applying the permissive version pattern so a name
        // such as android13_5.15_kernelsu.ko yields android13-5.15.
        normalized = Regex.Replace(normalized, @"(?i)_kernelsu\.ko$", string.Empty);
        var patterns = new[] {
            @"(?i)(android\d+(?:\.\d+)?[-_]\d+\.\d+(?:[-_][0-9A-Za-z.]+)?)",
            @"(?i)(\d+\.\d+(?:\.\d+)?[-_]android\d+)",
            @"(?i)(\d+\.\d+(?:\.\d+)?)"
        };
        foreach (var pattern in patterns)
        {
            var m = Regex.Match(normalized, pattern);
            if (m.Success) { kmi = m.Groups[1].Value.Replace('_', '-'); return true; }
        }
        return false;
    }

    private static string? TryGetArchitecture(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("aarch64") || n.Contains("arm64")) return "arm64";
        if (n.Contains("x86_64") || n.Contains("x64")) return "x64";
        if (n.Contains("armeabi") || n.Contains("arm32")) return "arm32";
        if (n.Contains("/x86/") || n.EndsWith("x86")) return "x86";
        return null;
    }

}