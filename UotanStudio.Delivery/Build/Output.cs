using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Reading;
using UotanStudio.Firmware.BoundedContexts.Compression;
using UotanStudio.Firmware.BoundedContexts.Compression.Compressors;

namespace UotanStudio.Services.Delivery;

/// <summary><para>导出过程中的进度快照：百分比、消息文本及可选的字节/条目/阶段详细信息。</para>
/// A progress snapshot during an export: percent, message text, and optional byte/item/stage detail.</summary>
internal sealed record DeliveryProgress(double Percent, string Message,
    (long Processed, long Total)? Bytes = null, string? Stage = null,
    (long Processed, long Total)? Items = null, string? Detail = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary><para>按交付计划在暂存目录准备平台工具。</para>Prepares platform tools in a staging directory for a delivery plan.</summary>
internal interface IDeliveryTools
{
    /// <summary>
    /// <para>把交付所需工具（platform-tools 与 zstd）解压/下载到 staging 目录下的 bin 布局。</para>
    /// Prepares the tools a delivery needs (platform-tools and zstd) into the bin layout under the staging directory.
    /// </summary>
    Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token);
}

/// <summary><para>先在暂存目录里逐个准备每个文件，再发布：覆盖调用方批准的目标，保留其余文件。</para>
/// Prepares every file privately, then publishes: overwrites caller-approved destinations and keeps the rest.</summary>
internal sealed class DeliveryOutputService(IDeliveryTools tools, IDeliverySigningKeyProvider? signingKeys = null)
{
    private static readonly HashSet<string> FirmwareUpdateImages = new(StringComparer.OrdinalIgnoreCase)
    {
        "abl", "aop_config", "aop", "bluetooth", "cpucp", "devcfg", "dsp", "dtbo", "featenabler", "hyp",
        "imagefv", "keymaster", "modem", "multiimgqti", "qupfw", "recovery", "shrm", "tz", "uefi",
        "uefisecapp", "vbmeta_system", "vbmeta", "vendor_boot", "xbl_config", "xbl_ramdump", "xbl"
    };

    private static string OrganizedImagePath(DeliveryPlan plan, string stage, DeliveryFile file)
    {
        if (!plan.PackSuper) return Path.Combine(stage, "images", file.FileName);
        return Path.Combine(stage, OrganizedImageRelativePath(file));
    }

    /// <summary>
    /// <para>计算单个镜像在组织布局下的相对路径：固件镜像放入 firmware-update/，其余放根目录。</para>
    /// Computes a single image's relative path in the organized layout: firmware images go under firmware-update/, others at the root.
    /// </summary>
    internal static string OrganizedImageRelativePath(DeliveryFile file)
    {
        var stem = Path.GetFileNameWithoutExtension(file.FileName);
        return FirmwareUpdateImages.Contains(stem) ? Path.Combine("firmware-update", file.FileName) : file.FileName;
    }

    /// <summary>
    /// <para>在暂存目录中逐一准备每个文件后原子发布到输出目录：覆盖调用方批准的目标，保留其余已有文件。
    /// 校验在消除误报的同时不允许输出覆盖已选源镜像——输出目录不得等于任一源镜像所在目录（避免 images/images 嵌套），
    /// 且按组织布局写出的目标路径不得与任一选中源镜像重合。</para>
    /// Prepares every file privately in a staging directory, then atomically publishes to the output: overwrites
    /// caller-approved destinations and keeps the rest. Validation rejects false positives yet forbids the output
    /// from overwriting a selected source image—the output directory must not equal any source's own directory
    /// (avoiding images/images nesting), and no written destination may coincide with a selected source path.
    /// </summary>
    internal async Task<string[]> ExportAsync(DeliveryPlan plan, IProgress<DeliveryProgress>? progress = null, CancellationToken token = default,
        IReadOnlySet<string>? overwrite = null)
    {
        plan = plan.ForOutput();
        DeliveryValidation.Validate(plan);
        if (string.IsNullOrWhiteSpace(plan.OutputDirectory)) throw new InvalidDataException("Select an output directory.");
        var output = Path.GetFullPath(plan.OutputDirectory);
        CheckDirectory(output);
        var sources = plan.Files.Concat(plan.Container).Where(f => f.SourcePath.Length > 0).ToArray();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var plannedOutputs = PlannedOutputPaths(plan).Select(path => Path.GetFullPath(path)).ToArray();
        foreach (var source in sources)
        {
            if (string.Equals(Path.GetFullPath(output), GetDirectoryNameOrRoot(source.SourcePath), comparison))
                throw new InvalidDataException("输出目录不能是源镜像所在目录 / Output must not target a source image's own directory.");
            var sourcePath = Path.GetFullPath(source.SourcePath);
            for (var i = 0; i < plannedOutputs.Length; i++)
                if (string.Equals(sourcePath, plannedOutputs[i], comparison))
                    throw new InvalidDataException("输出会覆盖源镜像 / Output would overwrite a source image: " + source.SourcePath);
        }
        foreach (var source in new[] { plan.Payload.DynamicPartitionsFile, plan.Payload.SignatureFile, plan.Payload.SigningKeyPath, plan.Payload.PostinstallFile, plan.Package.SigningKeyPath, plan.Package.CertificatePath }.Where(p => p.Length > 0))
            if (IsUnder(source, output)) throw new InvalidDataException("Output must not contain Payload configuration inputs.");
        if (DeliveryPackageBuilder.HasDat(plan) && IsUnder(DeliveryRecoveryUpdater.Resolve(plan), output))
            throw new InvalidDataException("Output must not contain the source Recovery updater.");
        if (plan.OrganizeDirectory && !plan.PackPayload && (plan.Windows || plan.Linux || plan.MacOS || plan.Txt || plan.Recovery && plan.Scripts.ContainsKey("update-binary"))
            && plan.ScriptFingerprint != DeliveryScriptGenerator.Fingerprint(plan))
            throw new InvalidDataException("脚本已过期，请重新生成 / Scripts are stale; generate them again.");
        var parent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Invalid output directory.");
        foreach (var destination in PlannedOutputPaths(plan))
        {
            CheckDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination)) throw new IOException("输出位置被目录占用 / A directory blocks the output: " + destination);
            if (overwrite is null && File.Exists(destination)) throw new IOException("目标文件已存在 / Output already exists: " + destination);
        }
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".uotan-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var imageDirectory = plan.OrganizeDirectory && !plan.PackSuper ? Path.Combine(stage, "images") : stage;
        Directory.CreateDirectory(imageDirectory);
        try
        {
            progress?.Report(new(0, "Checking inputs", Stage: "Delivery_ProgressChecking"));
            if (plan.PackPayload) await DeliveryPayloadBuilder.BuildAsync(plan, Path.Combine(imageDirectory, "payload.bin"), progress, token, signingKeys).ConfigureAwait(false);
            if (plan.PackSuper)
            {
                if (plan.Super.Empty)
                    await DeliverySuperBuilder.BuildAsync(plan.Container, plan.Super,
                        Path.Combine(imageDirectory, "super_empty.img"),
                        null, token,
                        new InlineProgress<DeliveryProgress>(p => progress?.Report(p with { Percent = p.Percent * .55 }))).ConfigureAwait(false);
                else
                {
                    var superOutput = Path.Combine(imageDirectory, plan.Super.Zstd ? "super.img" : SuperFileName(plan.Super));
                    await DeliverySuperBuilder.BuildAsync(plan.Container, plan.Super with { Empty = false, Sparse = plan.Super.Sparse && !plan.OrganizeDirectory }, superOutput,
                        null, token,
                        new InlineProgress<DeliveryProgress>(p => progress?.Report(p with { Percent = p.Percent * .40 }))).ConfigureAwait(false);
                    if (plan.Super.Zstd)
                    {
                        await Task.Run(() =>
                        {
                            using var input = File.OpenRead(superOutput);
                            using var destination = new FileStream(Path.Combine(imageDirectory, SuperFileName(plan.Super)), FileMode.CreateNew, FileAccess.Write);
                            using var tracked = new DeliveryWriteProgressStream(destination,
                                _ => progress?.Report(new(45 + input.Position * 10.0 / Math.Max(1, input.Length), "Compressing Super",
                                    (input.Position, input.Length), "Delivery_ProgressCompress")), token);
                            ZstdCompressor.Compress(input, tracked, new CompressionOptions
                            {
                                Level = DeliverySuperOptions.ResolveZstdLevel(plan.Super)
                            });
                        }, token).ConfigureAwait(false);
                        File.Delete(superOutput);
                    }
                }
            }
            if (plan.OrganizeDirectory)
            {
                var copy = plan.Files.Where(f => !f.Generated).ToArray();
                var copyLengths = copy.Select(f =>
                {
                    if (!plan.PackSuper) return new FileInfo(f.SourcePath).Length;
                    using var image = MetadataReader.OpenImage(f.SourcePath);
                    return image.Length;
                }).ToArray();
                var copyTotal = copyLengths.Sum();
                long copied = 0;
                if (copy.Length > 0) progress?.Report(new(55, "Copying images", (0, copyTotal), "Delivery_ProgressCopy"));
                for (var i = 0; i < copy.Length; i++)
                {
                    DeliveryValidation.CheckSource(copy[i], DeliveryPackageBuilder.IsDatArtifact(copy[i].FileName)
                        && copy[i].FileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));
                    var index = i;
                    var completed = copied;
                    var destination = OrganizedImagePath(plan, stage, copy[i]);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await CopyFileAsync(copy[i].SourcePath, destination, token,
                        p => progress?.Report(new(55 + (index + p / 100) * 20 / Math.Max(1, copy.Length), "Copying images",
                            (completed + (long)(copyLengths[index] * p / 100), copyTotal), "Delivery_ProgressCopy", Detail: copy[index].FileName)),
                        expandSparse: plan.PackSuper).ConfigureAwait(false);
                    copied += copyLengths[i];
                }
                await tools.PrepareAsync(plan, stage, progress, token).ConfigureAwait(false);
                foreach (var (name, contents) in plan.Scripts)
                {
                    if (!DeliveryValidation.IsScriptName(name))
                        throw new InvalidDataException("Invalid script filename: " + name);
                    if (Encoding.UTF8.GetByteCount(contents) > 1024 * 1024) throw new InvalidDataException("Script exceeds 1 MiB.");
                    if (name == "updater-script" || name == "update-binary") continue;
                    var path = Path.Combine(stage, name);
                    await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false), token).ConfigureAwait(false);
                    if (!OperatingSystem.IsWindows() && name.EndsWith(".sh"))
                        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                var payloadNotice = plan.Payload.SigningKeyPath.Length == 0 ? "Unsigned full payload. " : "RSA SHA-256 signed full payload. ";
                var readme = (plan.PackPayload ? payloadNotice + "ota.zip is an A/B OTA package, not a Fastboot package. Unzipping payload.bin does not make it flashable with fastboot. Recovery must support A/B OTA and trust the Payload signing key; disabling ZIP verification does not disable Payload verification. Missing device/build metadata may require target-specific OTA integration. Standalone images are separate from this OTA.\n\n" : "") + "UotanStudio delivery\n\n" + plan.Information + "\n\nImages: " + (plan.PackSuper ? "./" : "images/") + "\nTools: bin/\n"
                    + "Linux/macOS: chmod +x Flash*.sh bin/Linux/* bin/macOS/* before running when ZIP extraction loses executable permissions.\n"
                    + "TXT: select the intended complete variant. The legacy toolbox does not enforce alternative groups or device checks.\n"
                    + "Legacy TXT paths containing spaces may not work in the old toolbox.\n"
                    + "Scripts target the partition names and slot policy selected by the author.\n";
                await File.WriteAllTextAsync(Path.Combine(stage, "README.txt"), readme, token).ConfigureAwait(false);
            }
            await DeliveryPackageBuilder.BuildAsync(plan, stage, imageDirectory, token, progress, signingKeys).ConfigureAwait(false);
            if (plan.PackPayload && plan.OrganizeDirectory)
            {
                File.Delete(Path.Combine(imageDirectory, "payload.bin"));
                File.Delete(Path.Combine(imageDirectory, "payload_properties.txt"));
            }
            if (plan.PackPayload && !plan.OrganizeDirectory)
                File.Delete(Path.Combine(imageDirectory, "payload_properties.txt"));
            var stagedFiles = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var hashes = new StringBuilder();
            long totalBytes = 0;
            foreach (var file in stagedFiles) totalBytes = checked(totalBytes + new FileInfo(file).Length);
            long processedBytes = 0;
            progress?.Report(new(90, "Verifying output", Stage: "Delivery_ProgressVerify", Bytes: (0, totalBytes)));
            for (var i = 0; i < stagedFiles.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                await using var input = new FileStream(stagedFiles[i], FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var fileLength = input.Length;
                var hashAlgorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
                {
                    hashAlgorithm.AppendData(buffer, 0, read);
                    processedBytes = checked(processedBytes + read);
                    progress?.Report(new(90 + processedBytes * 9.0 / Math.Max(1, totalBytes), "Verifying output",
                        Stage: "Delivery_ProgressVerify", Bytes: (processedBytes, totalBytes), Detail: Path.GetFileName(stagedFiles[i])));
                }
                var hash = hashAlgorithm.GetHashAndReset();
                hashAlgorithm.Dispose();
                if (input.Length != fileLength) throw new IOException("Output changed while verifying.");
                hashes.Append(Convert.ToHexString(hash).ToLowerInvariant()).Append("  ")
                    .AppendLine(Path.GetRelativePath(stage, stagedFiles[i]).Replace('\\', '/'));
            }
            if (plan.OrganizeDirectory)
                await File.WriteAllTextAsync(Path.Combine(stage, "checksums.sha256"), hashes.ToString(), token).ConfigureAwait(false);
            if (plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty)
            {
                var archive = Path.Combine(stage, "super.zip");
                await CreateSuperArchiveAsync(stage, archive, token).ConfigureAwait(false);
                if (plan.Package.SigningKeyPath.Length > 0)
                    await DeliveryPackageBuilder.SignAsync(archive, plan.Package, token).ConfigureAwait(false);
                foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
                    .Where(path => !path.Equals(archive, StringComparison.OrdinalIgnoreCase)).ToArray())
                    File.Delete(file);
                foreach (var directory in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length).ToArray())
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            var result = Publish(stage, output, token, overwrite);
            progress?.Report(new(100, "Output complete", Stage: "Delivery_ProgressDone"));
            return result;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    /// <summary>
    /// <para>返回组织布局下生成的容器文件名（ota.zip / super.zip / super_empty.img / super.img / super.zst / payload.bin）。</para>
    /// Returns the generated container filename in the organized layout (ota.zip / super.zip / super_empty.img / super.img / super.zst / payload.bin).
    /// </summary>
    internal static string ContainerFileName(DeliveryPlan plan) => plan.PackPayload
        ? (plan.OrganizeDirectory ? ZipFileName(plan, "ota.zip") : "payload.bin")
        : plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty
            ? ZipFileName(plan, "super.zip")
            : plan.Super.Empty ? "super_empty.img" : SuperFileName(plan.Super);

    /// <summary><para>打包成 ZIP 时使用用户指定的压缩包名称（补 .zip），空则回退默认名。</para>
    /// Uses the user-specified archive name (appending .zip) when packing into a ZIP; falls back to the default.</summary>
    internal static string ZipFileName(DeliveryPlan plan, string fallback) =>
        plan.OrganizeDirectory && plan.ArchiveName.Trim().Length > 0 ? plan.ArchiveName.Trim() + ".zip" : fallback;

    /// <summary><para>返回 Super 镜像文件名：压缩时为 super.zst，否则为 super.img。</para>
    /// Returns the Super image filename: super.zst when compressed, otherwise super.img.</summary>
    internal static string SuperFileName(DeliverySuperOptions options) => options.Zstd ? "super.zst" : "super.img";

    private static async Task CreateSuperArchiveAsync(string stage, string archivePath, CancellationToken token)
    {
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var source in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(archivePath, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetRelativePath(stage, source).Replace('\\', '/');
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2009, 1, 1, 0, 0, 0, TimeSpan.Zero);
            if (name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                || name.Equals("META-INF/com/google/android/update-binary", StringComparison.Ordinal))
                entry.ExternalAttributes = 0x81ED << 16;
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = entry.Open();
            await input.CopyToAsync(destination, 1024 * 1024, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>流式复制源文件到目标并报告进度；expandSparse 时把稀疏镜像展开（Recovery 按物理块写入桌面暂存目录）。</para>
    /// Streams a source file to a destination with progress; when expandSparse is set, sparse images are expanded in the staging directory.
    /// </summary>
    internal static async Task CopyFileAsync(string source, string destination, CancellationToken token, Action<double>? progress = null,
        bool expandSparse = false)
    {
        await using var input = expandSparse ? MetadataReader.OpenImage(source)
            : new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        var length = input.Length;
        var buffer = new byte[65536];
        long copied = 0, last = 0;
        while (copied < length)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Source truncated while copying.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            copied += count;
            if (Environment.TickCount64 - last >= 100) { progress?.Invoke(copied * 100.0 / Math.Max(1, length)); last = Environment.TickCount64; }
        }
        if (input.Length != length) throw new IOException("Source changed while copying.");
        progress?.Invoke(100);
    }

    /// <summary><para>计算本次输出会写到的全部目标文件路径（完整绝对路径，含组织目录布局、脚本与容器）。</para>
    /// Computes every destination file path this export would write: full absolute paths including the organized layout, scripts, and containers.</summary>
    internal static IEnumerable<string> PlannedOutputPaths(DeliveryPlan plan)
    {
        plan = plan.ForOutput();
        var output = Path.GetFullPath(plan.OutputDirectory);
        if (plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty)
            return [Path.Combine(output, ZipFileName(plan, "super.zip"))];
        var plannedNames = plan.OrganizeDirectory
            ? plan.Files.Select(f => f.Generated && plan.PackPayload ? ZipFileName(plan, "ota.zip") : plan.PackSuper ? OrganizedImageRelativePath(f) : Path.Combine("images", f.FileName)).Concat(plan.Scripts.Keys.Where(name => name is not ("updater-script" or "update-binary"))).Concat(["README.txt", "checksums.sha256"])
            : [ContainerFileName(plan)];
        if (plan.PackPayload && plan.OrganizeDirectory) plannedNames = plannedNames.Append(ZipFileName(plan, "ota.zip"));
        if (plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty)
            plannedNames = plannedNames.Concat(["META-INF/com/google/android/update-binary", "META-INF/com/google/android/updater-script"]);
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper) plannedNames = plannedNames.Append(ZipFileName(plan, "recovery.zip"));
        return plannedNames.Select(name => Path.Combine(output, name));
    }

    /// <summary><para>提前扫描输出目录，返回本次输出会覆盖的已存在目标文件（排序去重）。</para>
    /// Scans the output directory in advance and returns planned destination files that already exist, sorted and deduplicated.</summary>
    internal static string[] FindExistingOutputs(DeliveryPlan plan)
        => PlannedOutputPaths(plan).Where(File.Exists).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string[] Publish(string stage, string output, CancellationToken token, IReadOnlySet<string>? overwrite = null)
    {
        var files = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Select(p => (Source: p, Destination: Path.Combine(output, Path.GetRelativePath(stage, p)))).ToArray();
        var moved = new List<(string Source, string Destination)>();
        var created = new List<string>();
        var backups = new List<(string Backup, string Destination)>();
        var skipped = new List<string>();
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var parent = Path.GetDirectoryName(file.Destination)!;
                var missing = new Stack<string>();
                for (var p = parent; !Directory.Exists(p); p = Path.GetDirectoryName(p)!) missing.Push(p);
                while (missing.TryPop(out var p)) { Directory.CreateDirectory(p); created.Add(p); }
                CheckDirectory(parent);
                if (Path.Exists(file.Destination))
                {
                    if (Directory.Exists(file.Destination)) throw new IOException("输出位置被目录占用 / A directory blocks the output: " + file.Destination);
                    if (overwrite is null || !overwrite.Contains(file.Destination))
                    {
                        skipped.Add(file.Destination);
                        continue;
                    }
                    var backup = Path.Combine(stage, ".uotan-original-" + Guid.NewGuid().ToString("N"));
                    File.Move(file.Destination, backup, false);
                    backups.Add((backup, file.Destination));
                }
                File.Move(file.Source, file.Destination, false);
                moved.Add(file);
            }
            foreach (var backup in backups) File.Delete(backup.Backup);
            return moved.Select(f => f.Destination).ToArray();
        }
        catch
        {
            foreach (var file in moved.AsEnumerable().Reverse()) File.Move(file.Destination, file.Source, false);
            foreach (var backup in backups.AsEnumerable().Reverse()) if (File.Exists(backup.Backup)) File.Move(backup.Backup, backup.Destination, false);
            foreach (var p in created.AsEnumerable().Reverse()) if (!Directory.EnumerateFileSystemEntries(p).Any()) Directory.Delete(p);
            throw;
        }
    }

    /// <summary>
    /// <para>沿目录向上逐级检查：任一祖先被文件占用或为符号链接则抛错，保证输出目录可安全创建。</para>
    /// Walks up the directory ancestors, throwing if any is blocked by a file or is a symlink, so the output directory can be created safely.
    /// </summary>
    internal static void CheckDirectory(string path)
    {
        for (var p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
        {
            if (File.Exists(p)) throw new IOException("A file blocks the output directory: " + p);
            if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked output directories are not supported: " + p);
        }
    }

    private static bool IsUnder(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary><para>返回路径所在目录；已是目录则原样返回。空/根路径回退自身。</para>
    /// Returns the containing directory of a path, or the path itself when it is already a directory.</summary>
    private static string GetDirectoryNameOrRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var full = Path.GetFullPath(path);
        return Directory.Exists(full)
            ? Path.TrimEndingDirectorySeparator(full)
            : Path.GetDirectoryName(full) is { Length: > 0 } parent ? Path.TrimEndingDirectorySeparator(parent) : full;
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}