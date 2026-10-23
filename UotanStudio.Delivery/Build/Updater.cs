using System.Buffers.Binary;
using System.Text;
using UotanStudio.Delivery.Build;

namespace UotanStudio.Delivery;

/// <summary><para>复用原 ROM 的原生 Edify 解释器，而不是假定 shell 的 block_image_update 命令。</para>
/// Reuses the original ROM's native Edify interpreter instead of assuming a shell block_image_update command.</summary>
internal static class DeliveryRecoveryUpdater
{
    internal const string ScriptPath = "META-INF/com/google/android/updater-script";
    /// <summary>Standard member path of the native updater inside a traditional Recovery package. <para>传统 Recovery 包内原生更新器的标准成员路径。</para></summary>
    internal const string EntryPath = "META-INF/com/google/android/update-binary";

    /// <summary>
    /// Resolves the native Edify updater for DAT flashing: an explicit path first, then META-INF/com/google/android/update-binary
    /// discoverable alongside the DAT sources; multiple candidates fall back to the embedded arm64 updater, and none throws.
    /// <para>解析 DAT 卡刷要使用的原生 Edify 更新器：优先显式指定的路径，其次沿 DAT 来源目录寻找原包
    /// META-INF/com/google/android/update-binary；多个候选时回退内嵌的 arm64 更新器，找不到则报错。</para>
    /// </summary>
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
            > 1 => Embedded(),
            _ => throw new InvalidDataException("DAT 卡刷需要原包的原生 update-binary。请保留 DAT 目录（或其上一级）的 META-INF/com/google/android/update-binary，或明确指定 RecoveryUpdaterPath。"),
        };
    }

    /// <summary><para>解开随程序内嵌的 arm64 原生更新器到工具缓存；返回磁盘路径。</para>
    /// Extracts the embedded arm64 native updater into the tool cache; returns the disk path.</summary>
    internal static string Embedded() => Validate(UpdaterAssets.Extract());

    /// <summary>
    /// Validates that the updater is a native ELF executable exposing the Edify functions needed for flashing; returns the absolute path on success.
    /// <para>校验给定的更新器为本机 ELF 可执行文件，且导出卡刷所需的 Edify 函数（block_image_update 等）；通过则返回绝对路径。</para>
    /// </summary>
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
        foreach (var required in (ReadOnlySpan<string>)["block_image_update", "block_image_verify", "package_extract_file", "file_getprop", "updater-script"])
            if (bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(required)) < 0)
                throw new InvalidDataException("The Recovery updater does not expose the required Edify functions: " + required);
        return file.SourcePath;
    }
}