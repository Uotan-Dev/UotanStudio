using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.SharedKernel.Streams;
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>Retains source semantics and resolves edited host files into filesystem builders with original or inherited Linux attributes.</summary>
public static class FileSystemPreservation
{
    private const string FolderName = ".filesystem";
    private const int MaximumEntries = 100000;
    private const long MaximumManifestBytes = 32 * 1024 * 1024;
    private static readonly AsyncLocal<Dictionary<string, string>?> CapturedPaths = new();
    private static readonly AsyncLocal<IProgress<FileSystemByteProgress>?> ActiveProgress = new();

    internal static void RecordPaths(Dictionary<string, string> paths)
    {
        if (CapturedPaths.Value is { } captured)
            foreach (var pair in paths) captured[pair.Key] = pair.Value;
    }

    internal static void RejectLossyDirectoryRebuild(string directory)
    {
        if (FileSystemEditPlan.Current is { } plan && Path.GetFullPath(directory) == plan.Root) return;
        var metadataPath = RepackMetadata.ResolveActualMetadataPath(directory);
        var hasSnapshot = Directory.Exists(Path.Combine(directory, FolderName))
            || metadataPath is not null && Directory.Exists(Path.Combine(Path.GetDirectoryName(metadataPath)!, FolderName));
        if (hasSnapshot || metadataPath is not null && RepackMetadata.IsMetadataFile(metadataPath)
            && RepackMetadata.Read(directory)?.FormatName is "EXT4" or "EROFS" or "F2FS" or "Android Sparse Image (simg)")
            throw new InvalidDataException("Use the filesystem format handler for an extracted image; a fresh directory build would discard preserved source semantics.");
    }

    /// <summary>Parses a private frozen source so extraction, inspection and the retained image have identical input bytes.</summary>
    public static async Task<FirmwareOpResult> UnpackAsync(IReadonlyFormatHandler handler, string sourcePath,
        string outputDirectory, UnpackOptions? options, CancellationToken cancellationToken,
        Func<string, Task<FirmwareOpResult>> unpack)
    {
        var context = RepackMetadataContext.Unspecified.Capture();
        var previousProgress = ActiveProgress.Value;
        ActiveProgress.Value = options?.FileSystemProgress;
        CapturedPaths.Value = new(StringComparer.Ordinal);
        var temporary = Path.Combine(Path.GetTempPath(), "uotan-filesystem-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var frozen = Path.Combine(temporary, Path.GetFileName(sourcePath));
            var before = new FileInfo(sourcePath);
            var sourceLength = before.Length;
            var sourceTime = before.LastWriteTimeUtc;
            await using (var input = File.OpenRead(sourcePath))
            await using (var output = new FileStream(frozen, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var bytes = new ExtractionByteCounter(input.Length, FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.Preparing), cancellationToken);
                using var counted = bytes.Wrap(output);
                await input.CopyToAsync(counted, 65536, cancellationToken).ConfigureAwait(false);
                bytes.Complete();
            }
            var after = new FileInfo(sourcePath);
            if (after.Length != sourceLength || after.LastWriteTimeUtc != sourceTime)
                throw new IOException("Source image changed while preparing its private snapshot.");
            using var scope = RepackMetadata.UseContext(context);
            var result = await RepackMetadata.UnpackAsync(handler, frozen, outputDirectory, options, cancellationToken,
                async () =>
                {
                    var extracted = await unpack(frozen).ConfigureAwait(false);
                    ActiveProgress.Value?.Report(new(FileSystemWorkStage.ReadingMetadata, 0, 0));
                    return extracted;
                }).ConfigureAwait(false);
            return new FirmwareOpResult
            {
                Success = result.Success, Summary = result.Summary.Replace(frozen, sourcePath, StringComparison.Ordinal),
                Entries = result.Entries, Metadata = result.Metadata, Components = result.Components
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
        finally
        {
            ActiveProgress.Value = previousProgress;
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    internal static async Task CaptureAsync(string source, string extraction, string metadataDirectory,
        RepackMetadata metadata, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = FileExtraction.ResolvePath(metadataDirectory, FolderName + "/" + id);
        Directory.CreateDirectory(directory);
        try
        {
            var snapshot = Path.Combine(directory, "original.img");
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var bytes = new ExtractionByteCounter(input.Length, FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.Preserving), cancellationToken);
                using var counted = bytes.Wrap(output);
                await input.CopyToAsync(counted, 65536, cancellationToken).ConfigureAwait(false);
                bytes.Complete();
            }
            metadata.FileSystemPreservationId = id;
            var manifest = new FileSystemPreservationManifest
            {
                Format = metadata.FormatName,
                SourceLength = new FileInfo(snapshot).Length,
                SourceHash = HashImageWithProgress(snapshot, cancellationToken),
                ConfigurationHash = HashConfiguration(metadata),
                ConfigurationJson = JsonSerializer.Serialize(metadata, RepackJsonContext.Default.RepackMetadata),
                ImagePaths = CapturedPaths.Value is { } paths ? new(paths, StringComparer.Ordinal) : [],
                ParsedEntries = ReadEntries(source, metadata.FormatName),
                Projection = Scan(extraction, metadataDirectory, metadata.SourceFileName, cancellationToken,
                    ActiveProgress.Value is null ? null : FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.VerifyingFiles)),
                Sidecars = ReadSidecars(metadataDirectory, metadata.SourceFileName, cancellationToken)
            };
            var path = Path.Combine(directory, "manifest.json");
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var bounded = new CountingWriteStream(file, MaximumManifestBytes, leaveOpen: true);
                await JsonSerializer.SerializeAsync(bounded, manifest, FileSystemPreservationJsonContext.Default.FileSystemPreservationManifest,
                    cancellationToken).ConfigureAwait(false);
            }
            metadata.FileSystemPreservationHash = HashFile(path, cancellationToken);
        }
        catch
        {
            Directory.Delete(directory, true);
            throw;
        }
    }

    /// <summary>Restores an unchanged image exactly, or rebuilds edited files and directories using validated source metadata.</summary>
    public static bool TryRestore(string inputDirectory, string outputPath, string format,
        CancellationToken cancellationToken = default,
        Action<IReadOnlyDictionary<string, string>>? rebuildReport = null)
    {
        var metadata = RepackMetadata.Read(inputDirectory);
        if (metadata is null) return false;
        if (metadata.FormatName != format) throw new InvalidDataException("Filesystem metadata format does not match the requested image format.");
        // A non-filesystem Sparse extraction contains one RAW file and has no inode snapshot to restore.
        if (format == "Android Sparse Image (simg)" && metadata.Configuration.Count == 0
            && metadata.FileSystemPreservationId is null && metadata.FileSystemPreservationHash is null
            && File.Exists(Path.Combine(inputDirectory, Path.GetFileNameWithoutExtension(metadata.SourceFileName) + ".raw")))
            return false;
        if (metadata.FileSystemPreservationId is not { Length: 32 } id || !Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("Filesystem preservation data is missing. Re-extract the source image before rebuilding it.");
        var metadataDirectory = Path.GetDirectoryName(RepackMetadata.ResolveActualMetadataPath(inputDirectory))!;
        var directory = FileExtraction.ResolvePath(metadataDirectory, FolderName + "/" + id);
        var manifestPath = FileExtraction.ResolvePath(directory, "manifest.json");
        if (new FileInfo(manifestPath).Length > MaximumManifestBytes) throw new InvalidDataException("Filesystem preservation manifest exceeds its size limit.");
        if (metadata.FileSystemPreservationHash != HashFile(manifestPath, cancellationToken))
            throw new InvalidDataException("Filesystem preservation manifest hash mismatch.");
        FileSystemPreservationManifest manifest;
        using (var file = File.OpenRead(manifestPath))
        {
            if (file.Length > MaximumManifestBytes) throw new InvalidDataException("Filesystem preservation manifest exceeds its size limit.");
            manifest = JsonSerializer.Deserialize(file, FileSystemPreservationJsonContext.Default.FileSystemPreservationManifest)
                ?? throw new InvalidDataException("Invalid filesystem preservation manifest.");
        }
        if (manifest.Version != 1 || manifest.Format != format || manifest.SourceLength < 0
            || manifest.Projection is null || manifest.Sidecars is null || manifest.Projection.Count > MaximumEntries)
            throw new InvalidDataException("Unsupported filesystem preservation manifest.");
        var volumeEdit = ValidateConfiguration(metadata, manifest);
        if (!Same(ReadSidecars(metadataDirectory, metadata.SourceFileName, cancellationToken), manifest.Sidecars))
            throw new InvalidDataException("Filesystem configuration changed. The current writer cannot guarantee preservation of every source feature after this change.");
        var projection = Scan(inputDirectory, metadataDirectory, metadata.SourceFileName, cancellationToken);
        var edited = !Same(projection, manifest.Projection);
        var snapshot = FileExtraction.ResolvePath(directory, "original.img");
        var fullOutput = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var protectedRoot in new[] { inputDirectory, metadataDirectory })
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
            if (fullOutput.Equals(root, comparison) || fullOutput.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new IOException("Rebuilt output must be outside the extracted tree and its preservation data.");
        }
        if (edited)
        {
            if (HashFile(snapshot, cancellationToken) != manifest.SourceHash || new FileInfo(snapshot).Length != manifest.SourceLength)
                throw new InvalidDataException("Retained filesystem image changed.");
            manifest.ParsedEntries = ReadEntries(snapshot, format);
            var plan = new FileSystemEditPlan(inputDirectory, manifest, projection, RepackMetadata.DetectPartitionName(metadata.SourceFileName));
            using var editScope = plan.Enter();
            FileExtraction.WriteAtomic(fullOutput, output =>
            {
                BuildEdited(inputDirectory, output, snapshot, format, metadata.PackingOptions, cancellationToken, rebuildReport: rebuildReport);
                cancellationToken.ThrowIfCancellationRequested();
                var current = RepackMetadata.Read(inputDirectory);
                if (current is null || HashConfiguration(current) != HashConfiguration(metadata)
                    || current.FileSystemPreservationHash != metadata.FileSystemPreservationHash
                    || !Same(ReadSidecars(metadataDirectory, metadata.SourceFileName, cancellationToken), manifest.Sidecars)
                    || !Same(Scan(inputDirectory, metadataDirectory, metadata.SourceFileName, cancellationToken), projection))
                    throw new IOException("Filesystem content or attributes changed during rebuilding.");
            }, overwrite: true);
            return true;
        }
        using var source = File.OpenRead(snapshot);
        if (source.Length != manifest.SourceLength) throw new InvalidDataException("Retained filesystem image length changed.");
        FileExtraction.WriteAtomic(fullOutput, output =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            int count;
            while ((count = source.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer.AsSpan(0, count));
                output.Write(buffer.AsSpan(0, count));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Convert.ToHexString(hash.GetHashAndReset()) != manifest.SourceHash)
                throw new InvalidDataException("Retained filesystem image hash mismatch.");
            if (volumeEdit is not null) PatchErofsVolume(output, volumeEdit);
            var current = RepackMetadata.Read(inputDirectory);
            if (current is null || HashConfiguration(current) != HashConfiguration(metadata)
                || current.FileSystemPreservationHash != metadata.FileSystemPreservationHash
                || !Same(ReadSidecars(metadataDirectory, metadata.SourceFileName, cancellationToken), manifest.Sidecars)
                || !Same(Scan(inputDirectory, metadataDirectory, metadata.SourceFileName, cancellationToken), manifest.Projection))
                throw new IOException("Filesystem projection or configuration changed while restoring the image.");
        }, overwrite: true);
        return true;
    }

    private static void BuildEdited(string inputDirectory, Stream output, string snapshot, string format,
        Dictionary<string, string> options, CancellationToken token, Stream? sourceFilesystem = null,
        Action<IReadOnlyDictionary<string, string>>? rebuildReport = null)
    {
        token.ThrowIfCancellationRequested();
        if (format == "Android Sparse Image (simg)")
        {
            using var sparse = SparseReader.FromImageFile(snapshot);
            using var logical = new SparseReadStream(sparse);
            var inner = DetectFilesystem(logical);
            var temporary = Path.Combine(Path.GetTempPath(), "uotan-edited-raw-" + Guid.NewGuid().ToString("N"));
            using var raw = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
            BuildEdited(inputDirectory, raw, snapshot, inner, options, token, logical, rebuildReport);
            raw.Position = 0;
            SparseImageConverter.ConvertRawToSparse(raw, output, sparse.Header.BlockSize, raw.Length);
            return;
        }
        using var original = sourceFilesystem ?? File.OpenRead(snapshot);
        original.Position = 0;
        switch (format)
        {
            case "EXT4":
                using (var parser = new Ext4Parser(original))
                Ext4Builder.Build(inputDirectory, output, new Ext4Options { InodeSize = 4096, BlockSize = checked((int)parser.BlockSize), Uuid = parser.SourceUuid, VolumeName = parser.SourceVolumeName });
                break;
            case "EROFS":
                using (var parser = new ErofsParser(original))
                {
                    var erofsOptions = parser.ReadRebuildOptions();
                    erofsOptions.VolumeName = options.GetValueOrDefault("VolumeName", parser.SourceVolumeName);
                    if (erofsOptions.VolumeName != parser.SourceVolumeName) erofsOptions.SourceVolumeNameBytes = null;
                    // Sparse logical streams are forward-only in the conversion pipeline. Keep the
                    // established single-pass builder there; direct EROFS files use candidate sizing.
                    if (sourceFilesystem is null)
                        rebuildReport?.Invoke(ErofsRebuildOptimizer.Build(inputDirectory, output, parser, erofsOptions, token));
                    else
                        new ErofsBuilder().Build(inputDirectory, output, erofsOptions);
                }
                break;
            case "F2FS":
                using (var parser = new F2fsParser(original))
                F2fsBuilder.Build(inputDirectory, output, new F2fsOptions { Uuid = new Guid(parser.SourceUuid), VolumeName = options.GetValueOrDefault("VolumeName", parser.SourceVolumeName) });
                break;
            default: throw new InvalidDataException("Editing this filesystem container is not supported.");
        }
    }

    private static string DetectFilesystem(Stream stream)
    {
        var probe = new byte[2048];
        stream.Position = 0;
        stream.ReadExactly(probe);
        stream.Position = 0;
        if (BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(1080)) == 0xEF53) return "EXT4";
        return BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(1024)) switch
        {
            0xE0F5E1E2 => "EROFS", 0xF2F52010 => "F2FS",
            _ => throw new InvalidDataException("Sparse image does not contain a supported editable filesystem.")
        };
    }

    private static string? ValidateConfiguration(RepackMetadata metadata, FileSystemPreservationManifest manifest)
    {
        if (HashConfiguration(metadata) == manifest.ConfigurationHash) return null;
        if (metadata.FormatName == "EROFS" && manifest.ConfigurationJson is { Length: > 0 })
        {
            var original = JsonSerializer.Deserialize(manifest.ConfigurationJson, RepackJsonContext.Default.RepackMetadata)
                ?? throw new InvalidDataException("Invalid original filesystem configuration.");
            var modified = JsonSerializer.Deserialize(JsonSerializer.Serialize(metadata, RepackJsonContext.Default.RepackMetadata), RepackJsonContext.Default.RepackMetadata)!;
            if (modified.PackingOptions.TryGetValue("VolumeName", out var requested)
                && original.PackingOptions.TryGetValue("VolumeName", out var previous)
                && Encoding.UTF8.GetByteCount(requested) <= 16 && !requested.Contains('\0'))
            {
                modified.PackingOptions["VolumeName"] = previous;
                if (HashConfiguration(modified) == manifest.ConfigurationHash) return requested;
            }
        }
        throw new InvalidDataException("Filesystem configuration changed. The current writer cannot guarantee preservation of every source feature after this change.");
    }

    private static void PatchErofsVolume(FileStream image, string volume)
    {
        image.Position = ErofsSuperblock.SuperblockOffset;
        var header = new byte[128];
        image.ReadExactly(header);
        var superblock = ErofsSuperblock.FromBytes(header);
        if (superblock.BlockSize is < 512 or > 65536
            || checked((ulong)image.Length) != checked(superblock.BlockCount64 * (ulong)superblock.BlockSize))
            throw new InvalidDataException("Cannot change an EROFS volume with additional image tails or unsupported block geometry.");
        var region = new byte[superblock.BlockSize > ErofsSuperblock.SuperblockOffset
            ? superblock.BlockSize - ErofsSuperblock.SuperblockOffset : superblock.BlockSize];
        image.Position = ErofsSuperblock.SuperblockOffset;
        image.ReadExactly(region);
        region.AsSpan(64, 16).Clear();
        Encoding.UTF8.GetBytes(volume, region.AsSpan(64, 16));
        if (superblock.HasCompatChecksum)
        {
            region.AsSpan(4, 4).Clear();
            uint crc = uint.MaxValue;
            foreach (var value in region)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82F63B78u : 0);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(4, 4), crc);
        }
        image.Position = ErofsSuperblock.SuperblockOffset;
        image.Write(region);
    }

    private static bool Same(Dictionary<string, string> actual, Dictionary<string, string> expected)
        => actual.Count == expected.Count && actual.All(pair => expected.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static string HashConfiguration(RepackMetadata metadata)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(metadata, RepackJsonContext.Default.RepackMetadata))!.AsObject();
        json.Remove("fileSystemPreservationHash");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    private static Dictionary<string, string> ReadSidecars(string directory, string source, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var suffix in new[] { "_fs_config", "_file_contexts", "_info" })
        {
            var name = RepackMetadata.DetectPartitionName(source) + suffix;
            var path = FileExtraction.ResolvePath(directory, name);
            if (File.Exists(path)) result.Add(name, HashFile(path, token));
        }
        return result;
    }

    private static string HashImageWithProgress(string path, CancellationToken token)
    {
        var bytes = new ExtractionByteCounter(new FileInfo(path).Length,
            FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.VerifyingImage), token);
        var hash = HashFile(path, token, bytes.Add);
        bytes.Complete();
        return hash;
    }

    private static string HashFile(string path, CancellationToken token, Action<int>? processed = null)
    {
        using var input = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer.AsSpan(0, count));
            processed?.Invoke(count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static Dictionary<string, string> Scan(string extraction, string metadataDirectory, string sourceName, CancellationToken token,
        IProgress<(long Processed, long Total)>? progress = null)
    {
        var root = Path.GetFullPath(extraction);
        var metadata = Path.GetFullPath(metadataDirectory);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingHashes = new List<(string Path, string Relative, long Size)>();
        var pending = new Stack<string>();
        pending.Push(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (directory.Equals(metadata, comparison)
                    && (Path.GetFileName(path) == FolderName || RepackMetadata.IsMetadataFile(path)
                        || Path.GetFileName(path) == RepackMetadata.DetectPartitionName(sourceName) + "_fs_config"
                        || Path.GetFileName(path) == RepackMetadata.DetectPartitionName(sourceName) + "_file_contexts"
                        || Path.GetFileName(path) == RepackMetadata.DetectPartitionName(sourceName) + "_info")) continue;
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var info = new FileInfo(path);
                string value;
                if (info.LinkTarget is { } target) value = "link:" + target;
                else if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    value = "directory";
                    pending.Push(path);
                }
                else if (progress is not null)
                {
                    pendingHashes.Add((path, relative, info.Length));
                    value = "";
                }
                else value = "file:" + HashFile(path, token);
                if (!OperatingSystem.IsWindows() && info.LinkTarget is null)
                    value += ":mode=" + (int)File.GetUnixFileMode(path);
                result.Add(relative, value);
                if (result.Count > MaximumEntries) throw new InvalidDataException("Filesystem projection exceeds 100000 entries.");
            }
        }
        if (progress is not null)
        {
            var bytes = new ExtractionByteCounter(pendingHashes.Sum(p => p.Size), progress, token);
            foreach (var pendingHash in pendingHashes)
                result[pendingHash.Relative] = "file:" + HashFile(pendingHash.Path, token, bytes.Add) + result[pendingHash.Relative];
            bytes.Complete();
        }
        return result;
    }

    private static string ReadSymlink(Action<Stream> read)
    {
        using var bytes = new MemoryStream();
        using var bounded = new CountingWriteStream(bytes, 1024 * 1024, leaveOpen: true);
        read(bounded);
        return PortableExtractionPaths.NameEncoding.GetString(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
    }

    private static List<PreservedInodeEntry> ReadEntries(string source, string format)
    {
        if (format == "Android Sparse Image (simg)")
        {
            using var sparse = SparseReader.FromImageFile(source);
            using var logical = new SparseReadStream(sparse);
            return ReadEntries(logical, DetectFilesystem(logical));
        }
        using var stream = File.OpenRead(source);
        return ReadEntries(stream, format);
    }

    private static List<PreservedInodeEntry> ReadEntries(Stream source, string format)
    {
        if (format == "EXT4")
        {
            using var parser = new Ext4Parser(source);
            return parser.ListEntriesWithRoot().Select(e => new PreservedInodeEntry
            {
                Path = e.Path, Inode = e.Inode, Flags = e.Flags, DeviceId = e.DeviceId, Mode = e.Mode, Uid = e.Uid, Gid = e.Gid,
                SymlinkTarget = (e.Mode & 0xF000) == 0xA000 ? ReadSymlink(output => parser.ReadFile(e.Path, output)) : null, Xattrs = e.Xattrs?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            }).ToList();
        }
        if (format == "EROFS")
        {
            using var parser = new ErofsParser(source);
            return parser.EnumerateEntries().Select(e => new PreservedInodeEntry
            {
                Path = e.Path, Inode = e.Nid, DeviceId = e.DeviceId, Mode = e.Mode, Uid = e.Uid, Gid = e.Gid,
                SymlinkTarget = (e.Mode & 0xF000) == 0xA000 ? ReadSymlink(output => parser.ReadFile(e.Path, output)) : null, Xattrs = e.Xattrs?.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal)
            }).ToList();
        }
        if (format == "F2FS")
        {
            using var parser = new F2fsParser(source);
            return parser.ListEntriesWithRoot().Select(e => new PreservedInodeEntry
            {
                Path = e.Path, Inode = e.Inode, Flags = e.Flags, DeviceId = e.DeviceId, Mode = e.Mode, Uid = e.Uid, Gid = e.Gid, SymlinkTarget = (e.Mode & 0xF000) == 0xA000 ? ReadSymlink(output => parser.ReadFile(e.Path, output)) : null, Xattrs = e.Xattrs?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            }).ToList();
        }
        return [];
    }
}

internal sealed class PreservedInodeEntry
{
    public string Path { get; set; } = "";
    public ulong Inode { get; set; }
    public uint Flags { get; set; }
    public uint DeviceId { get; set; }
    public ushort Mode { get; set; }
    public uint Uid { get; set; }
    public uint Gid { get; set; }
    public string? SymlinkTarget { get; set; }
    public Dictionary<string, byte[]>? Xattrs { get; set; }
}

internal sealed class FileSystemPreservationManifest
{
    public int Version { get; set; } = 1;
    public string Format { get; set; } = "";
    public long SourceLength { get; set; }
    public string SourceHash { get; set; } = "";
    public string ConfigurationHash { get; set; } = "";
    public string ConfigurationJson { get; set; } = "";
    public Dictionary<string, string> Projection { get; set; } = [];
    public Dictionary<string, string> Sidecars { get; set; } = [];
    public Dictionary<string, string> ImagePaths { get; set; } = [];
    public List<PreservedInodeEntry> ParsedEntries { get; set; } = [];
}

[JsonSerializable(typeof(FileSystemPreservationManifest))]
internal partial class FileSystemPreservationJsonContext : JsonSerializerContext;
