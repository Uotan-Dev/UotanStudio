using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
using UotanStudio.Firmware.CrossCutting.Compress;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher;

/// <summary>Native boot patch workflow that uses the firmware project's parsers and writers.</summary>
public sealed class BootPatchService
{
    public async Task<BootPatchDetection> DetectAsync(string packagePath, CancellationToken cancellationToken = default)
        => await BootPatchAnalyzer.AnalyzeAsync(packagePath, cancellationToken).ConfigureAwait(false);

    /// <summary>Applies a root package to an Android boot image and atomically publishes a new image.</summary>
    public async Task<BootPatchResult> PatchAsync(string packagePath, string bootPath, string outputPath,
        BootPatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new BootPatchOptions();
        options.Progress?.Report(0d);
        var sourceFullPath = Path.GetFullPath(bootPath);
        var destinationFullPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The patched image must be written to a different file.");
        var detection = await DetectAsync(packagePath, cancellationToken).ConfigureAwait(false);
        if (!detection.IsSupported) throw new InvalidDataException("The selected file is not a supported Magisk, KernelSU or APatch package.");
        if (detection.Kind == BootPatchKind.APatch && !APatchSuperKey.IsValid(options.SuperPassword))
            throw new InvalidDataException("APatch SuperKey must be 8-63 characters and contain both a letter and a digit.");
        var arch = options.Architecture is { Length: > 0 } selected && detection.Architectures.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? selected : detection.Architectures[0];
        using var parser = new BootImageParser();
        parser.ParseFile(bootPath);
        string? stockBootSha1 = null;
        if (detection.Kind == BootPatchKind.Magisk)
        {
            await using var sourceBoot = new FileStream(bootPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.SequentialScan);
            stockBootSha1 = Convert.ToHexStringLower(await SHA1.HashDataAsync(sourceBoot, cancellationToken).ConfigureAwait(false));
        }
        options.Progress?.Report(.2d);
        string? bootKmi = null;
        string? kernelSuKmi = null;
        if (detection.Kind == BootPatchKind.KernelSu)
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
        if (parser.Ramdisk.Length == 0 && detection.Kind != BootPatchKind.APatch)
            throw new InvalidDataException("The boot image has no ramdisk to patch.");
        var changes = new List<string>();

        var patchedKernel = parser.Kernel;
        byte[]? kernelDtb = null;
        if (detection.Kind == BootPatchKind.APatch)
        {
            var kpimg = await ReadPackageEntryAsync(packagePath, detection, arch, "kpimg", cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The selected APatch package has no kpimg for the selected architecture.");
            var rawKernel = parser.Header.KernelCompression == CompressionType.None
                ? parser.Kernel
                : BootCompressor.TryDecompress(parser.Kernel, parser.Header.KernelCompression)
                    ?? throw new InvalidDataException("The boot kernel compression is unsupported or corrupt.");
            SplitEmbeddedKernelDtb(parser, rawKernel, out var kernelBody, out kernelDtb);
            var injected = KernelPatchInjector.Patch(kernelBody, kpimg, options.SuperPassword!);
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
            // Magisk's legacy boot flow always requests an initramfs when the
            // stock kernel advertises skip_initramfs.  Keep the explicit option
            // for callers while matching the device patcher by detecting that
            // marker automatically.
            var patched = MagiskKernelPatcher.Apply(kernelBody,
                options.ForceRootfs || MagiskKernelPatcher.NeedsLegacyRootfs(kernelBody), changes);
            // Magisk deliberately keeps the original compressed bytes when no
            // vendor pattern was found.  Recompressing an unchanged kernel can
            // alter headers and invalidate vendor signatures.
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
        if (parser.Ramdisk.Length == 0)
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
        var temp = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllBytesAsync(temp, packed, cancellationToken).ConfigureAwait(false);
            File.Move(temp, outputPath, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        options.Progress?.Report(1d);
        return new BootPatchResult(outputPath, detection with { Architectures = [arch] }, changes);
    }

    private static async Task ApplyPackageAsync(CpioArchiveEditor editor, string packagePath, BootPatchDetection detection,
        string architecture, BootPatchOptions options, List<string> changes, CancellationToken ct, string? bootKmi = null,
        string? stockBootSha1 = null)
    {
        if (detection.Kind == BootPatchKind.KernelSuLkm)
        {
            var ko = await File.ReadAllBytesAsync(packagePath, ct).ConfigureAwait(false);
            editor.AddOrReplace("overlay.d/sbin/kernelsu.ko", ko, Regular(0x81A4));
            changes.Add("install kernelsu.ko");
            return;
        }
        using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var abiNames = AbiDirectories(architecture);
        var initTarget = editor.Exists("init.real") ? "init.real" : "init";
        foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
        {
            var path = entry.FullName.Replace('\\', '/');
            var inAbi = abiNames.Any(a => path.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)
                || path.Contains('/' + a + '/', StringComparison.OrdinalIgnoreCase));
            var isMagiskAsset = detection.Kind == BootPatchKind.Magisk && path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase);
            if (!inAbi && !isMagiskAsset) continue;
            if (detection.Kind == BootPatchKind.Magisk)
            {
                // Magisk's boot_patch.sh injects only the runtime payloads below.
                // util_functions.sh and helper libraries are patch-time inputs and
                // must never become ramdisk files.
                if (!IsMagiskRuntimeEntry(entry.Name, detection.Version)) continue;
                var magiskTarget = MagiskTarget(entry.Name, initTarget, detection.Version);
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
            if (!(entry.Name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("init", StringComparison.OrdinalIgnoreCase))) continue;
            if (detection.Kind == BootPatchKind.KernelSu && entry.Name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase)
                && bootKmi is not null && KernelSuEmbeddedExtractor.TryGetKmi(entry.FullName, out var entryKmi)
                && !string.Equals(entryKmi, bootKmi, StringComparison.OrdinalIgnoreCase))
                continue;
            await using var input = entry.Open();
            using var data = new MemoryStream();
            await input.CopyToAsync(data, ct).ConfigureAwait(false);
            var target = entry.Name.Equals("init", StringComparison.OrdinalIgnoreCase) ? initTarget : $"overlay.d/sbin/{entry.Name}";
            EnsureParentDirectories(editor, target, changes);
            editor.AddOrReplace(target, data.ToArray(), Regular((uint)(entry.Name.Equals("init", StringComparison.OrdinalIgnoreCase) ? 0x81E8 : 0x81A4)));
            changes.Add($"install {target}");
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
        if (detection.Kind == BootPatchKind.KernelSu || detection.Kind == BootPatchKind.APatch)
        {
            foreach (var entry in zip.Entries.Where(e => IsKernelSuLibrary(e.Name)))
            {
                var bytes = await ReadEntryBoundedAsync(entry, ct).ConfigureAwait(false);
                if (entry.Name.EndsWith(".ko", StringComparison.OrdinalIgnoreCase))
                {
                    var target = entry.Name.Equals("ksu.ko", StringComparison.OrdinalIgnoreCase) ? "kernelsu.ko" : entry.Name;
                    EnsureParentDirectories(editor, $"overlay.d/sbin/{target}", changes);
                    editor.AddOrReplace($"overlay.d/sbin/{target}", bytes, Regular(0x81A4));
                    changes.Add($"install {target}");
                    continue;
                }
                if (entry.Name.Equals("ksud", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("ksuinit", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureParentDirectories(editor, $"overlay.d/sbin/{entry.Name}", changes);
                    editor.AddOrReplace($"overlay.d/sbin/{entry.Name}", bytes, Regular(0x81EDu));
                    changes.Add($"install {entry.Name}");
                    continue;
                }
                var extracted = bootKmi is null ? null : KernelSuEmbeddedExtractor.ExtractForKmi(bytes, bootKmi);
                if (extracted is null) continue;
                EnsureParentDirectories(editor, "overlay.d/sbin/kernelsu.ko", changes);
                editor.AddOrReplace("overlay.d/sbin/kernelsu.ko", extracted, Regular(0x81A4));
                changes.Add($"extract and install KernelSU module for KMI {bootKmi}");
                break;
            }
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

    private static string? MagiskTarget(string name, string initTarget, string? version)
    {
        if (name.Equals("init", StringComparison.OrdinalIgnoreCase) || name.Equals("magiskinit", StringComparison.OrdinalIgnoreCase) || name.Equals("libmagiskinit.so", StringComparison.OrdinalIgnoreCase)) return initTarget;
        if (name.Equals("magisk.xz", StringComparison.OrdinalIgnoreCase) || name.Equals("magisk", StringComparison.OrdinalIgnoreCase) || name.Equals("libmagisk.so", StringComparison.OrdinalIgnoreCase)) return "overlay.d/sbin/magisk.xz";
        if (name.Equals("magisk32.xz", StringComparison.OrdinalIgnoreCase) || name.Equals("magisk32", StringComparison.OrdinalIgnoreCase) || name.Equals("libmagisk32.so", StringComparison.OrdinalIgnoreCase)) return "overlay.d/sbin/magisk32.xz";
        if (name.Equals("magisk64.xz", StringComparison.OrdinalIgnoreCase) || name.Equals("magisk64", StringComparison.OrdinalIgnoreCase) || name.Equals("libmagisk64.so", StringComparison.OrdinalIgnoreCase)) return version?.StartsWith("27.", StringComparison.Ordinal) == true ? "overlay.d/sbin/magisk64.xz" : "overlay.d/sbin/magisk.xz";
        if (name.Equals("stub.xz", StringComparison.OrdinalIgnoreCase) || name.Equals("stub.apk", StringComparison.OrdinalIgnoreCase)) return "overlay.d/sbin/stub.xz";
        if (name.Equals("init-ld.xz", StringComparison.OrdinalIgnoreCase) || name.Equals("init-ld", StringComparison.OrdinalIgnoreCase) || name.Equals("libinit-ld.so", StringComparison.OrdinalIgnoreCase)) return "overlay.d/sbin/init-ld.xz";
        if (name.Equals("busybox.xz", StringComparison.OrdinalIgnoreCase) || name.Equals("busybox", StringComparison.OrdinalIgnoreCase) || name.Equals("libbusybox.so", StringComparison.OrdinalIgnoreCase)) return "overlay.d/sbin/busybox.xz";
        if (name.Equals("util_functions.xz", StringComparison.OrdinalIgnoreCase) || name.Equals("util_functions.sh", StringComparison.OrdinalIgnoreCase)) return "overlay.d/sbin/util_functions.xz";
        return name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) ? $"overlay.d/sbin/{name}" : null;
    }

    private static bool IsMagiskRuntimeEntry(string name, string? version)
        => name.Equals("stub.apk", StringComparison.OrdinalIgnoreCase)
            || name.Equals("libmagisk.so", StringComparison.OrdinalIgnoreCase)
            || (version?.StartsWith("27.", StringComparison.Ordinal) == true
                && (name.Equals("libmagisk32.so", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("libmagisk64.so", StringComparison.OrdinalIgnoreCase)))
            || name.Equals("libmagiskinit.so", StringComparison.OrdinalIgnoreCase)
            || name.Equals("libinit-ld.so", StringComparison.OrdinalIgnoreCase);

    private static void SplitEmbeddedKernelDtb(BootImageParser parser, ReadOnlySpan<byte> rawKernel,
        out byte[] kernelBody, out byte[]? kernelDtb)
    {
        kernelBody = rawKernel.ToArray();
        kernelDtb = null;
        // Android v1 images commonly carry a concatenated FDT after the gzip
        // kernel. Preserve that payload when patching the kernel body.
        if (parser.Header.HeaderVersion != BootImageVersion.V1) return;
        ReadOnlySpan<byte> dtbMagic = "\xD0\x0D\xFE\xED"u8;
        for (var offset = Math.Max(0, rawKernel.Length - 8 * 1024 * 1024); offset <= rawKernel.Length - 40; offset++)
        {
            if (!rawKernel.Slice(offset, 4).SequenceEqual(dtbMagic)) continue;
            var total = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(rawKernel.Slice(offset + 4, 4));
            if (total < 40 || total > rawKernel.Length - offset) continue;
            kernelBody = rawKernel[..offset].ToArray();
            kernelDtb = rawKernel[offset..].ToArray();
            return;
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
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
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
        // Magisk records files created by the patch so its restore operation can
        // remove them on the next patch. Keep this list deterministic.
        // Magisk's restore list is a NUL-terminated sequence, rather than a
        // text file with line separators.  Keep the exact delimiter so the
        // next patch can consume it with the same semantics as Magisk.
        var removeList = Encoding.UTF8.GetBytes(
            "overlay.d\0" +
            "overlay.d/sbin\0" +
            "overlay.d/sbin/init-ld.xz\0" +
            "overlay.d/sbin/magisk.xz\0" +
            "overlay.d/sbin/stub.xz\0");
        editor.AddOrReplace(".backup/.rmlist", removeList, Regular(0x8000u));
        changes.Add("write Magisk restore metadata");
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
            var updated = text;
            if (!options.KeepVerity)
            {
                updated = System.Text.RegularExpressions.Regex.Replace(updated, @"(?i)([, ]|^)(avb|verify)(=[^, ]+)?", "$1");
                updated = System.Text.RegularExpressions.Regex.Replace(updated, @"(?i),avb_keys=[^, ]+", string.Empty);
            }
            if (!options.KeepForceEncryption)
                updated = System.Text.RegularExpressions.Regex.Replace(updated, @"(?i)([, ]|^)(forceencrypt|fileencryption|forcefdeorfbe)(=[^, ]+)?", "$1");
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
}
