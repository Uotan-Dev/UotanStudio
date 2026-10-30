using System.IO.Compression;
using System.Text;
using UotanStudio.Compress;
using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.Archive.Editing;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.BootSecurity.Patcher.Kernel;
using UotanStudio.Firmware.BootSecurity.Workflow;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>Native boot patch workflow that uses the firmware project's parsers and writers.
/// <para>使用本固件项目解析器与写入器的原生 boot 补丁流程。</para></summary>
public sealed class BootPatchService
{
    /// <summary>
    /// Analyzes a root package file and reports its patch kind, architectures and available modules.
    /// <para>分析 root 包文件，报告其补丁类型、架构与可用模块。</para>
    /// </summary>
    /// <param name="packagePath">
    /// Path to the Magisk, KernelSU, APatch or KernelPatch package to inspect.
    /// <para>待检查的 Magisk、KernelSU 或 APatch 包路径。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels the package analysis. <para>取消包的解析过程。</para></param>
    /// <returns>The detection result for the package.
    /// <para>该包的检测结果。</para></returns>
    public async Task<BootPatchDetection> DetectAsync(string packagePath, CancellationToken cancellationToken = default)
        => await BootPatchAnalyzer.AnalyzeAsync(packagePath, cancellationToken).ConfigureAwait(false);

    /// <summary>Applies a root package to an Android boot image and atomically publishes a new image.
    /// <para>将 root 包应用到 Android boot 镜像，并以原子方式发布新镜像。</para></summary>
    /// <param name="packagePath">Path to the Magisk, KernelSU, APatch or KernelPatch package selected by <see cref="DetectAsync"/>.
    /// <para>由 <see cref="DetectAsync"/> 选定的 Magisk、KernelSU 或 APatch 包路径。</para></param>
    /// <param name="bootPath">Source boot image; it is read only and never overwritten.
    /// <para>源 boot 镜像；只读取，不会被覆盖。</para></param>
    /// <param name="outputPath">Destination image path. The result is written to a temporary file first and moved into place only after the full image is packed; the temporary file is removed on failure.
    /// <para>目标镜像路径。结果先写入临时文件，完整镜像打包完成后才移动到位；失败时删除临时文件。</para></param>
    /// <param name="options">Patch options such as patch mode, architecture, KMI override and verity flags; <see langword="null"/> uses the defaults of <c>BootPatchOptions</c>.
    /// <para>补丁选项（修补模式、架构、KMI 覆盖、verity 开关等）；为 <see langword="null"/> 时使用 <c>BootPatchOptions</c> 的默认值。</para></param>
    /// <param name="cancellationToken">Cancels package reads and the output write; cancellation leaves the existing output file untouched.
    /// <para>取消包的读取与输出写入；取消时既有输出文件保持不变。</para></param>
    /// <returns>The published output path, the detection narrowed to the architecture actually applied, and the ordered list of changes made to the ramdisk and kernel.
    /// <para>已发布的输出路径、收敛到实际应用架构的检测结果，以及对 ramdisk 与内核所做变更的有序列表。</para></returns>
    /// <exception cref="InvalidOperationException"><paramref name="bootPath"/> and <paramref name="outputPath"/> resolve to the same file.
    /// <para><paramref name="bootPath"/> 与 <paramref name="outputPath"/> 解析为同一文件。</para></exception>
    /// <exception cref="InvalidDataException">The package is unsupported, the requested patch mode does not fit the package, the APatch SuperKey is invalid, a KernelPatch run has no SuperKey, the boot image lacks a kernel or a required ramdisk, kernel or ramdisk decompression fails, the KernelSU package has no verified module for the selected KMI or no ksuinit payload, the GKI kernel package carries no kernel image, or the image is already Magisk-patched.
    /// <para>包不受支持、请求的修补模式与包不匹配、APatch SuperKey 无效、KernelPatch 修补缺少 SuperKey、boot 镜像缺少内核或必需的 ramdisk、内核或 ramdisk 解压失败、 KernelSU 包不含所选 KMI 的已验证模块或不含 ksuinit 负载、GKI 内核包不含内核镜像，或镜像已被 Magisk 打过补丁。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled during a read or write.
    /// <para>读取或写入期间令牌被取消。</para></exception>
    /// <example>
    /// <code>
    /// var service = new BootPatchService();
    /// var result = await service.PatchAsync("Magisk-v28.0.apk", "boot.img", "boot_patched.img");
    /// </code>
    /// </example>
    public async Task<BootPatchResult> PatchAsync(string packagePath, string bootPath, string outputPath,
        BootPatchOptions? options = null, CancellationToken cancellationToken = default)
        => await PatchCoreAsync(packagePath, bootPath, outputPath, options, detection: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Applies an already analyzed root package to an Android boot image, skipping the package re-analysis.
    /// <para>把已分析过的 root 包应用到 Android boot 镜像，跳过对包的再次分析。</para></summary>
    /// <param name="packagePath">Path to the package the detection was produced from.
    /// <para>生成检测结果所用的包路径。</para></param>
    /// <param name="bootPath">Source boot image; it is read only and never overwritten.
    /// <para>源 boot 镜像；只读取，不会被覆盖。</para></param>
    /// <param name="outputPath">Destination image path published atomically after the full image is packed.
    /// <para>目标镜像路径；完整镜像打包完成后原子发布。</para></param>
    /// <param name="options">Patch options such as patch mode, architecture, KMI override and verity flags.
    /// <para>补丁选项（修补模式、架构、KMI 覆盖、verity 开关等）。</para></param>
    /// <param name="detection">Detection produced by <see cref="DetectAsync"/> for <paramref name="packagePath"/>.
    /// <para>由 <see cref="DetectAsync"/> 针对 <paramref name="packagePath"/> 生成的检测结果。</para></param>
    /// <param name="cancellationToken">Cancels package reads and the output write.
    /// <para>取消包的读取与输出写入。</para></param>
    /// <returns>The published output path, the applied detection and the ordered change list.
    /// <para>已发布的输出路径、实际应用的检测结果与有序的变更列表。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="detection"/> is <see langword="null"/>.
    /// <para><paramref name="detection"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException"><paramref name="bootPath"/> and <paramref name="outputPath"/> resolve to the same file.
    /// <para><paramref name="bootPath"/> 与 <paramref name="outputPath"/> 解析为同一文件。</para></exception>
    /// <exception cref="InvalidDataException">The detection is unsupported, the requested patch mode does not fit the package, the APatch SuperKey is invalid, a KernelPatch run has no SuperKey, the boot image lacks a kernel or a required ramdisk, kernel or ramdisk decompression fails, the KernelSU package has no verified module for the selected KMI or no ksuinit payload, the GKI kernel package carries no kernel image, or the image is already Magisk-patched.
    /// <para>检测结果不受支持、请求的修补模式与包不匹配、APatch SuperKey 无效、KernelPatch 修补缺少 SuperKey、boot 镜像缺少内核或必需的 ramdisk、内核或 ramdisk 解压失败、 KernelSU 包不含所选 KMI 的已验证模块或不含 ksuinit 负载、GKI 内核包不含内核镜像，或镜像已被 Magisk 打过补丁。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled during a read or write.
    /// <para>读取或写入期间令牌被取消。</para></exception>
    public async Task<BootPatchResult> PatchWithDetectionAsync(string packagePath, string bootPath, string outputPath,
        BootPatchOptions? options, BootPatchDetection detection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detection);
        return await PatchCoreAsync(packagePath, bootPath, outputPath, options, detection, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BootPatchResult> PatchCoreAsync(string packagePath, string bootPath, string outputPath,
        BootPatchOptions? options, BootPatchDetection? detection, CancellationToken cancellationToken)
    {
        options ??= new BootPatchOptions();
        options.Progress?.Report(0d);
        var sourceFullPath = Path.GetFullPath(bootPath);
        var destinationFullPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The patched image must be written to a different file.");
        detection ??= await DetectAsync(packagePath, cancellationToken).ConfigureAwait(false);
        if (!detection.IsSupported)
            throw new InvalidDataException(detection.UnsupportedReason
                ?? "The selected file is not a supported Magisk, KernelSU, APatch, KernelPatch or kernel image package.");
        var mode = ResolvePatchMode(detection, options.Mode);
        if (detection.Kind == BootPatchKind.APatch && !APatchSuperKey.IsValid(options.SuperPassword))
            throw new InvalidDataException("APatch SuperKey must be 8-63 characters and contain both a letter and a digit.");
        if ((detection.Kind == BootPatchKind.KernelPatch || mode == BootPatchMode.Kpm)
            && string.IsNullOrWhiteSpace(options.SuperPassword))
            throw new InvalidDataException("The KernelPatch package requires a non-empty SuperKey.");
        var arch = options.Architecture is { Length: > 0 } selected && detection.Architectures.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? selected : detection.Architectures[0];
        using var parser = new BootImageParser();
        parser.ParseFile(bootPath);
        var role = parser.DetectRole(Core.Repacking.RepackMetadata.DetectPartitionName(Path.GetFileName(bootPath)));
        if (role == BootImageRole.InitBoot &&
            detection.Kind is BootPatchKind.KernelSu or BootPatchKind.APatch or BootPatchKind.KernelPatch)
            throw new InvalidDataException(
                $"{detection.Kind} patches the kernel, but init_boot images carry an empty kernel segment; select the boot image instead of init_boot.");
        string? stockBootSha1 = null;
        if (detection.Kind == BootPatchKind.Magisk)
        {
            await using var sourceBoot = new FileStream(bootPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.SequentialScan);
            stockBootSha1 = Convert.ToHexStringLower(await Sha1.ComputeHashAsync(sourceBoot, cancellationToken).ConfigureAwait(false));
        }
        options.Progress?.Report(.2d);
        string? bootKmi = null;
        string? kernelSuKmi = null;
        if (mode == BootPatchMode.Lkm && detection.Kind == BootPatchKind.KernelSu)
        {
            if (parser.Kernel.Length == 0)
                throw new InvalidDataException("The boot image does not contain a kernel.");
            var kernelForVersion = parser.Header.KernelCompression == CompressionType.None
                ? parser.Kernel
                : BootCompressor.TryDecompress(parser.Kernel, parser.Header.KernelCompression)
                    ?? throw new InvalidDataException("The boot kernel compression is unsupported or corrupt.");
            bootKmi = KernelVersionExtractor.ExtractKmi(kernelForVersion);
            if (string.IsNullOrWhiteSpace(bootKmi))
                throw new InvalidDataException("Unable to determine the boot kernel KMI.");
            if (detection.Kmis is not { Count: > 0 })
                throw new InvalidDataException("KernelSU package does not contain any KMI-specific modules.");
            kernelSuKmi = string.IsNullOrWhiteSpace(options.KernelSuKmi) ? bootKmi : options.KernelSuKmi.Trim();
            if (!detection.Kmis.Contains(kernelSuKmi, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"KernelSU package does not contain a module for KMI '{kernelSuKmi}'.");
        }
        if (parser.Ramdisk.Length == 0 && detection.Kind is not (BootPatchKind.APatch or BootPatchKind.KernelPatch)
            && mode is not (BootPatchMode.Gki or BootPatchMode.Kpm))
            throw new InvalidDataException("The boot image has no ramdisk to patch.");
        var changes = new List<string>();

        var patchedKernel = parser.Kernel;
        byte[]? kernelDtb = null;
        if (mode == BootPatchMode.Gki)
        {
            if (parser.Kernel.Length == 0)
                throw new InvalidDataException("The boot image does not contain a kernel to replace.");
            if (parser.Header.HeaderVersion == BootImageVersion.V1)
            {
                var rawStock = parser.Header.KernelCompression == CompressionType.None
                    ? parser.Kernel
                    : BootCompressor.TryDecompress(parser.Kernel, parser.Header.KernelCompression)
                        ?? throw new InvalidDataException("The boot kernel compression is unsupported or corrupt.");
                SplitEmbeddedKernelDtb(parser, rawStock, out _, out kernelDtb);
            }

            var replacement = await LoadGkiKernelAsync(packagePath, cancellationToken).ConfigureAwait(false);
            SplitEmbeddedKernelDtb(parser, replacement, out _, out var replacementDtb);
            byte[] kernelBody;
            if (replacementDtb is { Length: > 0 } || kernelDtb is not { Length: > 0 })
            {
                kernelBody = replacement;
            }
            else
            {
                kernelBody = [.. replacement, .. kernelDtb];
                changes.Add("re-attach the original kernel DTB");
            }

            patchedKernel = parser.Header.KernelCompression == CompressionType.None
                ? kernelBody
                : CompressRamdisk(kernelBody, parser.Header.KernelCompression);
            changes.Add("replace boot kernel with the supplied GKI image");
        }
        else if (detection.Kind is BootPatchKind.APatch or BootPatchKind.KernelPatch
            || mode == BootPatchMode.Kpm)
        {
            var kpimg = await ReadPackageEntryAsync(packagePath, detection, arch, "kpimg", cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The selected KernelPatch package has no kpimg for the selected architecture.");
            var rawKernel = parser.Header.KernelCompression == CompressionType.None
                ? parser.Kernel
                : BootCompressor.TryDecompress(parser.Kernel, parser.Header.KernelCompression)
                    ?? throw new InvalidDataException("The boot kernel compression is unsupported or corrupt.");
            var targetKernelArchitecture = BootPatchAnalyzer.DetectKernelArchitecture(rawKernel);
            if (!string.Equals(targetKernelArchitecture, "arm64", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"KernelPatch and APatch injection support arm64 boot kernels only; the boot kernel is {targetKernelArchitecture ?? "unrecognized"}.");
            SplitEmbeddedKernelDtb(parser, rawKernel, out var kernelBody, out kernelDtb);
            var injected = KernelPatchInjector.Patch(kernelBody.Span, kpimg, options.SuperPassword!);
            if (kernelDtb is { Length: > 0 }) injected = [.. injected, .. kernelDtb];
            patchedKernel = parser.Header.KernelCompression == CompressionType.None
                ? injected
                : CompressRamdisk(injected, parser.Header.KernelCompression);
            changes.Add("inject KernelPatch kpimg into kernel");
        }
        else if (detection.Kind == BootPatchKind.Magisk && parser.Kernel.Length > 0)
        {
            var rawKernel = parser.Header.KernelCompression == CompressionType.None
                ? parser.Kernel
                : BootCompressor.TryDecompress(parser.Kernel, parser.Header.KernelCompression)
                    ?? throw new InvalidDataException("The boot kernel compression is unsupported or corrupt.");
            SplitEmbeddedKernelDtb(parser, rawKernel, out var kernelBody, out kernelDtb);
            var beforeKernelChanges = changes.Count;
            var patched = MagiskKernelPatcher.ApplyDetectingRootfs(kernelBody.Span, options.ForceRootfs, changes);
            if (changes.Count == beforeKernelChanges)
                patchedKernel = parser.Kernel;
            else
            {
                if (kernelDtb is { Length: > 0 })
                {
                    patched = [.. patched, .. kernelDtb];
                }

                patchedKernel = parser.Header.KernelCompression == CompressionType.None
                    ? patched
                    : CompressRamdisk(patched, parser.Header.KernelCompression);
            }
        }

        byte[] compressed;
        if (detection.Kind is BootPatchKind.KernelPatch or BootPatchKind.APatch || mode is BootPatchMode.Gki
            || (mode == BootPatchMode.Kpm && detection.Kind == BootPatchKind.KernelSu))
        {
            compressed = parser.Ramdisk;
        }
        else if (parser.Ramdisk.Length == 0)
        {
            compressed = [];
        }
        else
        {
            var rawRamdisk = parser.Header.RamdiskCompression == CompressionType.None
                ? parser.Ramdisk
                : BootCompressor.TryDecompress(parser.Ramdisk, parser.Header.RamdiskCompression)
                    ?? throw new InvalidDataException("The boot ramdisk compression is unsupported or corrupt.");
            var editor = new CpioArchiveEditor();
            using (var ramdiskStream = new MemoryStream(rawRamdisk, writable: false)) editor.Load(ramdiskStream);
            if (detection.Kind is BootPatchKind.KernelSu or BootPatchKind.KernelSuLkm
                && (editor.Exists(".backup/.magisk") || editor.Exists("overlay.d/sbin/magisk.xz")))
                throw new InvalidDataException("Cannot work with a Magisk patched image; restore the stock image first.");
            var stockInit = editor.Find("init")?.Data;
            PatchFstab(editor, options, changes);
            await ApplyPackageAsync(editor, packagePath, detection, arch, options, changes, cancellationToken, kernelSuKmi, stockBootSha1).ConfigureAwait(false);
            if (detection.Kind == BootPatchKind.Magisk)
                AddMagiskBackupEntries(editor, stockInit, changes);
            options.Progress?.Report(.65d);
            using var rebuilt = new MemoryStream();
            editor.Save(rebuilt);
            compressed = parser.Header.RamdiskCompression == CompressionType.None
                ? rebuilt.ToArray()
                : CompressRamdisk(rebuilt.ToArray(), parser.Header.RamdiskCompression);
        }
        options.Progress?.Report(.65d);
        var packed = BootImagePacker.Repack(parser, kernel: patchedKernel, ramdisk: compressed);
        options.Progress?.Report(.85d);
        var temp = TempFileHelper.CreateAdjacentPath(outputPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllBytesAsync(temp, packed, cancellationToken).ConfigureAwait(false);
            AtomicPublish.Publish(temp, outputPath);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        options.Progress?.Report(1d);
        return new BootPatchResult(outputPath, detection with { Architectures = [arch] }, changes);
    }

    private static async Task ApplyPackageAsync(CpioArchiveEditor editor, string packagePath, BootPatchDetection detection,
        string architecture, BootPatchOptions options, List<string> changes, CancellationToken ct, string? bootKmi = null,
        string? stockBootSha1 = null)
    {
        var initTarget = editor.Exists("init.real") ? "init.real" : "init";
        var initStaged = false;
        var moduleInstalled = false;
        if (detection.Kind == BootPatchKind.KernelSuLkm)
        {
            var ko = await File.ReadAllBytesAsync(packagePath, ct).ConfigureAwait(false);
            EnsureParentDirectories(editor, "kernelsu.ko", changes);
            editor.AddOrReplace("kernelsu.ko", ko, Regular(0x81A4));
            changes.Add("install kernelsu.ko");
            return;
        }
        using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = ZipContainer.OpenRead(fs);
        var abiNames = AbiDirectories(architecture);
        var entryNames = zip.Entries.Select(static e => e.FullName.Replace('\\', '/')).ToArray();
        var magiskSplit = MagiskPackageLayout.UsesSplitLayout(entryNames);
        foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
        {
            var path = entry.FullName.Replace('\\', '/');
            var inAbi = abiNames.Any(a => path.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)
                || path.Contains('/' + a + '/', StringComparison.OrdinalIgnoreCase));
            var isMagiskAsset = detection.Kind == BootPatchKind.Magisk && path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase);
            if (!inAbi && !isMagiskAsset) continue;
            if (detection.Kind == BootPatchKind.Magisk)
            {
                if (!MagiskPackageLayout.IsRuntimeEntry(entry.Name, magiskSplit)) continue;
                var magiskTarget = MagiskPackageLayout.Target(entry.Name, initTarget, magiskSplit);
                if (magiskTarget is null) continue;
                var bytes = await ReadEntryBoundedAsync(entry, ct).ConfigureAwait(false);
                if (!entry.Name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) && magiskTarget.EndsWith(".xz", StringComparison.OrdinalIgnoreCase))
                    bytes = CompressionService.Compress(bytes, CompressionFormat.Xz);
                EnsureParentDirectories(editor, magiskTarget, changes);
                editor.AddOrReplace(magiskTarget, bytes, Regular(magiskTarget.Equals(initTarget, StringComparison.Ordinal) ? 0x81E8u : 0x81A4u));
                changes.Add($"install {magiskTarget}");
                continue;
            }
            if (detection.Kind == BootPatchKind.KernelSu && IsKernelSuLibrary(entry.Name)) continue;
            var isKsuInit = detection.Kind == BootPatchKind.KernelSu && entry.Name.Equals("init", StringComparison.OrdinalIgnoreCase);
            if (!isKsuInit && !(entry.Name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase))) continue;
            if (detection.Kind == BootPatchKind.KernelSu && entry.Name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase)
                && bootKmi is not null && KernelSuEmbeddedExtractor.TryGetKmi(entry.FullName, out var entryKmi)
                && !string.Equals(entryKmi, bootKmi, StringComparison.OrdinalIgnoreCase))
                continue;
            if (isKsuInit)
            {
                await using var initInput = entry.Open();
                using var initData = new MemoryStream();
                await initInput.CopyToAsync(initData, ct).ConfigureAwait(false);
                editor.AddOrReplace("ksuinit", initData.ToArray(), Regular(0x81ED));
                changes.Add("stage ksuinit");
                InstallKernelSuInit(editor, initTarget, changes);
                initStaged = true;
                continue;
            }
            await using var input = entry.Open();
            using var data = new MemoryStream();
            await input.CopyToAsync(data, ct).ConfigureAwait(false);
            EnsureParentDirectories(editor, "kernelsu.ko", changes);
            editor.AddOrReplace("kernelsu.ko", data.ToArray(), Regular(0x81A4));
            changes.Add("install kernelsu.ko");
            moduleInstalled = true;
        }
        if (detection.Kind == BootPatchKind.Magisk)
        {
            var config = Encoding.UTF8.GetBytes($"KEEPVERITY={(options.KeepVerity ? "true" : "false")}\n" +
                $"KEEPFORCEENCRYPT={(options.KeepForceEncryption ? "true" : "false")}\n" +
                $"RECOVERYMODE={(options.RecoveryMode ? "true" : "false")}\n" +
                "VENDORBOOT=false\n" +
                (string.IsNullOrWhiteSpace(options.PreInitDevice) ? string.Empty : $"PREINITDEVICE={options.PreInitDevice.Trim()}\n") +
                (string.IsNullOrWhiteSpace(stockBootSha1) ? string.Empty : $"SHA1={stockBootSha1}\n"));
            if (editor.Exists(".backup/.magisk")) editor.Remove(".backup/.magisk");
            editor.AddOrReplace(".backup", [], new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Directory,
                Mode = 0x4000,
                UnixPermissions = 0,
                LinkCount = 2
            });
            editor.AddOrReplace(".backup/.magisk", config, new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Regular,
                Mode = 0x8000,
                UnixPermissions = 0,
                LinkCount = 1
            });
            changes.Add("write Magisk patch configuration");
            if (options.PatchVbmetaFlag)
                changes.Add("enable Magisk PATCHVBMETAFLAG");
        }
        if (detection.Kind == BootPatchKind.KernelSu)
        {
            foreach (var entry in zip.Entries.Where(e => IsKernelSuLibrary(e.Name)))
            {
                var bytes = await ReadEntryBoundedAsync(entry, ct).ConfigureAwait(false);
                if (entry.Name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureParentDirectories(editor, "kernelsu.ko", changes);
                    editor.AddOrReplace("kernelsu.ko", bytes, Regular(0x81A4));
                    changes.Add("install kernelsu.ko");
                    moduleInstalled = true;
                    continue;
                }
                if (entry.Name.Equals("ksuinit", StringComparison.OrdinalIgnoreCase))
                {
                    editor.AddOrReplace("ksuinit", bytes, Regular(0x81ED));
                    changes.Add("stage ksuinit");
                    InstallKernelSuInit(editor, initTarget, changes);
                    initStaged = true;
                    continue;
                }
                if (!initStaged
                    && KernelSuEmbeddedExtractor.ExtractNamed(bytes, "ksuinit") is { } ksuinit)
                {
                    editor.AddOrReplace("ksuinit", ksuinit, Regular(0x81ED));
                    changes.Add("stage ksuinit from embedded assets");
                    InstallKernelSuInit(editor, initTarget, changes);
                    initStaged = true;
                }
                if (moduleInstalled) continue;
                var extracted = bootKmi is null ? null : KernelSuEmbeddedExtractor.ExtractForKmi(bytes, bootKmi);
                if (extracted is null) continue;
                var moduleArchitecture = BootPatchAnalyzer.DetectElfArchitecture(extracted);
                if (moduleArchitecture is "arm64" or "arm32" or "x64" or "x86"
                    && !string.Equals(moduleArchitecture, architecture, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"The KernelSU module for KMI {bootKmi} targets {moduleArchitecture}, while the boot image ABI is {architecture}.");
                EnsureParentDirectories(editor, "kernelsu.ko", changes);
                editor.AddOrReplace("kernelsu.ko", extracted, Regular(0x81A4));
                changes.Add($"extract and install KernelSU module for KMI {bootKmi}");
                moduleInstalled = true;
            }

            if (!moduleInstalled)
                throw new InvalidDataException(
                    $"The KernelSU package has no verified module for KMI '{bootKmi}'.");
            if (!initStaged)
                throw new InvalidDataException("The KernelSU package has no ksuinit payload to install.");
        }
        var script = zip.Entries.FirstOrDefault(e => e.Name.Equals("util_functions.sh", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("boot-patch.sh", StringComparison.OrdinalIgnoreCase));
        if (script is not null)
        {
            using var reader = new StreamReader(script.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            _ = BootPatchScriptParser.Parse(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
        }
    }

    private static string[] AbiDirectories(string architecture) => architecture.ToLowerInvariant() switch
    {
        "arm64" => ["arm64-v8a", "arm64", "aarch64"],
        "arm32" => ["armeabi-v7a", "armeabi", "arm"],
        "x64" => ["x86_64", "x64"],
        "x86" => ["x86", "i686"],
        "mips" => ["mips"],
        "mips64" => ["mips64"],
        "riscv64" => ["riscv64"],
        _ => [architecture]
    };

    internal static void SplitEmbeddedKernelDtb(BootImageParser parser, ReadOnlyMemory<byte> rawKernel,
        out ReadOnlyMemory<byte> kernelBody, out byte[]? kernelDtb)
    {
        kernelBody = rawKernel;
        kernelDtb = null;
        if (parser.Header.HeaderVersion != BootImageVersion.V1) return;
        ReadOnlySpan<byte> dtbMagic = [0xD0, 0x0D, 0xFE, 0xED];
        var firstOffset = Math.Max(0, rawKernel.Length - 8 * 1024 * 1024);
        var lastOffset = rawKernel.Length - 40;
        if (lastOffset < firstOffset) return;
        var window = rawKernel.Span[firstOffset..(lastOffset + dtbMagic.Length)];
        var searched = 0;
        while (searched <= window.Length - dtbMagic.Length)
        {
            var relative = window[searched..].IndexOf(dtbMagic);
            if (relative < 0) return;
            var offset = firstOffset + searched + relative;
            var total = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(rawKernel.Span.Slice(offset + 4, 4));
            if (total >= 40 && total <= rawKernel.Length - offset)
            {
                kernelBody = rawKernel[..offset];
                kernelDtb = rawKernel[offset..].ToArray();
                return;
            }
            searched += relative + 1;
        }
    }

    private static bool IsKernelSuLibrary(string name)
        => name.EndsWith("libksud.so", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("libkernelsu.so", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("libsu.so", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ksud", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ksuinit", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("kernelsu.ko", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ksu.ko", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("susfs.ko", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadEntryBoundedAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        const long max = 256L * 1024 * 1024;
        if (entry.Length > max) throw new InvalidDataException($"Patch entry '{entry.FullName}' exceeds the bounded limit.");
        await using var input = entry.Open();
        using var data = new MemoryStream(entry.Length > 0 && entry.Length <= int.MaxValue ? (int)entry.Length : 0);
        await input.CopyToAsync(data, ct).ConfigureAwait(false);
        if (data.Length > max) throw new InvalidDataException("Patch entry exceeds the bounded limit.");
        return data.ToArray();
    }

    private static async Task<byte[]?> ReadPackageEntryAsync(string packagePath, BootPatchDetection detection,
        string architecture, string fileName, CancellationToken ct)
    {
        using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.SequentialScan);
        using var zip = ZipContainer.OpenRead(fs);
        var abiNames = AbiDirectories(architecture);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
            abiNames.Any(a => e.FullName.Replace('\\', '/').Contains('/' + a + '/', StringComparison.OrdinalIgnoreCase)))
            ?? zip.Entries.FirstOrDefault(e => e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                e.FullName.Replace('\\', '/').StartsWith("assets/", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        await using var input = entry.Open();
        using var data = new MemoryStream();
        await input.CopyToAsync(data, ct).ConfigureAwait(false);
        return data.ToArray();
    }

    private static ArchiveEntryMetadata Regular(uint mode) => new() { FileType = CpioFileType.Regular, Mode = mode, UnixPermissions = (int)(mode & 0x1FF), LinkCount = 1 };

    private static ArchiveEntryMetadata DirectoryMetadata(uint mode) => new() { FileType = CpioFileType.Directory, Mode = mode, UnixPermissions = (int)(mode & 0x1FF), LinkCount = 2 };

    private static void AddMagiskBackupEntries(CpioArchiveEditor editor, byte[]? stockInit, List<string> changes)
    {
        if (stockInit is not { Length: > 0 }) return;
        EnsureParentDirectories(editor, ".backup/.magisk", changes);
        var compressedInit = CompressionService.Compress(stockInit, CompressionFormat.Xz);
        editor.AddOrReplace(".backup/init.xz", compressedInit, Regular(0x81E8u));
        var removeList = Encoding.UTF8.GetBytes(
            "overlay.d\0" +
            "overlay.d/sbin\0" +
            "overlay.d/sbin/init-ld.xz\0" +
            "overlay.d/sbin/magisk.xz\0" +
            "overlay.d/sbin/stub.xz\0");
        editor.AddOrReplace(".backup/.rmlist", removeList, Regular(0x8000u));
        changes.Add("write Magisk restore metadata");
    }

    /// <summary>
    /// Installs the KernelSU init: the stock init moves to
    /// init.real (when present) and ksuinit becomes the root init with 0755.
    /// <para>安装 KernelSU 的 init：原有 init 移动到 init.real（存在时），ksuinit 以 0755 成为根 init。</para>
    /// </summary>
    private static void InstallKernelSuInit(CpioArchiveEditor editor, string initTarget, List<string> changes)
    {
        var ksuinit = editor.Find("ksuinit")?.Data;
        if (ksuinit is not { Length: > 0 }) return;
        var stockInit = editor.Find("init")?.Data;
        if (stockInit is { Length: > 0 } && !editor.Exists("init.real"))
        {
            editor.Remove("init");
            editor.AddOrReplace("init.real", stockInit, Regular(0x81E8));
            changes.Add("rename init to init.real");
        }
        editor.AddOrReplace("init", ksuinit, Regular(0x81ED));
        changes.Add("install init (ksuinit)");
    }

    private static void EnsureParentDirectories(CpioArchiveEditor editor, string path, List<string> changes)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            current = current.Length == 0 ? parts[i] : $"{current}/{parts[i]}";
            if (editor.Exists(current)) continue;
            editor.AddOrReplace(current, [], DirectoryMetadata(0x41E8u));
            changes.Add($"create {current} directory");
        }
    }

    private static void PatchFstab(CpioArchiveEditor editor, BootPatchOptions options, List<string> changes)
    {
        if (options.KeepVerity && options.KeepForceEncryption) return;
        foreach (var entry in editor.Entries.Where(e => e.Name.Contains("fstab", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (entry.Data is not { Length: > 0 } data || data.Length > 4 * 1024 * 1024) continue;
            var text = Encoding.UTF8.GetString(data);
            var updated = FstabVerificationEditor.Patch(text, !options.KeepVerity, !options.KeepForceEncryption);
            if (!string.Equals(updated, text, StringComparison.Ordinal))
            {
                editor.AddOrReplace(entry.Name, Encoding.UTF8.GetBytes(updated), entry.Metadata);
                changes.Add($"patch {entry.Name} for verity/encryption options");
            }
        }
    }

    private static byte[] CompressRamdisk(byte[] data, CompressionType type)
    {
        var format = type switch
        {
            CompressionType.Gzip => CompressionFormat.Gzip,
            CompressionType.Xz => CompressionFormat.Xz,
            CompressionType.Bzip2 => CompressionFormat.Bzip2,
            CompressionType.Zstd => CompressionFormat.Zstd,
            CompressionType.Lz4 => CompressionFormat.Lz4,
            CompressionType.Lzma => CompressionFormat.Lzma,
            _ => throw new NotSupportedException($"Cannot recompress ramdisk as {type}.")
        };
        return CompressionService.Compress(data, format);
    }

    /// <summary>
    /// Resolves the effective patch mode: Auto picks the strategy from the package content — KernelSU
    /// packages with verified module payloads take the LKM path, KernelSU packages that only carry a
    /// KernelPatch image take the KPM path, and kernel images or AnyKernel3 packages take the GKI path;
    /// explicit modes are rejected when the package cannot perform them.
    /// <para>解析实际修补模式：Auto 按包内容选择策略——带已验证模块负载的 KernelSU 包走 LKM，仅携带
    /// KernelPatch 镜像的 KernelSU 包走 KPM，内核镜像或 AnyKernel3 压缩包走 GKI；包无法执行的显式模式会被拒绝。</para>
    /// </summary>
    internal static BootPatchMode ResolvePatchMode(BootPatchDetection detection, BootPatchMode requested)
    {
        if (requested != BootPatchMode.Auto)
        {
            var supported = requested switch
            {
                BootPatchMode.Lkm => detection.Kind is BootPatchKind.KernelSu or BootPatchKind.KernelSuLkm,
                BootPatchMode.Gki => detection.Kind == BootPatchKind.GkiKernel,
                BootPatchMode.Kpm => detection.Kind is BootPatchKind.KernelSu or BootPatchKind.KernelPatch,
                _ => false,
            };
            if (!supported) throw new InvalidDataException($"The {detection.Kind} package does not support {requested} patch mode.");
            return requested;
        }

        return detection.Kind switch
        {
            BootPatchKind.KernelPatch => BootPatchMode.Kpm,
            BootPatchKind.KernelSu => detection.Kmis is { Count: > 0 } || !detection.HasKernelPatchImage
                ? BootPatchMode.Lkm
                : BootPatchMode.Kpm,
            BootPatchKind.KernelSuLkm => BootPatchMode.Lkm,
            BootPatchKind.GkiKernel => BootPatchMode.Gki,
            _ => BootPatchMode.Auto,
        };
    }

    /// <summary>
    /// Loads the replacement kernel for GKI mode from the package itself: a raw kernel image or the kernel
    /// entry of an AnyKernel3-style package, decompressed to its raw body.
    /// <para>从包本身加载 GKI 模式的替换内核：原始内核镜像或 AnyKernel3 风格压缩包中的内核条目，并解压为原始内核体。</para>
    /// </summary>
    private static async Task<byte[]> LoadGkiKernelAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.SequentialScan);
        var head = new byte[4];
        var filled = 0;
        while (filled < head.Length)
        {
            var read = await stream.ReadAsync(head.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
        }

        stream.Position = 0;
        var payload = filled == 4 && head[0] == (byte)'P' && head[1] == (byte)'K'
            ? await ReadZipKernelEntryAsync(stream, ct).ConfigureAwait(false)
            : await ReadBoundedStreamAsync(stream, ct).ConfigureAwait(false);
        var type = BootCompressor.Detect(payload);
        return type == CompressionType.None
            ? payload
            : BootCompressor.TryDecompress(payload, type)
                ?? throw new InvalidDataException("The replacement GKI kernel compression is unsupported or corrupt.");
    }

    private static async Task<byte[]> ReadZipKernelEntryAsync(Stream stream, CancellationToken ct)
    {
        using var zip = ZipContainer.OpenRead(stream, leaveOpen: true);
        foreach (var candidate in BootPatchAnalyzer.GkiKernelEntryNames)
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            return await ReadEntryBoundedAsync(entry, ct).ConfigureAwait(false);
        }

        throw new InvalidDataException("The kernel package contains no kernel image entry (Image/Image.gz/zImage).");
    }

    private static async Task<byte[]> ReadBoundedStreamAsync(Stream stream, CancellationToken ct)
    {
        const long max = 256L * 1024 * 1024;
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        while (output.Length < max)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, max - output.Length)), ct).ConfigureAwait(false);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }

        if (output.Length >= max && stream.ReadByte() >= 0)
            throw new InvalidDataException("The replacement GKI kernel exceeds the bounded limit.");
        return output.ToArray();
    }
}