using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.BootSecurity.Patcher.Kernel;
using UotanStudio.Firmware.Core.Magic;

namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>Supported root patch package families. <para>支持的根补丁包类型族。</para></summary>
public enum BootPatchKind
{
    /// <summary>
    /// Family could not be identified from the inspected entry names.
    /// <para>无法从被检查的条目名称识别包类型族。</para>
    /// </summary>
    Unknown,
    /// <summary>
    /// Magisk package identified by its libmagisk assets and boot_patch.sh.
    /// <para>通过 libmagisk 资源与 boot_patch.sh 识别的 Magisk 包。</para>
    /// </summary>
    Magisk,
    /// <summary>
    /// KernelSU package carrying libksud or embedded .ko modules.
    /// <para>携带 libksud 或内置 .ko 模块的 KernelSU 包。</para>
    /// </summary>
    KernelSu,
    /// <summary>
    /// APatch package identified by kpatch/kpimg assets or script markers.
    /// <para>通过 kpatch/kpimg 资源或脚本标记识别的 APatch 包。</para>
    /// </summary>
    APatch,
    /// <summary>A standalone KernelSU loadable kernel module file. <para>独立的 KernelSU 可加载内核模块文件。</para></summary>
    KernelSuLkm
}

/// <summary>Optional controls exposed by a Magisk boot patch script. <para>Magisk 引导补丁脚本暴露的可选控制项。</para></summary>
public enum BootPatchOption
{
    /// <summary>
    /// KEEPVERITY: keep dm-verity enabled in the patched image.
    /// <para>KEEPVERITY：在修补后的镜像中保留 dm-verity。</para>
    /// </summary>
    KeepVerity,
    /// <summary>KEEPFORCEENCRYPT: keep forced encryption enabled. <para>KEEPFORCEENCRYPT：保留强制加密。</para></summary>
    KeepForceEncryption,
    /// <summary>
    /// PATCHVBMETAFLAG: request the vbmeta flag patch path.
    /// <para>PATCHVBMETAFLAG：请求 vbmeta 标志修补路径。</para>
    /// </summary>
    PatchVbmetaFlag,
    /// <summary>
    /// RECOVERYMODE: patch for a recovery-image installation.
    /// <para>RECOVERYMODE：面向 recovery 镜像安装进行修补。</para>
    /// </summary>
    RecoveryMode,
    /// <summary>
    /// LEGACYSAR: request the legacy system-as-root layout.
    /// <para>LEGACYSAR：请求传统 system-as-root 布局。</para>
    /// </summary>
    LegacySar,
    /// <summary>PREINITDEVICE: record an early-init device node. <para>PREINITDEVICE：记录早期 init 设备节点。</para></summary>
    PreInitDevice
}

/// <summary>
/// Result of inspecting a patch package: one asset found inside a KernelSU shared library.
/// <para>检查补丁包的结果：在 KernelSU 共享库内发现的一个资源。</para>
/// </summary>
/// <param name="Name">
/// Asset path name with backslashes normalized to forward slashes.
/// <para>反斜杠已规范为正斜杠的资源路径名。</para>
/// </param>
/// <param name="Kmi">
/// KMI string recovered from the asset name, or <c>null</c> when the name carries none.
/// <para>从资源名称还原的 KMI 字符串；名称不含 KMI 时为 <c>null</c>。</para>
/// </param>
/// <param name="Architecture">
/// Architecture inferred from the name or the ELF header, or <c>null</c> when unknown.
/// <para>从名称或 ELF 头推断的架构；无法判定时为 <c>null</c>。</para>
/// </param>
/// <param name="Method">
/// Extraction method reported by the embedded asset scanner.
/// <para>由内置资源扫描器报告的提取方法。</para>
/// </param>
/// <param name="HashVerified">Whether the asset passed its hash verification. <para>该资源是否通过哈希校验。</para></param>
/// <param name="Size">Length in bytes of the extracted payload. <para>提取后负载的字节长度。</para></param>
public sealed record KernelSuKmiAsset(string Name, string? Kmi, string? Architecture, string Method, bool HashVerified, long Size);

/// <summary>
/// Architectures, KMI strings and embedded assets discovered in a KernelSU APK or ZIP.
/// <para>在 KernelSU 的 APK 或 ZIP 中发现的架构、KMI 字符串与内置资源。</para>
/// </summary>
/// <param name="Architectures">Distinct ABI names detected in the package. <para>在包中检测到的去重 ABI 名称。</para></param>
/// <param name="Kmis">KMI strings recovered from embedded modules. <para>从内置模块还原的 KMI 字符串。</para></param>
/// <param name="Assets">
/// Verified assets extracted from libksud-style libraries.
/// <para>从 libksud 类库中提取的已校验资源。</para>
/// </param>
public sealed record KernelSuPackageInfo(IReadOnlyList<string> Architectures, IReadOnlyList<string> Kmis,
    IReadOnlyList<KernelSuKmiAsset> Assets);

/// <summary>
/// Detection outcome describing a package's family, ABIs, options and KMIs.
/// <para>描述包类型族、ABI、可选项与 KMI 的检测结果。</para>
/// </summary>
/// <param name="Kind">Identified package family. <para>识别出的包类型族。</para></param>
/// <param name="Architectures">ABI names the package supports. <para>包支持的 ABI 名称。</para></param>
/// <param name="Version">
/// MAGISK_VER string for Magisk packages; <c>null</c> for other families or when the marker is absent.
/// <para>Magisk 包的 MAGISK_VER 字符串；其他类型族或标记缺失时为 <c>null</c>。</para>
/// </param>
/// <param name="SupportedOptions">
/// Option tokens referenced by the package's boot_patch.sh; <c>null</c> for non-Magisk families.
/// <para>包内 boot_patch.sh 引用的选项标记；非 Magisk 类型族为 <c>null</c>。</para>
/// </param>
/// <param name="Kmis">
/// KMI strings detected for KernelSU packages; empty for other families.
/// <para>KernelSU 包检测到的 KMI 字符串；其他类型族为空。</para>
/// </param>
public sealed record BootPatchDetection(BootPatchKind Kind, IReadOnlyList<string> Architectures, string? Version = null,
    IReadOnlySet<BootPatchOption>? SupportedOptions = null, IReadOnlyList<string>? Kmis = null)
{
    /// <summary>Whether a known family was detected together with at least one architecture.
    /// <para>是否同时检测到已知类型族与至少一个架构。</para></summary>
    /// <value>
    /// <see langword="true"/> when <c>Kind</c> is not <see cref="BootPatchKind.Unknown"/>
    /// and the ABI list is non-empty.
    /// <para>当 <c>Kind</c> 不是 <see cref="BootPatchKind.Unknown"/> 且 ABI 列表非空时为 <see langword="true"/>。</para>
    /// </value>
    public bool IsSupported => Kind != BootPatchKind.Unknown && Architectures.Count > 0;
}

/// <summary>Options used by the native boot patcher. <para>原生引导修补器使用的选项。</para></summary>
public sealed record BootPatchOptions
{
    /// <summary>Keeps dm-verity active in the patched image. <para>在修补后的镜像中保留 dm-verity。</para></summary>
    /// <value>Defaults to <see langword="true"/>. <para>默认为 <see langword="true"/>。</para></value>
    public bool KeepVerity { get; init; } = true;
    /// <summary>Keeps forced encryption active in the patched image. <para>在修补后的镜像中保留强制加密。</para></summary>
    /// <value>Defaults to <see langword="true"/>. <para>默认为 <see langword="true"/>。</para></value>
    public bool KeepForceEncryption { get; init; } = true;
    /// <summary>Requests the Magisk PATCHVBMETAFLAG configuration.
    /// <para>请求 Magisk 的 PATCHVBMETAFLAG 配置。</para></summary>
    /// <value>Defaults to <see langword="false"/>. <para>默认为 <see langword="false"/>。</para></value>
    public bool PatchVbmetaFlag { get; init; }
    /// <summary>Patches for a recovery-image installation instead of a boot image.
    /// <para>面向 recovery 镜像安装而非 boot 镜像进行修补。</para></summary>
    /// <value>Defaults to <see langword="false"/>. <para>默认为 <see langword="false"/>。</para></value>
    public bool RecoveryMode { get; init; }
    /// <summary>Requests Magisk's legacy SAR rootfs kernel patch.
    /// <para>请求 Magisk 的传统 SAR rootfs 内核补丁。</para></summary>
    /// <remarks>Magisk defaults this option to false; it is only needed for legacy SAR images.</remarks>
    /// <value>Defaults to <see langword="false"/>. <para>默认为 <see langword="false"/>。</para></value>
    public bool ForceRootfs { get; init; }
    /// <summary>
    /// Device node passed as Magisk's PREINITDEVICE value.
    /// <para>作为 Magisk PREINITDEVICE 值传入的设备节点。</para>
    /// </summary>
    /// <value><c>null</c> when no pre-init device is requested. <para>未请求预初始化设备时为 <c>null</c>。</para></value>
    public string? PreInitDevice { get; init; }
    /// <summary>Target CPU architecture for the patch run. <para>本次修补运行的目标 CPU 架构。</para></summary>
    /// <value>An ABI name as produced by <see cref="BootPatchAnalyzer"/>, or <c>null</c> to let detection choose.
    /// <para>由 <see cref="BootPatchAnalyzer"/> 产生的 ABI 名称；为 <c>null</c> 时由检测流程决定。</para></value>
    public string? Architecture { get; init; }
    /// <summary>Explicit KernelSU KMI selection; null means use the boot kernel KMI.
    /// <para>显式指定的 KernelSU KMI；为 null 时使用 boot 内核的 KMI。</para></summary>
    /// <value>
    /// A KMI string, or <c>null</c> to derive it from the boot image.
    /// <para>KMI 字符串；为 <c>null</c> 时从 boot 镜像推导。</para>
    /// </value>
    public string? KernelSuKmi { get; init; }
    /// <summary>
    /// Password forwarded to the patcher for super partition access.
    /// <para>转发给修补器用于 super 分区访问的密码。</para>
    /// </summary>
    /// <value><c>null</c> when no super password is supplied. <para>未提供 super 密码时为 <c>null</c>。</para></value>
    public string? SuperPassword { get; init; }
    /// <summary>
    /// Optional sink that receives progress values from the patch pipeline.
    /// <para>接收修补流水线进度值的可选接收器。</para>
    /// </summary>
    /// <value><c>null</c> disables progress reporting; otherwise the pipeline reports <see cref="double"/> progress.
    /// <para>为 <c>null</c> 时不报告进度；否则流水线报告 <see cref="double"/> 进度值。</para></value>
    public IProgress<double>? Progress { get; init; }
}

/// <summary>
/// Validation rules used by APatch for a user supplied SuperKey.
/// <para>APatch 对用户提供的 SuperKey 所使用的校验规则。</para>
/// </summary>
internal static class APatchSuperKey
{
    /// <summary>
    /// APatch accepts 8-63 character keys containing at least one letter and digit.
    /// <para>APatch 接受长度为 8-63 个字符、且至少包含一个字母和一个数字的密钥。</para>
    /// </summary>
    /// <param name="value">Candidate key; <c>null</c> is always rejected. <para>候选密钥；<c>null</c> 一律不通过。</para></param>
    /// <returns><see langword="true"/> when the key satisfies the length and character-class rules.
    /// <para>当密钥满足长度与字符类别规则时返回 <see langword="true"/>。</para></returns>
    public static bool IsValid(string? value)
        => value is { Length: >= 8 and <= 63 }
           && value.Any(char.IsLetter)
           && value.Any(char.IsDigit);
}

/// <summary>
/// Result returned after a patch operation.
/// <para>修补操作完成后返回的结果。</para>
/// </summary>
/// <param name="OutputPath">Path of the patched image produced by the run. <para>本次运行生成的修补后镜像路径。</para></param>
/// <param name="Detection">Package detection metadata the patch was based on. <para>修补所依据的包检测元数据。</para></param>
/// <param name="Changes">Descriptions of the changes applied during the run. <para>本次运行中已应用变更的描述。</para></param>
public sealed record BootPatchResult(string OutputPath, BootPatchDetection Detection, IReadOnlyList<string> Changes);

/// <summary>
/// A parsed operation from Magisk/APatch shell patch scripts.
/// <para>从 Magisk/APatch shell 补丁脚本中解析出的一条操作。</para>
/// </summary>
/// <param name="Command">Recognized operation keyword as written in the script. <para>脚本中书写的可识别操作关键字。</para></param>
/// <param name="Arguments">
/// Quoting-stripped argument tokens following the command.
/// <para>已去除引号的命令后随参数标记。</para>
/// </param>
/// <param name="LineNumber">1-based source line the operation was found on. <para>该操作所在源码行的 1 基编号。</para></param>
public sealed record BootPatchScriptOperation(string Command, IReadOnlyList<string> Arguments, int LineNumber);

/// <summary>
/// Small, deliberately conservative parser for the shell DSL shipped inside root packages.
/// It understands the file operations used by Magisk's <c>util_functions.sh</c> and APatch
/// installer scripts without executing shell code.
/// <para>一个刻意保守的小型解析器，用于根包内置的 shell DSL。它理解 Magisk 的 <c>util_functions.sh</c>
/// 与 APatch 安装脚本使用的文件操作，且不执行任何 shell 代码。</para>
/// </summary>
internal static partial class BootPatchScriptParser
{
    /// <summary>
    /// Extracts recognized file operations from patch script text line by line, skipping comments and blanks.
    /// <para>逐行从补丁脚本文本中提取可识别的文件操作，跳过注释与空行。</para>
    /// </summary>
    /// <param name="script">Full script text; trailing shell comments are stripped before matching.
    /// <para>完整脚本文本；匹配前会剥离行尾的 shell 注释。</para></param>
    /// <returns>Operations in source order with their 1-based line numbers; empty when nothing matches.
    /// <para>按源码顺序返回操作及其 1 基行号；无匹配时为空。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="script"/> is <see langword="null"/>.
    /// <para><paramref name="script"/> 为 <see langword="null"/>。</para></exception>
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

/// <summary>
/// Detects package family and native architectures from APK/ZIP/KO files.
/// <para>从 APK/ZIP/KO 文件中检测包类型族与原生架构。</para>
/// </summary>
public static class BootPatchAnalyzer
{
    /// <summary>
    /// Returns all ABI/KMI metadata discovered in a KernelSU APK or ZIP.
    /// <para>返回在 KernelSU 的 APK 或 ZIP 中发现的全部 ABI/KMI 元数据。</para>
    /// </summary>
    /// <param name="path">
    /// Package file path, opened read-only with FileShare.Read.
    /// <para>包文件路径，以只读且 FileShare.Read 方式打开。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before opening and between entry scans.
    /// <para>在打开前与条目扫描之间检查。</para>
    /// </param>
    /// <returns>
    /// Detection metadata plus verified embedded assets;
    /// every list is empty when the package is not detected as KernelSU.
    /// <para>检测元数据与已校验的内置资源；包未被识别为 KernelSU 时各列表均为空。</para>
    /// </returns>
    /// <exception cref="IOException">The package file cannot be opened. <para>无法打开包文件。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
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

    /// <summary>
    /// Detects the CPU architecture from an Android boot kernel image.
    /// <para>从 Android boot 镜像的内核中检测 CPU 架构。</para>
    /// </summary>
    /// <param name="bootPath">Boot image file path parsed from disk. <para>从磁盘解析的 boot 镜像文件路径。</para></param>
    /// <param name="cancellationToken">
    /// Checked after parsing, before kernel inspection.
    /// <para>在解析完成后、检查内核前检查。</para>
    /// </param>
    /// <returns>
    /// An ABI name such as <c>arm64</c> or <c>x64</c>, or <c>null</c> when the kernel
    /// cannot be decompressed or matches no architecture.
    /// <para>形如 <c>arm64</c>、<c>x64</c> 的 ABI 名称；内核无法解压或不匹配任何架构时为 <c>null</c>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="bootPath"/> is null or whitespace.
    /// <para><paramref name="bootPath"/> 为 null 或空白。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
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

    /// <summary>
    /// Extracts the KernelSU KMI string from the kernel embedded in an Android boot image,
    /// running the parse on the thread pool.
    /// <para>从 Android boot 镜像内置内核中提取 KernelSU KMI 字符串，解析在线程池上运行。</para>
    /// </summary>
    /// <param name="bootPath">Boot image file path parsed from disk. <para>从磁盘解析的 boot 镜像文件路径。</para></param>
    /// <param name="cancellationToken">
    /// Honored by the thread-pool scheduling and checked after parsing.
    /// <para>由线程池调度遵守，并在解析完成后检查。</para>
    /// </param>
    /// <returns>
    /// The KMI string, or <c>null</c> when the kernel cannot be decompressed or carries no extractable version.
    /// <para>KMI 字符串；内核无法解压或不含可提取版本时为 <c>null</c>。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">The token was canceled before or during the scheduled work.
    /// <para>令牌在调度任务期间或之前被取消。</para></exception>
    /// <remarks>
    /// Boot image parse failures surface through the returned task from <c>BootImageParser.ParseFile</c>.
    /// </remarks>
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
        if (FileMagic.Identify(header)?.Description is { } rootDescription && TryMapArchitecture(rootDescription) is { } detected) return detected;

        for (var offset = 0; offset <= kernel.Length - 4 && offset < 64 * 1024 * 1024; offset++)
        {
            if (!FileMagic.IsElf(kernel[offset..])) continue;
            var length = Math.Min(kernel.Length - offset, 4096);
            var elfHeader = kernel.Slice(offset, length).ToArray();
            var description = FileMagic.Describe(elfHeader);
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

    /// <summary>
    /// Detects the package family, ABIs, Magisk options and KernelSU KMIs from a package file on disk.
    /// <para>从磁盘上的包文件检测类型族、ABI、Magisk 选项与 KernelSU KMI。</para>
    /// </summary>
    /// <param name="path">
    /// Package file path, opened read-only with FileShare.Read.
    /// <para>包文件路径，以只读且 FileShare.Read 方式打开。</para>
    /// </param>
    /// <param name="cancellationToken">Propagated into the stream-based analysis. <para>传递到基于流的分析流程。</para></param>
    /// <returns>A detection record; unknown family and empty lists when nothing matches.
    /// <para>检测结果记录；无匹配时为未知类型族与空列表。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is null or whitespace.
    /// <para><paramref name="path"/> 为 null 或空白。</para>
    /// </exception>
    /// <exception cref="IOException">The file cannot be opened. <para>无法打开该文件。</para></exception>
    public static async Task<BootPatchDetection> AnalyzeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return await AnalyzeAsync(path, stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detects package metadata from an open stream; the path only contributes its file extension.
    /// <para>从打开的流中检测包元数据；路径仅提供其文件扩展名。</para>
    /// </summary>
    /// <param name="path">
    /// Original file name; a <c>.ko</c> extension selects the kernel-module fast path.
    /// <para>原始文件名；<c>.ko</c> 扩展名会选择内核模块快速路径。</para>
    /// </param>
    /// <param name="stream">
    /// Package stream read from its current position and left open.
    /// <para>从当前位置读取的包流，读取后保持打开。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before reading and during every bounded read.
    /// <para>在读取前及每次有界读取期间检查。</para>
    /// </param>
    /// <returns>A detection record; unknown family with empty lists when no package marker matches.
    /// <para>检测结果记录；无包标记匹配时为未知类型族与空列表。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">A non-<c>.ko</c> package was handed a stream that cannot seek.
    /// <para>非 <c>.ko</c> 包传入了不可寻址的流。</para></exception>
    /// <exception cref="InvalidDataException">
    /// A payload read exceeds the bounded size limit.
    /// <para>某次负载读取超出有界大小上限。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
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
        var hasApatch = names.Any(n => n.EndsWith("libkptools.so", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("libkpatch.so", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("kpatch", StringComparison.OrdinalIgnoreCase));
        if (!hasApatch && names.Any(n => n.EndsWith("boot_patch.sh", StringComparison.OrdinalIgnoreCase)))
        {
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
                catch { }
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

/// <summary>
/// Extracts ELF relocatable kernel modules embedded in libksud.so.
/// <para>从 libksud.so 中提取内嵌的 ELF 可重定位内核模块。</para>
/// </summary>
internal static class KernelSuEmbeddedExtractor
{
    /// <summary>
    /// Scans raw bytes for <c>*_kernelsu.ko</c> name patterns and collects the distinct KMI strings.
    /// <para>扫描原始字节中的 <c>*_kernelsu.ko</c> 名称模式，收集去重后的 KMI 字符串。</para>
    /// </summary>
    /// <param name="data">
    /// Binary content, typically a whole shared-library image.
    /// <para>二进制内容，通常为完整的共享库映像。</para>
    /// </param>
    /// <returns>
    /// KMI strings ordered case-insensitively with underscores normalized to hyphens; empty when nothing matches.
    /// <para>按不区分大小写排序、下划线已规范为连字符的 KMI 字符串；无匹配时为空。</para>
    /// </returns>
    public static IReadOnlyList<string> FindKmisInBinary(ReadOnlySpan<byte> data)
    {
        var text = Encoding.Latin1.GetString(data);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, @"(?i)(android\d+(?:\.\d+)?[-_]\d+\.\d+)_kernelsu\.ko"))
            found.Add(match.Groups[1].Value.Replace('_', '-'));
        return found.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Extracts the verified kernel module matching one KMI from the embedded asset store of a libksud-style library.
    /// <para>从 libksud 类库的内置资源存储中提取与指定 KMI 匹配的已校验内核模块。</para>
    /// </summary>
    /// <param name="so">Bytes of the shared library to parse. <para>待解析的共享库字节。</para></param>
    /// <param name="kmi">
    /// Target KMI compared case-insensitively against asset names.
    /// <para>与资源名称进行不区分大小写比较的目标 KMI。</para>
    /// </param>
    /// <returns>
    /// The extracted ELF module bytes, or <c>null</c> when no verified asset matches or the container fails to parse.
    /// <para>提取出的 ELF 模块字节；无匹配的已校验资源或容器解析失败时为 <c>null</c>。</para>
    /// </returns>
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
                if (data.Length > 0 && data.AsSpan().StartsWith("\x7fELF"u8)) return data;
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
                    && !data.AsSpan().StartsWith("\x7fELF"u8)) continue;
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