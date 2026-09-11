using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.CrossCutting.Compress.Compressors;

namespace UotanStudio.Services.Delivery;

internal sealed record DeliveryProgress(double Percent, string Message);

internal interface IDeliveryTools
{
    Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token);
}

/// <summary>Prepares every file privately, then publishes without overwriting existing files.</summary>
internal sealed class DeliveryOutputService(IDeliveryTools tools)
{
    internal async Task<string[]> ExportAsync(DeliveryPlan plan, IProgress<DeliveryProgress>? progress = null, CancellationToken token = default)
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
        var plannedNames = plan.OrganizeDirectory
            ? plan.Files.Select(f => Path.Combine("images", f.FileName)).Concat(plan.Scripts.Keys).Concat(["README.txt", "checksums.sha256"])
            : [ContainerFileName(plan)];
        if (plan.PackPayload) plannedNames = plannedNames.Append(plan.OrganizeDirectory ? "images/payload_properties.txt" : "payload_properties.txt");
        if (plan.Package.Ota) plannedNames = plannedNames.Append("ota.zip");
        if (plan.Recovery) plannedNames = plannedNames.Append("recovery.zip");
        foreach (var name in plannedNames)
        {
            var destination = Path.Combine(output, name);
            CheckDirectory(Path.GetDirectoryName(destination)!);
            if (Path.Exists(destination)) throw new IOException("目标文件已存在 / Output already exists: " + destination);
        }
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".uotan-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var imageDirectory = plan.OrganizeDirectory ? Path.Combine(stage, "images") : stage;
        Directory.CreateDirectory(imageDirectory);
        try
        {
            progress?.Report(new(0, "检查输入 / Checking inputs"));
            if (plan.PackPayload) await DeliveryPayloadBuilder.BuildAsync(plan, Path.Combine(imageDirectory, "payload.bin"), progress, token).ConfigureAwait(false);
            if (plan.PackSuper)
            {
                var imageName = SuperFileName(plan.Super);
                var rawName = plan.Super.Empty ? "super_empty.img" : "super.img";
                var superOutput = Path.Combine(imageDirectory, rawName);
                await DeliverySuperBuilder.BuildAsync(plan.Container, plan.Super, superOutput,
                    new InlineProgress<double>(p => progress?.Report(new(p * .45, "构建 Super / Building Super"))), token).ConfigureAwait(false);
                if (plan.Super.Zstd)
                {
                    await Task.Run(() =>
                    {
                        using var input = File.OpenRead(superOutput);
                        using var destination = new FileStream(Path.Combine(imageDirectory, imageName), FileMode.CreateNew, FileAccess.Write);
                        using var tracked = new DeliveryWriteProgressStream(destination,
                            _ => progress?.Report(new(45 + input.Position * 10.0 / Math.Max(1, input.Length), "压缩 Super / Compressing Super")), token);
                        ZstdCompressor.Compress(input, tracked);
                    }, token).ConfigureAwait(false);
                    File.Delete(superOutput);
                }
            }
            if (plan.OrganizeDirectory)
            {
                var copy = plan.Files.Where(f => !f.Generated).ToArray();
                for (var i = 0; i < copy.Length; i++)
                {
                    DeliveryValidation.CheckSource(copy[i]);
                    var index = i;
                    await CopyFileAsync(copy[i].SourcePath, Path.Combine(imageDirectory, copy[i].FileName), token,
                        p => progress?.Report(new(55 + (index + p / 100) * 20 / Math.Max(1, copy.Length), "复制 / Copying " + copy[index].FileName))).ConfigureAwait(false);
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
                var readme = (plan.PackPayload ? payloadNotice + "Flash scripts cover only standalone images, never payload.bin. Device OTA acceptance requires a matching trusted public key and OTA integration.\n\n" : "") + "UotanStudio delivery\n\n" + plan.Information + "\n\nImages: images/\nTools: bin/\n"
                    + "Linux/macOS: chmod +x Flash*.sh bin/Linux/* bin/macOS/* before running when ZIP extraction loses executable permissions.\n"
                    + "TXT: select the intended complete variant. The legacy toolbox does not enforce alternative groups or device checks.\n"
                    + "Legacy TXT paths containing spaces may not work in the old toolbox.\n"
                    + "Scripts target the partition names and slot policy selected by the author.\n";
                await File.WriteAllTextAsync(Path.Combine(stage, "README.txt"), readme, token).ConfigureAwait(false);
            }
            await DeliveryPackageBuilder.BuildAsync(plan, stage, imageDirectory, token).ConfigureAwait(false);
            var stagedFiles = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var hashes = new StringBuilder();
            for (var i = 0; i < stagedFiles.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                await using var input = File.OpenRead(stagedFiles[i]);
                var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
                hashes.Append(Convert.ToHexString(hash).ToLowerInvariant()).Append("  ")
                    .AppendLine(Path.GetRelativePath(stage, stagedFiles[i]).Replace('\\', '/'));
                progress?.Report(new(90 + (i + 1) * 9.0 / Math.Max(1, stagedFiles.Length), "校验输出 / Verifying output"));
            }
            if (plan.OrganizeDirectory)
                await File.WriteAllTextAsync(Path.Combine(stage, "checksums.sha256"), hashes.ToString(), token).ConfigureAwait(false);
            var result = Publish(stage, output, token);
            progress?.Report(new(100, "输出完成 / Output complete"));
            return result;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    internal static string ContainerFileName(DeliveryPlan plan) => plan.PackPayload ? "payload.bin" : SuperFileName(plan.Super);

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

    private static string[] Publish(string stage, string output, CancellationToken token)
    {
        var files = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Select(p => (Source: p, Destination: Path.Combine(output, Path.GetRelativePath(stage, p)))).ToArray();
        foreach (var file in files)
        {
            CheckDirectory(Path.GetDirectoryName(file.Destination)!);
            if (Path.Exists(file.Destination)) throw new IOException("目标文件已存在 / Output already exists: " + file.Destination);
        }
        var moved = new List<(string Source, string Destination)>();
        var created = new List<string>();
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
                File.Move(file.Source, file.Destination, false);
                moved.Add(file);
            }
            return files.Select(f => f.Destination).ToArray();
        }
        catch
        {
            foreach (var file in moved.AsEnumerable().Reverse()) File.Move(file.Destination, file.Source, false);
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