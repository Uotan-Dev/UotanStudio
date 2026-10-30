using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>
/// Extracts ELF relocatable kernel modules and named payloads embedded in libksud.so.
/// <para>从 libksud.so 中提取内嵌的 ELF 可重定位内核模块与命名负载。</para>
/// </summary>
internal static class KernelSuEmbeddedExtractor
{
    /// <summary>Extracts the verified kernel module matching one KMI from the embedded asset store of a libksud-style library.
    /// <para>从 libksud 类库的内置资源存储中提取与指定 KMI 匹配的已校验内核模块。</para></summary>
    /// <param name="so">Bytes of the shared library to parse.<para>待解析的共享库字节。</para></param>
    /// <param name="kmi">Target KMI compared case-insensitively against asset names.
    /// <para>与资源名称进行不区分大小写比较的目标 KMI。</para></param>
    /// <returns>The extracted ELF module bytes, or <c>null</c> when no verified asset matches or the container fails to parse.
    /// <para>提取出的 ELF 模块字节；无匹配的已校验资源或容器解析失败时为 <c>null</c>。</para></returns>
    public static byte[]? ExtractForKmi(ReadOnlySpan<byte> so, string kmi)
    {
        try
        {
            var elf = new ElfFile(so.ToArray());
            var report = Extractor.ExtractAssets(elf, verbose: false);
            foreach (var asset in report.Assets)
            {
                if (!TryGetKmi(asset.Name, out var assetKmi) || !string.Equals(assetKmi, kmi, StringComparison.OrdinalIgnoreCase)) continue;
                if (!asset.Verified) continue;
                var data = Extractor.GetOutBytes(elf.Data, asset);
                if (data.Length > 0 && data.AsSpan().StartsWith("\u007fELF"u8)) return data;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Extracts one verified named asset from the embedded asset store of a libksud-style library.
    /// <para>从 libksud 类库的内置资源存储中提取一个指定名称的已校验资源。</para>
    /// </summary>
    /// <param name="so">Bytes of the shared library to parse. <para>待解析的共享库字节。</para></param>
    /// <param name="assetName">Asset name compared case-insensitively, for example <c>ksuinit</c>.
    /// <para>不区分大小写比较的资源名称，例如 <c>ksuinit</c>。</para></param>
    /// <returns>The verified ELF payload, or <c>null</c> when the name is absent, the payload cannot be located or
    /// fails its hash check, or the container does not parse.
    /// <para>已校验的 ELF 负载；名称不存在、负载无法定位或未通过哈希校验、容器无法解析时为 <c>null</c>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="assetName"/> is null or whitespace.
    /// <para><paramref name="assetName"/> 为 null 或空白。</para></exception>
    public static byte[]? ExtractNamed(ReadOnlySpan<byte> so, string assetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        try
        {
            var elf = new ElfFile(so.ToArray());
            var report = Extractor.ExtractAssets(elf, verbose: false,
                nameFilter: name => string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase));
            foreach (var asset in report.Assets)
            {
                if (!asset.Verified) continue;
                var data = Extractor.GetOutBytes(elf.Data, asset);
                if (data.Length > 0 && data.AsSpan().StartsWith("\u007fELF"u8)) return data;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Enumerates every verified asset whose name ends in <c>.ko</c> or whose payload starts with an ELF magic.
    /// <para>枚举所有名称以 <c>.ko</c> 结尾、或负载以 ELF 魔数开头的已校验资源。</para>
    /// </summary>
    /// <param name="so">Bytes of the shared library to parse. <para>待解析的共享库字节。</para></param>
    /// <returns>Asset metadata with KMI and architecture where derivable; empty when the container fails to parse.
    /// <para>尽可能附带 KMI 与架构的资源元数据；容器解析失败时为空。</para></returns>
    public static IReadOnlyList<KernelSuKmiAsset> ExtractDetailed(ReadOnlySpan<byte> so)
    {
        var assets = new List<KernelSuKmiAsset>();
        try
        {
            var elf = new ElfFile(so.ToArray());
            var report = Extractor.ExtractAssets(elf, verbose: false);
            foreach (var asset in report.Assets)
            {
                if (!asset.Verified) continue;
                var data = Extractor.GetOutBytes(elf.Data, asset);
                if (data.Length == 0) continue;
                var name = asset.Name.Replace('\\', '/');
                if (!name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase)
                    && !data.AsSpan().StartsWith("\u007fELF"u8)) continue;
                TryGetKmi(name, out var kmi);
                var arch = TryGetArchitecture(name) ?? BootPatchAnalyzer.DetectElfArchitecture(data);
                assets.Add(new KernelSuKmiAsset(name, kmi, arch, asset.Method, asset.Verified, data.Length));
            }
        }
        catch { }
        return assets;
    }

    /// <summary>
    /// Recovers a KMI string from an asset or module file name by trying ordered version patterns.
    /// <para>按顺序尝试版本模式，从资源或模块文件名中还原 KMI 字符串。</para>
    /// </summary>
    /// <param name="name">File name; a trailing <c>_kernelsu.ko</c> suffix is stripped before matching.
    /// <para>文件名；匹配前会剥离结尾的 <c>_kernelsu.ko</c> 后缀。</para></param>
    /// <param name="kmi">The matched KMI with underscores replaced by hyphens, or <c>null</c> when no pattern matches.
    /// <para>匹配到的 KMI（下划线已替换为连字符）；无模式匹配时为 <c>null</c>。</para></param>
    /// <returns>
    /// <see langword="true"/> when a pattern produced a KMI.
    /// <para>某模式成功产出 KMI 时返回 <see langword="true"/>。</para>
    /// </returns>
    public static bool TryGetKmi(string name, out string? kmi)
    {
        kmi = null;
        var normalized = name.Replace('\\', '/');
        normalized = Regex.Replace(normalized, @"(?i)_kernelsu\.ko$", string.Empty);
        string[] patterns =
        [
            @"(?i)(android\d+(?:\.\d+)?[-_]\d+\.\d+(?:[-_][0-9A-Za-z.]+)?)",
            @"(?i)(\d+\.\d+(?:\.\d+)?[-_]android\d+)",
            @"(?i)(\d+\.\d+(?:\.\d+)?)"
        ];
        foreach (var pattern in patterns)
        {
            var m = Regex.Match(normalized, pattern);
            if (m.Success) { kmi = m.Groups[1].Value.Replace('_', '-'); return true; }
        }
        return false;
    }

    private static string? TryGetArchitecture(string name)
    {
        if (name.Contains("aarch64", StringComparison.OrdinalIgnoreCase) || name.Contains("arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
        if (name.Contains("x86_64", StringComparison.OrdinalIgnoreCase) || name.Contains("x64", StringComparison.OrdinalIgnoreCase)) return "x64";
        if (name.Contains("armeabi", StringComparison.OrdinalIgnoreCase) || name.Contains("arm32", StringComparison.OrdinalIgnoreCase)) return "arm32";
        if (name.Contains("/x86/", StringComparison.OrdinalIgnoreCase) || name.EndsWith("x86", StringComparison.OrdinalIgnoreCase)) return "x86";
        return null;
    }

}