using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.BootSecurity.Patcher.Kernel;
using UotanStudio.Firmware.Core.Magic;

namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>
/// Detects package family and native architectures from APK/ZIP/KO files.
/// <para>从 APK/ZIP/KO 文件中检测包类型族与原生架构。</para>
/// </summary>
public static class BootPatchAnalyzer
{
    /// <summary>Returns all ABI/KMI metadata discovered in a KernelSU APK or ZIP.
    /// <para>返回在 KernelSU 的 APK 或 ZIP 中发现的全部 ABI/KMI 元数据。</para></summary>
    /// <param name="path">Package file path, opened read-only with FileShare.Read.
    /// <para>包文件路径，以只读且 FileShare.Read 方式打开。</para></param>
    /// <param name="cancellationToken">Checked before opening and between entry scans.
    /// <para>在打开前与条目扫描之间检查。</para></param>
    /// <returns>Detection metadata plus verified embedded assets; every list is empty when the package is not detected as KernelSU.
    /// <para>检测元数据与已校验的内置资源；包未被识别为 KernelSU 时各列表均为空。</para></returns>
    /// <exception cref="IOException">The package file cannot be opened.
    /// <para>无法打开包文件。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public static async Task<KernelSuPackageInfo> AnalyzeKernelSuPackageAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var detection = await AnalyzeAsync(path, stream, cancellationToken).ConfigureAwait(false);
        if (detection.Kind != BootPatchKind.KernelSu)
            return new([], [], []);
        stream.Position = 0;
        using var zip = ZipContainer.OpenRead(stream, leaveOpen: true);
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

    /// <summary>Detects the CPU architecture from an Android boot kernel image.
    /// <para>从 Android boot 镜像的内核中检测 CPU 架构。</para></summary>
    /// <param name="bootPath">Boot image file path parsed from disk.
    /// <para>从磁盘解析的 boot 镜像文件路径。</para></param>
    /// <param name="cancellationToken">Checked after parsing, before kernel inspection.
    /// <para>在解析完成后、检查内核前检查。</para></param>
    /// <returns>An ABI name such as <c>arm64</c> or <c>x64</c>, or <c>null</c> when the kernel cannot be decompressed or matches no architecture.
    /// <para>形如 <c>arm64</c>、<c>x64</c> 的 ABI 名称；内核无法解压或不匹配任何架构时为 <c>null</c>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="bootPath"/> is null or whitespace.
    /// <para><paramref name="bootPath"/> 为 null 或空白。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
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

    /// <summary>Extracts the KernelSU KMI string from the kernel embedded in an Android boot image, running the parse on the thread pool.
    /// <para>从 Android boot 镜像内置内核中提取 KernelSU KMI 字符串，解析在线程池上运行。</para></summary>
    /// <param name="bootPath">Boot image file path parsed from disk.
    /// <para>从磁盘解析的 boot 镜像文件路径。</para></param>
    /// <param name="cancellationToken">Honored by the thread-pool scheduling and checked after parsing.
    /// <para>由线程池调度遵守，并在解析完成后检查。</para></param>
    /// <returns>The KMI string, or <c>null</c> when the kernel cannot be decompressed or carries no extractable version.
    /// <para>KMI 字符串；内核无法解压或不含可提取版本时为 <c>null</c>。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled before or during the scheduled work.
    /// <para>令牌在调度任务期间或之前被取消。</para></exception>
    /// <remarks>Boot image parse failures surface through the returned task from
    /// <c>BootImageParser.ParseFile</c>.
    /// <para>boot 镜像解析失败通过 <c>BootImageParser.ParseFile</c> 返回的任务上报。</para></remarks>
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

    /// <summary>Detects the architecture and KernelSU KMI of the kernel embedded in an Android boot image in one parse and one decompression.
    /// <para>在一次解析与一次解压中同时检测 Android boot 镜像内嵌内核的架构与 KernelSU KMI。</para></summary>
    /// <param name="bootPath">Boot image file path parsed from disk.
    /// <para>从磁盘解析的 boot 镜像文件路径。</para></param>
    /// <param name="cancellationToken">Honored by the thread-pool scheduling and checked after parsing.
    /// <para>由线程池调度遵守，并在解析完成后检查。</para></param>
    /// <returns>The detected ABI name and KMI string; each is <c>null</c> when the kernel cannot be decompressed or carries no matching marker.
    /// <para>检测到的 ABI 名称与 KMI 字符串；内核无法解压或不含对应标记时分别为 <c>null</c>。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled before or during the scheduled work.
    /// <para>令牌在调度任务期间或之前被取消。</para></exception>
    public static Task<BootKernelInfo> DetectBootKernelAsync(string bootPath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var parser = new BootImageParser();
            parser.ParseFile(bootPath);
            cancellationToken.ThrowIfCancellationRequested();
            var kernel = parser.Header.KernelCompression == CompressionType.None
                ? parser.Kernel
                : BootCompressor.TryDecompress(parser.Kernel, parser.Header.KernelCompression);
            return kernel is null
                ? new BootKernelInfo(null, null)
                : new BootKernelInfo(DetectKernelArchitecture(kernel), KernelVersionExtractor.ExtractKmi(kernel));
        }, cancellationToken);

    /// <summary>Reads the architecture, KMI and component presence of a boot image in one parse and one decompression.
    /// <para>在一次解析与一次解压中读取 boot 镜像的架构、KMI 与组件存在性。</para></summary>
    /// <param name="bootPath">Boot image file path parsed from disk.
    /// <para>从磁盘解析的 boot 镜像文件路径。</para></param>
    /// <param name="cancellationToken">Honored by the thread-pool scheduling and checked after parsing.
    /// <para>由线程池调度遵守，并在解析完成后检查。</para></param>
    /// <returns>Kernel identity and component presence; architecture and KMI are <c>null</c> when the image has no
    /// usable kernel. <para>内核标识与组件存在性；镜像没有可用内核时架构与 KMI 为 <c>null</c>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="bootPath"/> is null or whitespace.
    /// <para><paramref name="bootPath"/> 为 null 或空白。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled before or during the scheduled work.
    /// <para>令牌在调度任务期间或之前被取消。</para></exception>
    /// <remarks>Boot image parse failures surface through the returned task from <c>BootImageParser.ParseFile</c>.
    /// <para>boot 镜像解析失败通过 <c>BootImageParser.ParseFile</c> 返回的任务上报。</para></remarks>
    public static Task<BootImageInspection> InspectBootAsync(string bootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootPath);
        return Task.Run(() =>
        {
            using var parser = new BootImageParser();
            parser.ParseFile(bootPath);
            cancellationToken.ThrowIfCancellationRequested();
            var hasKernel = parser.Kernel.Length > 0;
            var hasRamdisk = parser.Ramdisk.Length > 0;
            if (!hasKernel)
                return new BootImageInspection(null, null, hasKernel, hasRamdisk);
            var kernel = parser.Header.KernelCompression == CompressionType.None
                ? parser.Kernel
                : BootCompressor.TryDecompress(parser.Kernel, parser.Header.KernelCompression);
            return kernel is null
                ? new BootImageInspection(null, null, hasKernel, hasRamdisk)
                : new BootImageInspection(DetectKernelArchitecture(kernel), KernelVersionExtractor.ExtractKmi(kernel),
                    hasKernel, hasRamdisk);
        }, cancellationToken);
    }

    internal static string? DetectKernelArchitecture(ReadOnlySpan<byte> kernel)
    {
        if (LooksLikeArm64Image(kernel)) return "arm64";
        var headerLength = Math.Min(kernel.Length, 1024 * 1024);
        if (FileMagic.Identify(kernel[..headerLength])?.Description is { } rootDescription && TryMapArchitecture(rootDescription) is { } detected) return detected;

        ReadOnlySpan<byte> elfMagic = [0x7F, (byte)'E', (byte)'L', (byte)'F'];
        var startLimit = Math.Min(kernel.Length, 64 * 1024 * 1024);
        var scan = kernel[..Math.Min(kernel.Length, startLimit + elfMagic.Length - 1)];
        var searched = 0;
        while (searched < startLimit)
        {
            var relative = scan[searched..].IndexOf(elfMagic);
            if (relative < 0) break;
            var offset = searched + relative;
            var length = Math.Min(kernel.Length - offset, 4096);
            var description = FileMagic.Describe(kernel.Slice(offset, length));
            if (description is not null && TryMapArchitecture(description) is { } elfArchitecture) return elfArchitecture;
            searched = offset + 1;
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

    /// <summary>Detects the package family, ABIs, Magisk options and KernelSU KMIs from a package file on disk.
    /// <para>从磁盘上的包文件检测类型族、ABI、Magisk 选项与 KernelSU KMI。</para></summary>
    /// <param name="path">Package file path, opened read-only with FileShare.Read.
    /// <para>包文件路径，以只读且 FileShare.Read 方式打开。</para></param>
    /// <param name="cancellationToken">Propagated into the stream-based analysis.
    /// <para>传递到基于流的分析流程。</para></param>
    /// <returns>A detection record; unknown family and empty lists when nothing matches.
    /// <para>检测结果记录；无匹配时为未知类型族与空列表。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or whitespace.
    /// <para><paramref name="path"/> 为 null 或空白。</para></exception>
    /// <exception cref="IOException">The file cannot be opened.<para>无法打开该文件。</para></exception>
    public static async Task<BootPatchDetection> AnalyzeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return await AnalyzeAsync(path, stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Detects package metadata from an open stream; the path only contributes its file extension.
    /// <para>从打开的流中检测包元数据；路径仅提供其文件扩展名。</para></summary>
    /// <param name="path">Original file name; a <c>.ko</c> extension selects the kernel-module fast path.
    /// <para>原始文件名；<c>.ko</c> 扩展名会选择内核模块快速路径。</para></param>
    /// <param name="stream">Package stream read from its current position and left open.
    /// <para>从当前位置读取的包流，读取后保持打开。</para></param>
    /// <param name="cancellationToken">Checked before reading and during every bounded read.
    /// <para>在读取前及每次有界读取期间检查。</para></param>
    /// <returns>A detection record; unknown family with empty lists when no package marker matches.
    /// <para>检测结果记录；无包标记匹配时为未知类型族与空列表。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="NotSupportedException">A non-<c>.ko</c> package was handed a stream that cannot seek.
    /// <para>非 <c>.ko</c> 包传入了不可寻址的流。</para></exception>
    /// <exception cref="InvalidDataException">A payload read exceeds the bounded size limit.
    /// <para>某次负载读取超出有界大小上限。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public static async Task<BootPatchDetection> AnalyzeAsync(string path, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var ext = Path.GetExtension(path);
        if (ext.Equals(".ko", StringComparison.OrdinalIgnoreCase))
            return new(BootPatchKind.KernelSuLkm, [DetectElfArchitecture(await ReadBoundedAsync(stream, cancellationToken))]);

        if (!stream.CanSeek) throw new NotSupportedException("Patch package inspection requires a seekable stream.");
        var head = new byte[64];
        var headLength = await ReadHeadAsync(stream, head, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        if (!IsZipMagic(head.AsSpan(0, headLength))
            && (LooksLikeArm64Image(head.AsSpan(0, headLength)) || IsGzipMagic(head.AsSpan(0, headLength))))
        {
            return await AnalyzeRawKernelAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        using var zip = ZipContainer.OpenRead(stream, leaveOpen: true);
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
        var hasKpatchLibrary = names.Any(n => n.EndsWith("libkpatch.so", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("kpatch", StringComparison.OrdinalIgnoreCase));
        var hasBootPatchScript = names.Any(n => n.EndsWith("boot_patch.sh", StringComparison.OrdinalIgnoreCase));
        if (!hasApatch && hasBootPatchScript)
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
        var hasApatchScript = hasKpatchLibrary || (hasBootPatchScript && hasApatch);
        var hasKernelPatchImage = names.Any(n => n.Equals("assets/kpimg", StringComparison.OrdinalIgnoreCase))
            || names.Any(n => n.EndsWith("/kpm/kpimg", StringComparison.OrdinalIgnoreCase));
        var kind = !hasKsu && !hasMagisk && hasKernelPatchImage && !hasApatchScript
            ? BootPatchKind.KernelPatch
            : hasApatch ? BootPatchKind.APatch : hasKsu ? BootPatchKind.KernelSu : hasMagisk ? BootPatchKind.Magisk : BootPatchKind.Unknown;
        if (!hasApatch && !hasKsu && !hasMagisk && names.Any(IsLegacyMagiskEntry))
        {
            kind = BootPatchKind.Magisk;
        }
        var kmiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (kind == BootPatchKind.Unknown)
        {
            var kernelEntry = zip.Entries.FirstOrDefault(e => GkiKernelEntryNames.Contains(e.Name, StringComparer.OrdinalIgnoreCase));
            if (kernelEntry is not null)
            {
                var bytes = await ReadBoundedAsync(kernelEntry.Open(), cancellationToken).ConfigureAwait(false);
                var payloadType = BootCompressor.Detect(bytes);
                var raw = payloadType == CompressionType.None ? bytes : BootCompressor.TryDecompress(bytes, payloadType);
                if (raw is not null && LooksLikeArm64Image(raw))
                {
                    kind = BootPatchKind.GkiKernel;
                    var abi = DetectKernelArchitecture(raw);
                    if (abi is not null) abis.Add(abi);
                    if (KernelVersionExtractor.ExtractKmi(raw) is { } kmi) kmiNames.Add(kmi);
                }
            }
        }
        var unsupportedReason = ResolveUnsupportedReason(kind, names);
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
        }
        if (abis.Count == 0)
        {
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase)))
            {
                await using var es = entry.Open();
                var headerBuffer = new byte[128];
                var headerLength = await ReadHeadAsync(es, headerBuffer, cancellationToken).ConfigureAwait(false);
                var header = headerBuffer.AsSpan(0, headerLength).ToArray();
                if (header.Length >= 20 && header[0] == 0x7f && header[1] == (byte)'E')
                {
                    var abi = DetectElfArchitecture(header);
                    if (!string.Equals(abi, "unknown", StringComparison.OrdinalIgnoreCase) && !abi.StartsWith("elf-", StringComparison.OrdinalIgnoreCase))
                        abis.Add(abi);
                }
            }
        }
        return new(kind, abis, version, kind == BootPatchKind.Magisk ? supportedOptions : null,
            [.. kmiNames.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)],
            kind == BootPatchKind.KernelPatch || hasKernelPatchImage, unsupportedReason);
    }

    private static string? ResolveUnsupportedReason(BootPatchKind kind, string[] names)
    {
        if (kind == BootPatchKind.Magisk)
        {
            if (MagiskPackageLayout.HasRuntimePayload(names)) return null;
            return names.Any(IsLegacyMagiskEntry)
                ? "The Magisk package uses the v21.4 and earlier ZIP layout; select a v22.0 or newer APK to patch a boot image."
                : "The Magisk package carries no installable runtime payload (libmagiskinit, libmagisk32/libmagisk64 or libmagisk), so the boot image cannot be patched from it.";
        }

        if (kind == BootPatchKind.Unknown && names.Any(IsSukiSuManagerOnlyEntry))
        {
            return "The SukiSU build is a manager-only bundle without a boot-image payload; select a build that ships libksud, ksuinit, .ko modules or kpimg.";
        }

        return null;
    }

    private static bool IsLegacyMagiskEntry(string name)
    {
        var file = Path.GetFileName(name);
        return file.Equals("magiskinit", StringComparison.OrdinalIgnoreCase)
            || file.Equals("magiskinit64", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSukiSuManagerOnlyEntry(string name)
    {
        var file = Path.GetFileName(name);
        return file.Equals("libzako.so", StringComparison.OrdinalIgnoreCase)
            || file.Equals("libzakoboot.so", StringComparison.OrdinalIgnoreCase);
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

    internal static readonly string[] GkiKernelEntryNames =
        ["Image", "Image.gz-dtb", "Image.gz", "Image-dtb", "zImage", "zImage-dtb", "bzImage"];

    private static async Task<int> ReadHeadAsync(Stream stream, byte[] head, CancellationToken ct)
    {
        var filled = 0;
        while (filled < head.Length)
        {
            var read = await stream.ReadAsync(head.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
        }

        return filled;
    }

    private static bool IsZipMagic(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == (byte)'P'
        && head[1] == (byte)'K' && head[2] is 0x03 or 0x05 or 0x07 && head[3] is 0x04 or 0x06 or 0x08;

    private static bool IsGzipMagic(ReadOnlySpan<byte> head) => head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B;

    /// <summary>
    /// Recognizes an ARM64 kernel image by the <c>ARMd</c> magic at header offset 0x38.
    /// <para>通过头部 0x38 偏移处的 <c>ARMd</c> 魔数识别 ARM64 内核镜像。</para>
    /// </summary>
    private static bool LooksLikeArm64Image(ReadOnlySpan<byte> image) => image.Length >= 0x3C
        && image[0x38] == (byte)'A' && image[0x39] == (byte)'R' && image[0x3A] == (byte)'M' && image[0x3B] == 0x64;

    private static async Task<BootPatchDetection> AnalyzeRawKernelAsync(Stream stream, CancellationToken ct)
    {
        var payload = await ReadBoundedAsync(stream, ct).ConfigureAwait(false);
        var payloadType = BootCompressor.Detect(payload);
        var raw = payloadType == CompressionType.None ? payload : BootCompressor.TryDecompress(payload, payloadType);
        if (raw is null || !LooksLikeArm64Image(raw)) return new(BootPatchKind.Unknown, []);
        var abi = DetectKernelArchitecture(raw);
        var kmi = KernelVersionExtractor.ExtractKmi(raw);
        return new(BootPatchKind.GkiKernel, abi is null ? [] : [abi], Kmis: kmi is null ? [] : [kmi]);
    }
}