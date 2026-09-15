using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Services.Delivery;

/// <summary>Reuses the original ROM's native Edify interpreter instead of assuming a shell block_image_update command.</summary>
internal static class DeliveryRecoveryUpdater
{
    internal const string ArchivePath = "META-INF/com/google/android/uotan-updater";
    internal const string ScriptPath = "META-INF/com/google/android/updater-script";
    /// <summary><para>A/B OTA 包内原生更新器的标准成员路径。</para>Standard member path of the native updater inside an A/B OTA package.</summary>
    internal const string EntryPath = "META-INF/com/google/android/update-binary";

    internal static string Resolve(DeliveryPlan plan)
    {
        if (plan.Package.RecoveryUpdaterPath.Length > 0) return Validate(plan.Package.RecoveryUpdaterPath);
        var roots = plan.Files.Where(f => DeliveryPackageBuilder.IsDatArtifact(f.FileName) && f.SourcePath.Length > 0)
            .Select(f => Path.GetDirectoryName(Path.GetFullPath(f.SourcePath))!)
            .SelectMany(root => new[] { root, Path.GetDirectoryName(root) })
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = roots.Select(root => Path.Combine(root, "META-INF", "com", "google", "android", "update-binary"))
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return candidates.Length switch
        {
            1 => Validate(candidates[0]),
            // 多个原包无法自动挑选时回退到内嵌的 arm64 更新器；arm32 设备需明确指定 RecoveryUpdaterPath。
            > 1 => Embedded(),
            _ => throw new InvalidDataException("DAT 卡刷需要原包的原生 update-binary。请保留 DAT 目录（或其上一级）的 META-INF/com/google/android/update-binary，或明确指定 RecoveryUpdaterPath。"),
        };
    }

    /// <summary><para>解开随程序内嵌的 arm64 原生更新器到工具缓存；返回磁盘路径。</para>
    /// Extracts the embedded arm64 native updater into the tool cache; returns the disk path.</summary>
    internal static string Embedded()
    {
        var path = MaterializeAsset("update-binary-arm64", "delivery-updater");
        return Validate(path);
    }

    /// <summary><para>解开内嵌的 A/B OTA 原生更新器（ginkgo 上游构建）；用于 ota.zip 的执行入口。</para>
    /// Extracts the embedded A/B OTA native updater (ginkgo upstream build) used as ota.zip's entry point.</summary>
    internal static string EmbeddedOtaUpdater() => MaterializeAsset("update-binary-ab-arm64", "delivery-ota-updater");

    private static string MaterializeAsset(string name, string cacheDirectory)
    {
        var path = Path.Combine(Path.GetTempPath(), "UotanStudio", cacheDirectory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        if (!File.Exists(source)) throw new FileNotFoundException("Missing embedded Recovery updater asset.", source);
        if (!File.Exists(path) || new FileInfo(path).Length != new FileInfo(source).Length)
            File.Copy(source, path, true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    internal static string Validate(string path)
    {
        var file = new DeliveryFile { SourcePath = Path.GetFullPath(path) };
        DeliveryValidation.CheckSource(file);
        using var input = File.OpenRead(file.SourcePath);
        if (input.Length is < 52 or > 16 * 1024 * 1024)
            throw new InvalidDataException("Recovery updater must be a native ELF executable below 16 MiB.");
        Span<byte> header = stackalloc byte[20];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("\u007fELF"u8) || header[4] is not (1 or 2) || header[5] != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) is not (2 or 3)
            || BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) is not (3 or 40 or 62 or 183 or 243))
            throw new InvalidDataException("DAT requires the original ROM's native Edify updater, not a shell installer.");
        var bytes = File.ReadAllBytes(file.SourcePath);
        foreach (var required in new[] { "block_image_update", "block_image_verify", "package_extract_file", "file_getprop", "updater-script" })
            if (bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(required)) < 0)
                throw new InvalidDataException("The Recovery updater does not expose the required Edify functions: " + required);
        return file.SourcePath;
    }
}