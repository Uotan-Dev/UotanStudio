using UotanStudio.Firmware.BoundedContexts.Archive.Editing;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.BoundedContexts.Compression;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Workflow;

/// <summary>Merges a recovery ramdisk into an Android boot image with explicit source precedence.
/// <para>以显式的来源优先级将 recovery ramdisk 合并进 Android boot 镜像。</para></summary>
public sealed class RecoveryRamdiskMergeService
{
    /// <summary>
    /// Merges the recovery ramdisk into the source boot image and writes the repacked image to the output path.
    /// <para>将 recovery ramdisk 合并进源 boot 镜像，并把重新打包的镜像写出到输出路径。</para>
    /// </summary>
    /// <param name="sourceBootPath">Boot image providing the base ramdisk; its entries override recovery
    /// entries of the same name. <para>提供基础 ramdisk 的 boot 镜像；其中同名条目会覆盖 recovery 条目。</para></param>
    /// <param name="recoveryPath">Boot image whose ramdisk supplies the merged baseline. <para>其 ramdisk 作为合并底座的 boot 镜像。</para></param>
    /// <param name="outputPath">Destination file; must differ from <paramref name="sourceBootPath"/>
    /// (case-insensitive full-path comparison) and an existing file is replaced.
    /// <para>目标文件；必须与 <paramref name="sourceBootPath"/> 不同（按完整路径不区分大小写比较），已存在的文件会被替换。</para></param>
    /// <param name="cancellationToken">Checked after parsing and during the CPIO merge and write;
    /// cancellation is rethrown. <para>在解析后以及 CPIO 合并与写出期间检查；取消会重新抛出。</para></param>
    /// <returns>
    /// The merge result with uncompressed sizes and the inherited compression. Both ramdisks are
    /// decompressed, the recovery CPIO archive is rebuilt with every source entry added or replacing an
    /// equal name, and the merged archive is re-compressed with the source header's ramdisk compression.
    /// <para>合并结果，包含未压缩大小与继承的压缩方式。两个 ramdisk 均先解压，随后在 recovery 的 CPIO
    /// 归档上以同名添加或替换的方式合入全部源条目，合并后的归档按源头部的 ramdisk 压缩方式重新压缩。</para>
    /// </returns>
    /// <exception cref="ArgumentException">Any path is null, empty, or whitespace. <para>任一路径为 null、空白或仅含空白字符。</para></exception>
    /// <exception cref="InvalidOperationException">The output path resolves to the source boot image.
    /// <para>输出路径解析后与源 boot 镜像相同。</para></exception>
    /// <exception cref="InvalidDataException">An image lacks a ramdisk, or a ramdisk compression is
    /// unsupported, corrupt, or cannot be reproduced. <para>某镜像不含 ramdisk，或 ramdisk 压缩不受支持、已损坏、无法复现。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.ImageParseException">
    /// Either boot image cannot be parsed. <para>任一 boot 镜像无法解析。</para></exception>
    public async Task<RecoveryRamdiskMergeResult> MergeAsync(
        string sourceBootPath, string recoveryPath, string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (Path.GetFullPath(outputPath).Equals(Path.GetFullPath(sourceBootPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output must differ from the source boot image.");

        using var source = new BootImageParser();
        using var recovery = new BootImageParser();
        source.ParseFile(sourceBootPath);
        recovery.ParseFile(recoveryPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Ramdisk.Length == 0 || recovery.Ramdisk.Length == 0)
            throw new InvalidDataException("Both images must contain a ramdisk.");

        var sourceRaw = Decompress(source.Ramdisk, source.Header.RamdiskCompression);
        var recoveryRaw = Decompress(recovery.Ramdisk, recovery.Header.RamdiskCompression);
        var merged = MergeCpio(sourceRaw, recoveryRaw, cancellationToken);
        var compressed = Compress(merged, source.Header.RamdiskCompression);
        var packed = BootImagePacker.Repack(source, ramdisk: compressed);

        var temporary = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllBytesAsync(temporary, packed, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, outputPath, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        return new RecoveryRamdiskMergeResult(outputPath, sourceRaw.Length, recoveryRaw.Length, merged.Length,
            source.Header.RamdiskCompression);
    }

    private static byte[] MergeCpio(byte[] source, byte[] recovery, CancellationToken cancellationToken)
    {
        var target = new CpioArchiveEditor();
        using (var stream = new MemoryStream(recovery, false)) target.Load(stream);
        var overlay = new CpioArchiveEditor();
        using (var stream = new MemoryStream(source, false)) overlay.Load(stream);
        foreach (var entry in overlay.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.AddOrReplace(entry.Name, entry.Data);
        }
        using var output = new MemoryStream();
        target.Save(output);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data, CompressionType type)
        => type == CompressionType.None ? data : BootCompressor.TryDecompress(data, type)
            ?? throw new InvalidDataException($"Unsupported or corrupt ramdisk compression: {type}.");

    private static byte[] Compress(byte[] data, CompressionType type)
    {
        if (type == CompressionType.None) return data;
        var format = type switch
        {
            CompressionType.Gzip => CompressionFormat.Gzip,
            CompressionType.Lz4 => CompressionFormat.Lz4,
            CompressionType.Xz => CompressionFormat.Xz,
            CompressionType.Bzip2 => CompressionFormat.Bzip2,
            CompressionType.Zstd => CompressionFormat.Zstd,
            CompressionType.Lzma => CompressionFormat.Lzma,
            _ => throw new InvalidDataException($"Ramdisk compression cannot be reproduced: {type}.")
        };
        return CompressionService.Compress(data, format);
    }
}

/// <summary>
/// Outcome of a recovery ramdisk merge, reporting the written path and uncompressed sizes.
/// <para>recovery ramdisk 合并的结果，报告写出路径与各未压缩大小。</para>
/// </summary>
/// <param name="OutputPath">Published output image path. <para>已发布的输出镜像路径。</para></param>
/// <param name="SourceBytes">Uncompressed size in bytes of the source ramdisk. <para>源 ramdisk 解压后的字节数。</para></param>
/// <param name="RecoveryBytes">Uncompressed size in bytes of the recovery ramdisk. <para>recovery ramdisk 解压后的字节数。</para></param>
/// <param name="MergedBytes">Uncompressed size in bytes of the merged CPIO archive. <para>合并后 CPIO 归档的字节数（未压缩）。</para></param>
/// <param name="Compression">Ramdisk compression inherited from the source header and reapplied to the
/// merged archive. <para>继承自源头部并重新应用于合并归档的 ramdisk 压缩方式。</para></param>
public sealed record RecoveryRamdiskMergeResult(string OutputPath, int SourceBytes, int RecoveryBytes,
    int MergedBytes, CompressionType Compression);