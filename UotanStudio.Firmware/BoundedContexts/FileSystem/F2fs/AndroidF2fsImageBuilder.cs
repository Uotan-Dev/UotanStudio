using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Native;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

/// <summary>Builds an Android-profile F2FS image with native quota and read-only metadata structures.</summary>
public static class AndroidF2fsImageBuilder
{
    public static async Task BuildAsync(string sourceDirectory, string outputPath, long imageSizeBytes,
        NativeFileSystemTools tools, string mountPoint = "/system", string? fsConfigPath = null,
        string? fileContextsPath = null, bool enableCompression = false,
        CompressionAlgorithm compressionAlgorithm = CompressionAlgorithm.Lz4,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(outputPath);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }
        if (fsConfigPath is not null && !File.Exists(fsConfigPath))
        {
            throw new FileNotFoundException("Android fs_config file was not found.", fsConfigPath);
        }
        if (fileContextsPath is not null && !File.Exists(fileContextsPath))
        {
            throw new FileNotFoundException("Android file_contexts file was not found.", fileContextsPath);
        }
        if (imageSizeBytes < 64L * 1024 * 1024 || imageSizeBytes % (2 * 1024 * 1024) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageSizeBytes),
                "Android F2FS capacity must be at least 64 MiB and aligned to 2 MiB.");
        }
        if (enableCompression && compressionAlgorithm != CompressionAlgorithm.Lz4)
        {
            throw new NotSupportedException("The native Android F2FS profile currently supports only LZ4 compression.");
        }
        if (!mountPoint.StartsWith('/') || mountPoint.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Mount point must be an absolute Android path.", nameof(mountPoint));
        }
        if (destination.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The output must not be inside the source directory.", nameof(outputPath));
        }

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var image = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                image.SetLength(imageSizeBytes);
            }
            var imagePath = tools.PathForTool(temporary, cancellationToken);
            var sourcePath = tools.PathForTool(source, cancellationToken);
            var mkfs = await tools.RunAsync("mkfs.f2fs",
                ["-f", "-t", "0", "-g", "android", "-O",
                    enableCompression ? "extra_attr,project_quota,ro,compression" : "extra_attr,project_quota,ro", imagePath],
                cancellationToken).ConfigureAwait(false);
            NativeFileSystemTools.RequireSuccess("mkfs.f2fs", mkfs);

            var args = new List<string> { "-f", sourcePath, "-t", mountPoint };
            if (fsConfigPath is not null)
            {
                args.Add("-C");
                args.Add(tools.PathForTool(fsConfigPath, cancellationToken));
            }
            if (fileContextsPath is not null)
            {
                args.Add("-s");
                args.Add(tools.PathForTool(fileContextsPath, cancellationToken));
            }
            if (enableCompression)
            {
                args.Add("-c");
            }
            args.Add(imagePath);
            NativeFileSystemTools.RequireSuccess("sload.f2fs",
                await tools.RunAsync("sload.f2fs", args, cancellationToken).ConfigureAwait(false));
            NativeFileSystemTools.RequireSuccess("fsck.f2fs",
                await tools.RunAsync("fsck.f2fs", ["--dry-run", imagePath], cancellationToken).ConfigureAwait(false));

            await using (var image = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var header = new byte[1024 + 2184];
                await image.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                var features = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024 + 2180));
                var required = 0x0008u | 0x0010u | 0x4000u | (enableCompression ? 0x2000u : 0);
                if ((features & required) != required)
                {
                    throw new InvalidDataException($"Native F2FS image lacks required Android features (0x{features:X8}).");
                }
                image.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}