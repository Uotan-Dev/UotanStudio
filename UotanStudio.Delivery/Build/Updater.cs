using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Services.Delivery;

/// <summary>Reuses the original ROM's native Edify interpreter instead of assuming a shell block_image_update command.</summary>
internal static class DeliveryRecoveryUpdater
{
    internal const string ArchivePath = "META-INF/com/google/android/uotan-updater";
    internal const string ScriptPath = "META-INF/com/google/android/updater-script";

    internal static string Resolve(DeliveryPlan plan)
    {
        if (plan.Package.RecoveryUpdaterPath.Length > 0) return Validate(plan.Package.RecoveryUpdaterPath);
        var roots = plan.Files.Where(f => DeliveryPackageBuilder.IsDatArtifact(f.FileName) && f.SourcePath.Length > 0)
            .Select(f => Path.GetDirectoryName(Path.GetFullPath(f.SourcePath))!)
            .SelectMany(root => new[] { root, Path.GetDirectoryName(root) })
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = roots.Select(root => Path.Combine(root, "META-INF", "com", "google", "android", "update-binary"))
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException("DAT 卡刷需要原包的原生 update-binary。请保留 DAT 目录（或其上一级）的 META-INF/com/google/android/update-binary；多个原包请明确指定 RecoveryUpdaterPath。");
        return Validate(candidates[0]);
    }

    private static string Validate(string path)
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