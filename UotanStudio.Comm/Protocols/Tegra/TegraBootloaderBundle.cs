using System.Diagnostics.CodeAnalysis;

namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// The miniloader and boot configuration table a Tegra run needs for one chip generation, resolved from a directory
/// that carries upstream- or vendor-named files.
/// <para>一次 Tegra 运行针对某个芯片代际所需的 miniloader 与引导配置表，从存放上游或厂商命名文件的目录中解析。</para>
/// </summary>
/// <remarks>
/// The upstream project names its embedded miniloaders after the generation — <c>tegra20-miniloader.h</c>,
/// <c>tegra30-miniloader.h</c>, <c>tegra114-miniloader.h</c> and <c>tegra124-miniloader.h</c> — and ships the
/// matching BCTs as <c>tegra&lt;generation&gt;-*.bct</c>. This type resolves the same names case-insensitively, accepts
/// the plain binary forms the headers are generated from, and never guesses across generations: a directory that
/// carries only Tegra 4 files does not satisfy a Tegra K1 request.
/// <para>上游项目按代际为其内嵌 miniloader 命名——<c>tegra20-miniloader.h</c>、<c>tegra30-miniloader.h</c>、
/// <c>tegra114-miniloader.h</c> 与 <c>tegra124-miniloader.h</c>——并把对应的 BCT 作为 <c>tegra&lt;代际&gt;-*.bct</c>
/// 发布。本类型以大小写不敏感的方式解析同样的名称，接受由这些头文件生成的原始二进制形式，并且绝不跨代际猜测：
/// 只带 Tegra 4 文件的目录无法满足 Tegra K1 的请求。</para>
/// </remarks>
public sealed class TegraBootloaderBundle
{
    private TegraBootloaderBundle(TegraChip chip, string miniloaderPath, string bctPath)
    {
        Chip = chip;
        MiniloaderPath = miniloaderPath;
        BctPath = bctPath;
    }

    /// <summary>Gets the chip generation this bundle belongs to.
    /// <para>获取本组合所属的芯片代际。</para></summary>
    public TegraChip Chip { get; }

    /// <summary>Gets the absolute path of the miniloader binary.
    /// <para>获取 miniloader 二进制的绝对路径。</para></summary>
    public string MiniloaderPath { get; }

    /// <summary>Gets the absolute path of the boot configuration table.
    /// <para>获取引导配置表的绝对路径。</para></summary>
    public string BctPath { get; }

    /// <summary>
    /// Gets the token the reference names a generation with, which is also the middle segment of every file this type
    /// resolves: <c>tegra20</c>, <c>tegra30</c>, <c>tegra114</c> or <c>tegra124</c>.
    /// <para>获取参考实现用于命名代际的标记，它也是本类型解析的每个文件的中间片段：<c>tegra20</c>、<c>tegra30</c>、
    /// <c>tegra114</c> 或 <c>tegra124</c>。</para>
    /// </summary>
    /// <param name="chip">The chip generation. <para>芯片代际。</para></param>
    /// <returns>The token, or <see langword="null"/> for <see cref="TegraChip.Unknown"/>, which names no generation.
    /// <para>该标记；<see cref="TegraChip.Unknown"/> 不对应任何代际，返回 <see langword="null"/>。</para></returns>
    public static string? GetGenerationToken(TegraChip chip) => chip switch
    {
        TegraChip.Tegra20 => "tegra20",
        TegraChip.Tegra30 => "tegra30",
        TegraChip.Tegra114 => "tegra114",
        TegraChip.Tegra124 => "tegra124",
        _ => null,
    };

    /// <summary>
    /// Resolves the miniloader and BCT of one generation from a directory, reporting rather than throwing when the
    /// directory cannot satisfy the request. <para>从目录解析某个代际的 miniloader 与 BCT；目录无法满足请求时以返回值
    /// 报告而不是抛出。</para>
    /// </summary>
    /// <param name="directory">Directory holding the bundle files. <para>存放组合文件的目录。</para></param>
    /// <param name="chip">The chip generation to resolve; <see cref="TegraChip.Unknown"/> always fails.
    /// <para>要解析的芯片代际；<see cref="TegraChip.Unknown"/> 总是失败。</para></param>
    /// <param name="bundle">Receives the resolved bundle, or <see langword="null"/> when the directory cannot satisfy
    /// the request. <para>接收解析出的组合；目录无法满足请求时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when a miniloader and a BCT of that generation were both found.
    /// <para>该代际的 miniloader 与 BCT 都被找到时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <see langword="null"/> or blank.
    /// <para><paramref name="directory"/> 为 <see langword="null"/> 或空白。</para></exception>
    /// <exception cref="IOException">A candidate file could not be enumerated.
    /// <para>无法枚举某个候选文件。</para></exception>
    /// <exception cref="UnauthorizedAccessException">The directory denies read access.
    /// <para>目录拒绝读取访问。</para></exception>
    /// <example>
    /// <code>
    /// if (TegraBootloaderBundle.TrySelect(directory, TegraChip.Tegra124, out var bundle))
    /// {
    ///     Console.WriteLine(bundle.MiniloaderPath);
    /// }
    /// </code>
    /// </example>
    public static bool TrySelect(string directory, TegraChip chip, [NotNullWhen(true)] out TegraBootloaderBundle? bundle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        bundle = null;
        if (GetGenerationToken(chip) is not { } token || !Directory.Exists(directory))
        {
            return false;
        }

        var files = Directory.GetFiles(directory);
        var miniloader = FindMiniloader(files, token);
        var bct = FindBct(files, token);
        if (miniloader is null || bct is null)
        {
            return false;
        }

        bundle = new TegraBootloaderBundle(chip, Path.GetFullPath(miniloader), Path.GetFullPath(bct));
        return true;
    }

    /// <summary>
    /// Picks the miniloader of one generation out of a file list, accepting the bare binary, the generated C header
    /// and the vendor <c>.bin</c>/<c>.rcm</c> spellings. <para>从文件清单中挑出某个代际的 miniloader，接受原始二进制、
    /// 生成的 C 头文件以及厂商的 <c>.bin</c>/<c>.rcm</c> 写法。</para>
    /// </summary>
    private static string? FindMiniloader(IEnumerable<string> files, string token)
    {
        string[] extensions = [".bin", ".rcm", ".img", ".h", ""];
        return FindByExtensions(files, token + "-miniloader", extensions)
            ?? FindByExtensions(files, token + "_miniloader", extensions)
            ?? FindByExtensions(files, "miniloader-" + token, extensions);
    }

    /// <summary>
    /// Picks the BCT of one generation out of a file list, accepting <c>.bct</c>, the vendor <c>.bin</c> spelling and
    /// the generation-qualified names the reference packages use.
    /// <para>从文件清单中挑出某个代际的 BCT，接受 <c>.bct</c>、厂商的 <c>.bin</c> 写法以及参考包使用的带代际名。</para>
    /// </summary>
    private static string? FindBct(IEnumerable<string> files, string token)
    {
        string[] extensions = [".bct", ".bin", ""];
        return FindByExtensions(files, token + "-bct", extensions)
            ?? FindByExtensions(files, token + "_bct", extensions)
            ?? FindByExtensions(files, token + "-", extensions)
            ?? FindByExtensions(files, "bct-" + token, extensions);
    }

    private static string? FindByExtensions(IEnumerable<string> files, string stem, ReadOnlySpan<string> extensions)
    {
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var extension = Path.GetExtension(file);
            if (!name.Equals(stem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var candidate in extensions)
            {
                if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }

        return null;
    }
}