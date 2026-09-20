using UotanStudio.Firmware.BoundedContexts.Archive.Editing;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.BoundedContexts.Compression;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Workflow;

/// <summary>Merges a recovery ramdisk into an Android boot image with explicit source precedence.</summary>
public sealed class RecoveryRamdiskMergeService
{
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

public sealed record RecoveryRamdiskMergeResult(string OutputPath, int SourceBytes, int RecoveryBytes,
    int MergedBytes, CompressionType Compression);