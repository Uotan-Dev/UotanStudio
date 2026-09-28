using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>Owns source filesystem configuration, sidecar generation and preservation capture.</summary>
internal static class FileSystemRepackMetadata
{
    internal static void Configure(RepackMetadata metadata, string sourcePath, string outputDirectory, bool sparse = false)
    {
        metadata.Configuration = FileSystemRepackConfiguration.Read(sourcePath, sparse);
        metadata.CanPackDirectory = !sparse || File.Exists(Path.Combine(outputDirectory,
            Path.GetFileNameWithoutExtension(sourcePath) + ".raw"));
        string[] optionKeys = metadata.FormatName switch
        {
            "EXT4" => ["BlockSize"],
            "EROFS" => ["BlockSize", "VolumeName"],
            "F2FS" => ["BlockSize", "SegmentSize", "VolumeName"],
            _ => []
        };
        foreach (var key in optionKeys)
        {
            if (metadata.SourceProperties.TryGetValue(key, out var value))
            {
                metadata.PackingOptions[key] = value;
            }
        }
        if (metadata.Configuration.ContainsKey("superblock"))
        {
            metadata.PackingNotes += metadata.Configuration["fileSystem"]?.GetValue<string>() == "EROFS"
                ? " EROFS edits inherit codec parameters, UUID, volume and inode attributes from the verified original image; compression level is not recorded on disk. Physical layout is regenerated."
                : " configuration.superblock records the original filesystem metadata for inspection; only packingOptions are applied when rebuilding the filesystem.";
        }
    }

    internal static async Task CaptureAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken, bool sparse = false)
    {
        if (sparse && metadata.Configuration["fileSystem"]?.GetValue<string>() is not ("EXT4" or "EROFS" or "F2FS"))
        {
            return;
        }
        Directory.CreateDirectory(metadataDirectory);
        GenerateFileSystemMetadata(metadataDirectory, RepackMetadata.DetectPartitionName(Path.GetFileName(sourcePath)),
            metadata.FormatName, sourcePath, metadata.Configuration, cancellationToken);
        metadata.CanPackDirectory = true;
        metadata.PackingNotes = "Retains structured inode metadata and hash-deduplicated content objects, not the original image. Snapshot replay does not require the source image or host tree. Physical layout is regenerated; attributes and file content are verified before publication.";
        await FileSystemPreservation.CaptureAsync(sourcePath, outputDirectory, metadataDirectory, metadata, cancellationToken).ConfigureAwait(false);
    }

    private static void GenerateFileSystemMetadata(string metadataDirectory, string partitionName,
        string formatName, string sourcePath, JsonObject configuration, CancellationToken cancellationToken)
    {
        try
        {
            var superblock = configuration.ContainsKey("superblock") ? configuration["superblock"] as JsonObject : null;
            if (formatName == "Android Sparse Image (simg)")
            {
                using var container = SparseContainerAccess.Resolve().OpenLogicalView(sourcePath);
                var logical = container.Logical;
                switch (configuration["fileSystem"]?.GetValue<string>())
                {
                    case "EXT4":
                        using (var parser = new UotanStudio.Firmware.FileSystem.Ext4.Ext4Parser(logical))
                        {
                            FileSystemMetadataGenerator.GenerateForExt4(metadataDirectory, partitionName, parser, superblock);
                        }
                        break;
                    case "EROFS":
                        using (var parser = new UotanStudio.Firmware.FileSystem.Erofs.Parser.ErofsParser(logical))
                        {
                            FileSystemMetadataGenerator.GenerateForErofs(metadataDirectory, partitionName, parser, superblock);
                        }
                        break;
                    case "F2FS":
                        using (var parser = new UotanStudio.Firmware.FileSystem.F2fs.F2fsParser(logical))
                        {
                            FileSystemMetadataGenerator.GenerateForF2fs(metadataDirectory, partitionName, parser, superblock);
                        }
                        break;
                }
                return;
            }

            using var stream = File.OpenRead(sourcePath);
            switch (formatName)
            {
                case "EXT4":
                    using (var parser = new UotanStudio.Firmware.FileSystem.Ext4.Ext4Parser(stream))
                    {
                        FileSystemMetadataGenerator.GenerateForExt4(metadataDirectory, partitionName, parser, superblock);
                    }

                    break;
                case "EROFS":
                    using (var parser = FileSystemPreservation.OpenErofsParser(stream, cancellationToken))
                    {
                        FileSystemMetadataGenerator.GenerateForErofs(metadataDirectory, partitionName, parser, superblock);
                    }

                    break;
                case "F2FS":
                    using (var parser = new UotanStudio.Firmware.FileSystem.F2fs.F2fsParser(stream))
                    {
                        FileSystemMetadataGenerator.GenerateForF2fs(metadataDirectory, partitionName, parser, superblock);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to generate filesystem metadata: {ex.Message}");
        }
    }
}