using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.CrossCutting.Compress.Compressors;

namespace UotanStudio.Services.Delivery;

internal sealed record DeliveryProgress(double Percent, string Message,
    (long Processed, long Total)? Bytes = null, string? Stage = null,
    (long Processed, long Total)? Items = null, string? Detail = null);

internal interface IDeliveryTools
{
    Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token);
}

/// <summary>Prepares every file privately, then publishes: overwrites caller-approved destinations and keeps the rest.</summary>
internal sealed class DeliveryOutputService(IDeliveryTools tools)
{
    internal async Task<string[]> ExportAsync(DeliveryPlan plan, IProgress<DeliveryProgress>? progress = null, CancellationToken token = default,
        IReadOnlySet<string>? overwrite = null)
    {
        DeliveryValidation.Validate(plan);
        if (string.IsNullOrWhiteSpace(plan.OutputDirectory)) throw new InvalidDataException("Select an output directory.");
        var output = Path.GetFullPath(plan.OutputDirectory);
        CheckDirectory(output);
        foreach (var file in plan.Files.Concat(plan.Container).Where(f => f.SourcePath.Length > 0))
            if (IsUnder(file.SourcePath, output)) throw new InvalidDataException("输出目录不能包含源镜像 / Output must not contain source images.");
        foreach (var source in new[] { plan.Payload.DynamicPartitionsFile, plan.Payload.SignatureFile, plan.Payload.SigningKeyPath, plan.Payload.PostinstallFile, plan.Package.SigningKeyPath, plan.Package.CertificatePath }.Where(p => p.Length > 0))
            if (IsUnder(source, output)) throw new InvalidDataException("Output must not contain Payload configuration inputs.");
        if (plan.OrganizeDirectory && (plan.Windows || plan.Linux || plan.MacOS || plan.Txt || plan.Recovery && plan.Scripts.ContainsKey("Recovery.sh"))
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
        var imageDirectory = plan.OrganizeDirectory ? Path.Combine(stage, "images") : stage;
        Directory.CreateDirectory(imageDirectory);
        try
        {
            progress?.Report(new(0, "Checking inputs", Stage: "Delivery_ProgressChecking"));
            if (plan.PackPayload) await DeliveryPayloadBuilder.BuildAsync(plan, Path.Combine(imageDirectory, "payload.bin"), progress, token).ConfigureAwait(false);
            if (plan.PackSuper)
            {
                var imageName = SuperFileName(plan.Super);
                var rawName = plan.Super.Empty ? "super_empty.img" : "super.img";
                var superOutput = Path.Combine(imageDirectory, rawName);
                await DeliverySuperBuilder.BuildAsync(plan.Container, plan.Super, superOutput,
                    null, token,
                    new InlineProgress<DeliveryProgress>(p => progress?.Report(p with { Percent = p.Percent * .45 }))).ConfigureAwait(false);
                if (plan.Super.Zstd)
                {
                    await Task.Run(() =>
                    {
                        using var input = File.OpenRead(superOutput);
                        using var destination = new FileStream(Path.Combine(imageDirectory, imageName), FileMode.CreateNew, FileAccess.Write);
                        using var tracked = new DeliveryWriteProgressStream(destination,
                            _ => progress?.Report(new(45 + input.Position * 10.0 / Math.Max(1, input.Length), "Compressing Super",
                                (input.Position, input.Length), "Delivery_ProgressCompress")), token);
                        ZstdCompressor.Compress(input, tracked);
                    }, token).ConfigureAwait(false);
                    File.Delete(superOutput);
                }
            }
            if (plan.OrganizeDirectory)
            {
                var copy = plan.Files.Where(f => !f.Generated).ToArray();
                var copyLengths = copy.Select(f => new FileInfo(f.SourcePath).Length).ToArray();
                var copyTotal = copyLengths.Sum();
                long copied = 0;
                if (copy.Length > 0) progress?.Report(new(55, "Copying images", (0, copyTotal), "Delivery_ProgressCopy"));
                for (var i = 0; i < copy.Length; i++)
                {
                    DeliveryValidation.CheckSource(copy[i]);
                    var index = i;
                    var completed = copied;
                    await CopyFileAsync(copy[i].SourcePath, Path.Combine(imageDirectory, copy[i].FileName), token,
                        p => progress?.Report(new(55 + (index + p / 100) * 20 / Math.Max(1, copy.Length), "Copying images",
                            (completed + (long)(copyLengths[index] * p / 100), copyTotal), "Delivery_ProgressCopy", Detail: copy[index].FileName))).ConfigureAwait(false);
                    copied += copyLengths[i];
                }
                await tools.PrepareAsync(plan, stage, progress, token).ConfigureAwait(false);
                foreach (var (name, contents) in plan.Scripts)
                {
                    if (!DeliveryValidation.IsName(name) || !(name.EndsWith(".sh") || name.EndsWith(".bat") || name.EndsWith(".txt")))
                        throw new InvalidDataException("Invalid script filename: " + name);
                    if (Encoding.UTF8.GetByteCount(contents) > 1024 * 1024) throw new InvalidDataException("Script exceeds 1 MiB.");
                    var path = Path.Combine(stage, name);
                    await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false), token).ConfigureAwait(false);
                    if (!OperatingSystem.IsWindows() && name.EndsWith(".sh"))
                        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                var payloadNotice = plan.Payload.SigningKeyPath.Length == 0 ? "Unsigned full payload. " : "RSA SHA-256 signed full payload. ";
                var readme = (plan.PackPayload ? payloadNotice + "ota.zip is an A/B OTA package, not a Fastboot package. Unzipping payload.bin does not make it flashable with fastboot. Recovery must support A/B OTA and trust the Payload signing key; disabling ZIP verification does not disable Payload verification. Missing device/build metadata may require target-specific OTA integration. Standalone images are separate from this OTA.\n\n" : "") + "UotanStudio delivery\n\n" + plan.Information + "\n\nImages: images/\nTools: bin/\n"
                    + "Linux/macOS: chmod +x Flash*.sh bin/Linux/* bin/macOS/* before running when ZIP extraction loses executable permissions.\n"
                    + "TXT: select the intended complete variant. The legacy toolbox does not enforce alternative groups or device checks.\n"
                    + "Legacy TXT paths containing spaces may not work in the old toolbox.\n"
                    + "Scripts target the partition names and slot policy selected by the author.\n";
                await File.WriteAllTextAsync(Path.Combine(stage, "README.txt"), readme, token).ConfigureAwait(false);
            }
            await DeliveryPackageBuilder.BuildAsync(plan, stage, imageDirectory, token, progress).ConfigureAwait(false);
            if (plan.PackPayload)
            {
                File.Delete(Path.Combine(imageDirectory, "payload.bin"));
                File.Delete(Path.Combine(imageDirectory, "payload_properties.txt"));
            }
            var stagedFiles = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var hashes = new StringBuilder();
            progress?.Report(new(90, "Verifying output", Stage: "Delivery_ProgressVerify", Items: (0, stagedFiles.Length)));
            for (var i = 0; i < stagedFiles.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                await using var input = File.OpenRead(stagedFiles[i]);
                var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
                hashes.Append(Convert.ToHexString(hash).ToLowerInvariant()).Append("  ")
                    .AppendLine(Path.GetRelativePath(stage, stagedFiles[i]).Replace('\\', '/'));
                progress?.Report(new(90 + (i + 1) * 9.0 / Math.Max(1, stagedFiles.Length), "Verifying output",
                    Stage: "Delivery_ProgressVerify", Items: (i + 1, stagedFiles.Length)));
            }
            if (plan.OrganizeDirectory)
                await File.WriteAllTextAsync(Path.Combine(stage, "checksums.sha256"), hashes.ToString(), token).ConfigureAwait(false);
            var result = Publish(stage, output, token, overwrite);
            progress?.Report(new(100, "Output complete", Stage: "Delivery_ProgressDone"));
            return result;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    internal static string ContainerFileName(DeliveryPlan plan) => plan.PackPayload ? "ota.zip" : SuperFileName(plan.Super);

    internal static string SuperFileName(DeliverySuperOptions options) => options.Empty ? "super_empty.img" : options.Zstd ? "super.zst" : "super.img";

    internal static async Task CopyFileAsync(string source, string destination, CancellationToken token, Action<double>? progress = null)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
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
        var output = Path.GetFullPath(plan.OutputDirectory);
        var plannedNames = plan.OrganizeDirectory
            ? plan.Files.Select(f => f.Generated && plan.PackPayload ? "ota.zip" : Path.Combine("images", f.FileName)).Concat(plan.Scripts.Keys).Concat(["README.txt", "checksums.sha256"])
            : [ContainerFileName(plan)];
        if (plan.PackPayload) plannedNames = plannedNames.Append("ota.zip");
        if (plan.Recovery && !plan.PackPayload) plannedNames = plannedNames.Append("recovery.zip");
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
                        // 未批准覆盖：保留原文件，丢弃暂存副本。
                        // Not approved for overwrite: keep the existing file and discard the staged copy.
                        skipped.Add(file.Destination);
                        continue;
                    }
                    // 先把原文件移入暂存区作备份，全部发布成功后再删除；失败回滚时恢复原文件。
                    // Set the original aside in the stage as a backup; delete after a successful publish, restore on rollback.
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
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
