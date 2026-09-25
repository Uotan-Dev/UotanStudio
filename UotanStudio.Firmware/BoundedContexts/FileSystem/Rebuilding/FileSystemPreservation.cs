using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Extraction;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;

/// <summary>Retains source semantics and resolves edited host files into filesystem builders with original or inherited Linux attributes.</summary>
public static partial class FileSystemPreservation
{
    private const string FolderName = ".filesystem";
    private const int MaximumEntries = 100000;
    private const long MaximumManifestBytes = 32 * 1024 * 1024;
    private static readonly AsyncLocal<Dictionary<string, string>?> CapturedPaths = new();
    private static readonly AsyncLocal<IReadOnlyList<string>?> FrozenErofsDevices = new();

    /// <summary>Opens the frozen EROFS device set for the current preservation operation.</summary>
    public static ErofsParser OpenErofsParser(Stream source, CancellationToken token = default,
        bool requireCompleteMetadata = false)
    {
        if (FrozenErofsDevices.Value is not { } paths)
            return new ErofsParser(source, int.MaxValue, long.MaxValue, token)
            { RequireCompleteMetadata = requireCompleteMetadata };
        var streams = new Stream[paths.Count + 1];
        streams[0] = source;
        try
        {
            for (var i = 0; i < paths.Count; i++)
                streams[i + 1] = File.OpenRead(paths[i]);
            return new ErofsParser(streams, int.MaxValue, long.MaxValue, token)
            { RequireCompleteMetadata = requireCompleteMetadata };
        }
        catch
        {
            for (var i = 1; i < streams.Length; i++)
                streams[i]?.Dispose();
            throw;
        }
    }
    private static readonly AsyncLocal<IProgress<FileSystemByteProgress>?> ActiveProgress = new();

    internal static void RecordPaths(Dictionary<string, string> paths)
    {
        if (CapturedPaths.Value is { } captured)
            foreach (var pair in paths) captured[pair.Key] = pair.Value;
    }

    internal static void RejectLossyDirectoryRebuild(string directory)
    {
        if (RepackMetadata.HasPackingOverrides) return;
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
        var previousDevices = FrozenErofsDevices.Value;
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
            var external = options?.ErofsExtraDevicePaths;
            if (external is { Count: > 0 } && handler.FormatName != "EROFS")
                throw new ArgumentException("External device images are supported only for EROFS.");
            if (external is { Count: > ushort.MaxValue })
                throw new InvalidDataException("EROFS external device count exceeds the on-disk limit.");
            var originals = new List<string> { Path.GetFullPath(sourcePath) };
            var frozenDevices = new List<string>();
            if (external is not null)
            {
                foreach (var path in external)
                {
                    if (string.IsNullOrWhiteSpace(path))
                        throw new InvalidDataException("EROFS external device path is empty.");
                    originals.Add(Path.GetFullPath(path));
                }
            }
            if (originals.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Count() != originals.Count)
                throw new InvalidDataException("EROFS device images must be distinct.");
            var total = originals.Aggregate(0L, (sum, path) => checked(sum + new FileInfo(path).Length));
            var bytes = new ExtractionByteCounter(total,
                FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.Preparing), cancellationToken);
            for (var i = 0; i < originals.Count; i++)
            {
                var original = originals[i];
                var target = i == 0 ? frozen : Path.Combine(temporary, $"device-{i}.img");
                var info = new FileInfo(original);
                var length = info.Length;
                var time = info.LastWriteTimeUtc;
                await using (var input = File.OpenRead(original))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    using var counted = bytes.Wrap(output);
                    await StreamCopy.CopyExactlyAsync(input, counted, length, cancellationToken).ConfigureAwait(false);
                    if (input.Length != length)
                        throw new IOException("EROFS device changed during snapshot preparation.");
                }
                info.Refresh();
                if (info.Length != length || info.LastWriteTimeUtc != time)
                    throw new IOException("EROFS device changed during snapshot preparation.");
                if (i != 0) frozenDevices.Add(target);
            }
            bytes.Complete();
            FrozenErofsDevices.Value = external is null ? null : frozenDevices;
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
                Success = result.Success,
                Summary = result.Summary.Replace(frozen, sourcePath, StringComparison.Ordinal),
                Entries = result.Entries,
                Metadata = result.Metadata,
                Components = result.Components
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
        finally
        {
            ActiveProgress.Value = previousProgress;
            FrozenErofsDevices.Value = previousDevices;
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
            var rebuild = BuildRebuildParameters(source, metadata.FormatName);
            var parsedEntries = ReadEntries(source, metadata.FormatName);
            ValidateInodeFlags(parsedEntries, rebuild?.InnerFormat ?? metadata.FormatName);
            var document = new FilesystemMetadataDocument
            {
                Partition = RepackMetadata.DetectPartitionName(metadata.SourceFileName),
                Rebuild = rebuild,
                PartitionInfo = metadata.Configuration["superblock"] is JsonObject superblock
                    ? superblock.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty,
                        StringComparer.Ordinal)
                    : new(StringComparer.Ordinal),
                FsConfig = parsedEntries.ToDictionary(entry => FileSystemEditPlan.Normalize(entry.Path),
                    entry => new FsConfigEntry(entry.Uid, entry.Gid, (ushort)(entry.Mode & 0xFFF)), StringComparer.Ordinal),
                FileContexts = parsedEntries.Where(entry => entry.Xattrs?.ContainsKey("security.selinux") == true)
                    .ToDictionary(entry => FileSystemEditPlan.Normalize(entry.Path),
                        entry => Encoding.UTF8.GetString(entry.Xattrs!["security.selinux"]).TrimEnd('\0'), StringComparer.Ordinal)
            };
            var metadataPath = FileExtraction.ResolvePath(directory, "metadata.json");
            await using (var output = new FileStream(metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // No size cap here: the metadata document carries the full per-inode rebuild
                // profile (tens of MB for large partitions) and must never be silently truncated.
                // 此处不设大小上限：metadata 文档携带完整的逐 inode 重建档案（大分区可达数十 MB），
                // 绝不能被静默截断。
                await JsonSerializer.SerializeAsync(output, document, FilesystemMetadataJsonContext.Default.FilesystemMetadataDocument,
                    cancellationToken).ConfigureAwait(false);
            }
            metadata.FileSystemPreservationId = id;
            var imageBytes = new ExtractionByteCounter(new FileInfo(source).Length,
                FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.VerifyingImage), cancellationToken);
            var sourceHash = HashFile(source, cancellationToken, imageBytes.Add);
            imageBytes.Complete();
            var manifest = new FileSystemPreservationManifest
            {
                Version = 2,
                Format = metadata.FormatName,
                SourceLength = new FileInfo(source).Length,
                SourceHash = sourceHash,
                ConfigurationHash = HashConfiguration(metadata),
                ConfigurationJson = JsonSerializer.Serialize(metadata, RepackJsonContext.Default.RepackMetadata),
                ImagePaths = CapturedPaths.Value is { } paths ? new(paths, StringComparer.Ordinal) : [],
                ParsedEntries = parsedEntries,
                Projection = Scan(extraction, metadataDirectory, metadata.SourceFileName, cancellationToken,
                    ActiveProgress.Value is null ? null : FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.VerifyingFiles)),
                Sidecars = ReadSidecars(metadataDirectory, metadata.SourceFileName, id, cancellationToken, includeReferenceFiles: false)
            };
            VerifyCapturedContents(source, manifest, cancellationToken);
            await CaptureObjectsAsync(extraction, directory, manifest, cancellationToken).ConfigureAwait(false);
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
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    private static RebuildParameters? BuildRebuildParameters(string source, string format)
    {
        if (format == "Android Sparse Image (simg)")
        {
            using var sparse = SparseReader.FromImageFile(source);
            using var logical = new SparseReadStream(sparse);
            var inner = BuildRebuildParameters(logical, DetectFilesystem(logical));
            if (inner is null) return null;
            inner.InnerFormat = inner.Format;
            inner.Format = "Android Sparse Image (simg)";
            inner.SparseBlockSize = checked((int)sparse.Header.BlockSize);
            return inner;
        }

        using var stream = File.OpenRead(source);
        return BuildRebuildParameters(stream, format);
    }

    private static RebuildParameters? BuildRebuildParameters(Stream source, string format) => format switch
    {
        "EXT4" => ReadExt4Rebuild(source),
        "EROFS" => ReadErofsRebuild(source),
        "F2FS" => ReadF2fsRebuild(source),
        _ => null
    };

    private static RebuildParameters ReadExt4Rebuild(Stream source)
    {
        using var parser = new Ext4Parser(source);
        return new RebuildParameters
        {
            Format = "EXT4",
            BlockSize = checked((int)parser.BlockSize),
            InodeSize = parser.Superblock.InodeSize,
            Ext4Encoding = (parser.Superblock.FeatureIncompat & 0x20000) != 0 ? parser.Superblock.Encoding : null,
            Ext4EncodingFlags = parser.Superblock.EncodingFlags,
            Uuid = parser.SourceUuid,
            VolumeName = parser.SourceVolumeName,
            SourceLength = source.Length,
            SourceFilesystemBytes = parser.PartitionSize
        };
    }

    private static RebuildParameters ReadErofsRebuild(Stream source)
    {
        using var parser = OpenErofsParser(source, requireCompleteMetadata: true);
        var options = parser.ReadRebuildOptions();
        return new RebuildParameters
        {
            Format = "EROFS",
            BlockSize = options.BlockSize,
            Uuid = options.Uuid,
            VolumeName = options.VolumeName,
            SourceLength = source.CanSeek ? source.Length : null,
            SourceFilesystemBytes = checked((long)parser.Superblock.BlockCount64 * options.BlockSize),
            Erofs = ErofsRebuildMetadata.Capture(options)
        };
    }

    private static RebuildParameters ReadF2fsRebuild(Stream source)
    {
        using var parser = new F2fsParser(source);
        var algorithms = parser.ReadCompressionAlgorithms();
        return new RebuildParameters
        {
            Format = "F2FS",
            BlockSize = checked((int)parser.BlockSize),
            SegmentSize = parser.SegmentSize,
            Uuid = parser.SourceUuid,
            VolumeName = parser.SourceVolumeName,
            SourceLength = source.Length,
            SourceFilesystemBytes = checked((long)parser.Superblock.BlockCount * parser.BlockSize),
            F2fsCompressionEnabled = algorithms.Count > 0,
            F2fsCompressionAlgorithm = algorithms.Count == 1 ? algorithms.Single().ToString() : null
        };
    }

    /// <summary>
    /// Rebuilds the extraction tree using validated source metadata, without retaining the original image.
    /// An optional output limit bounds the emitted file extent before publication. For sparse output this
    /// is the container size, not the logical partition size.
    /// </summary>
    public static bool TryRestore(string inputDirectory, string outputPath, string format,
        CancellationToken cancellationToken = default,
        Action<IReadOnlyDictionary<string, string>>? rebuildReport = null,
        long? maximumOutputBytes = null,
        IReadOnlyList<FileSystemAttributeEdit>? attributeEdits = null,
        IReadOnlyList<FileSystemXattrEdit>? xattrEdits = null)
        => TryRestoreCore(inputDirectory, outputPath, format, cancellationToken, rebuildReport,
            maximumOutputBytes, attributeEdits, xattrEdits);

    private static bool TryRestoreCore(string inputDirectory, string outputPath, string format,
        CancellationToken cancellationToken, Action<IReadOnlyDictionary<string, string>>? rebuildReport,
        long? maximumOutputBytes, IReadOnlyList<FileSystemAttributeEdit>? attributeEdits,
        IReadOnlyList<FileSystemXattrEdit>? xattrEdits, string? snapshotDirectory = null)
    {
        if (maximumOutputBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        }
        var metadata = snapshotDirectory is null ? RepackMetadata.Read(inputDirectory)
            : RepackMetadata.Read(snapshotDirectory, RepackMetadataContext.LocalDirectory);
        if (metadata is null) return false;
        if (metadata.FormatName != format) throw new InvalidDataException("Filesystem metadata format does not match the requested image format.");
        if (format == "Android Sparse Image (simg)" && metadata.Configuration.Count == 0
            && metadata.FileSystemPreservationId is null && metadata.FileSystemPreservationHash is null
            && File.Exists(Path.Combine(inputDirectory, Path.GetFileNameWithoutExtension(metadata.SourceFileName) + ".raw")))
            return false;
        if (metadata.FileSystemPreservationId is not { Length: 32 } id || !Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("Filesystem preservation data is missing. Re-extract the source image before rebuilding it.");
        var metadataDirectory = snapshotDirectory ?? Path.GetDirectoryName(RepackMetadata.ResolveActualMetadataPath(inputDirectory))!;
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
        if (manifest.Version is not (1 or 2) || manifest.Format != format
            || manifest.Projection is null || manifest.Sidecars is null || manifest.Projection.Count > MaximumEntries)
            throw new InvalidDataException("Unsupported filesystem preservation manifest.");
        ValidateConfiguration(metadata, manifest);
        var packingOptions = new Dictionary<string, string>(metadata.PackingOptions, StringComparer.Ordinal);
        if (RepackMetadata.PackingOverrides is { } overrides)
        {
            foreach (var (key, value) in overrides)
            {
                packingOptions[key] = value;
            }
        }
        if (!Same(ReadSidecars(metadataDirectory, metadata.SourceFileName, id, cancellationToken,
                    includeReferenceFiles: manifest.Version == 1),
                manifest.Version == 1 ? manifest.Sidecars : StructuredSidecars(manifest.Sidecars)))
            throw new InvalidDataException("Filesystem configuration changed. The current writer cannot guarantee preservation of every source feature after this change.");
        var projection = Scan(inputDirectory, metadataDirectory, metadata.SourceFileName, cancellationToken);
        if (snapshotDirectory is not null)
        {
            ValidateSnapshotProjection(manifest, projection);
            // Host permissions can change across platforms; original inode permissions remain authoritative.
            manifest.Projection = projection;
        }
        var fullOutput = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var protectedRoot in new[] { inputDirectory, metadataDirectory })
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
            if (fullOutput.Equals(root, comparison) || fullOutput.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new IOException("Rebuilt output must be outside the extracted tree and its preservation data.");
        }
        var tables = FileSystemMetadataOverrides.LoadTables(metadataDirectory, metadata.SourceFileName, id,
            requireStructured: manifest.Version >= 2);
        if (manifest.Version >= 2)
        {
            ValidateInodeFlags(manifest.ParsedEntries, tables.Rebuild?.InnerFormat ?? format);
        }
        var plan = new FileSystemEditPlan(inputDirectory, manifest, projection, RepackMetadata.DetectPartitionName(metadata.SourceFileName));
        if ((format == "F2FS" || tables.Rebuild?.InnerFormat == "F2FS")
            && RepackMetadata.PackingOverrides is { } explicitOptions
            && explicitOptions.TryGetValue("EnableCompression", out var enabled))
        {
            var algorithm = ParseF2fsCompressionAlgorithm(packingOptions, tables.Rebuild);
            foreach (var entry in plan.Entries.Values.Where(entry => (entry.Mode & 0xF000) == 0x8000))
            {
                if (entry.Metadata is not { } inodeMetadata)
                {
                    continue;
                }
                entry.Metadata = inodeMetadata with
                {
                    F2fsCompression = IsTrue(enabled)
                        ? new F2fsInodeCompression(algorithm switch
                        {
                            CompressionAlgorithm.Lzo => 0,
                            CompressionAlgorithm.Lz4 => 1,
                            CompressionAlgorithm.Zstd => 2,
                            _ => throw new InvalidDataException("Unsupported F2FS compression algorithm.")
                        }, 4, checked((ushort)(IntegerOption(packingOptions, "CompressionLevel", 0) << 8)))
                        : null
                };
            }
        }
        if (xattrEdits is not null) { plan.ApplyUserXattrs(xattrEdits, cancellationToken); }
        var effectiveFsConfig = manifest.Version >= 2 ? new Dictionary<string, FsConfigEntry>(StringComparer.Ordinal)
            : new Dictionary<string, FsConfigEntry>(tables.FsConfig, StringComparer.Ordinal);
        if (attributeEdits is not null)
        {
            foreach (var (path, attributes) in plan.ApplyAttributes(attributeEdits, cancellationToken))
            {
                effectiveFsConfig[path] = attributes;
            }
        }
        using var editScope = plan.Enter();
        FileExtraction.WriteAtomic(fullOutput, output =>
        {
            using var bounded = maximumOutputBytes is { } capacity
                ? new CapacityWriteStream(output, capacity, cancellationToken) : null;
            BuildEdited(inputDirectory, (Stream?)bounded ?? output, format, effectiveFsConfig,
                manifest.Version >= 2 ? new Dictionary<string, string>(StringComparer.Ordinal) : tables.FileContexts, tables.Rebuild,
                packingOptions, output, Path.GetDirectoryName(fullOutput)!, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
            if (manifest.Version >= 2)
            {
                VerifyRebuilt(output, plan, cancellationToken);
            }
            var current = snapshotDirectory is null ? RepackMetadata.Read(inputDirectory)
                : RepackMetadata.Read(snapshotDirectory, RepackMetadataContext.LocalDirectory);
            if (current is null || HashConfiguration(current) != HashConfiguration(metadata)
                || current.FileSystemPreservationHash != metadata.FileSystemPreservationHash
                || !Same(ReadSidecars(metadataDirectory, metadata.SourceFileName, id, cancellationToken,
                            includeReferenceFiles: manifest.Version == 1),
                        manifest.Version == 1 ? manifest.Sidecars : StructuredSidecars(manifest.Sidecars))
                || !Same(Scan(inputDirectory, metadataDirectory, metadata.SourceFileName, cancellationToken), projection))
                throw new IOException("Filesystem content or attributes changed during rebuilding.");
        }, overwrite: true);
        if (rebuildReport is not null && format == "EROFS" && tables.Rebuild?.Erofs is { } erofsProfile)
            rebuildReport(BuildErofsReport(erofsProfile, tables.Rebuild, new FileInfo(fullOutput).Length));
        return true;
    }

    /// <summary>Describes the regenerated layout against the source filesystem size and the inherited codec profile.<para>描述相对源文件系统体积与继承编解码档案的重建布局。</para></summary>
    private static Dictionary<string, string> BuildErofsReport(ErofsRebuildParameters profile, RebuildParameters rebuild, long rebuiltLength)
    {
        var report = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RebuiltSize"] = rebuiltLength.ToString(CultureInfo.InvariantCulture),
            ["CompressionProfile"] = string.Join(", ", profile.Codecs
                .OrderBy(codec => codec.Algorithm, StringComparer.Ordinal)
                .Select(codec => $"{codec.Algorithm}:{codec.CompressionLevel}")),
            ["CompressionInference"] = string.Join("; ", profile.Codecs
                .OrderBy(codec => codec.Algorithm, StringComparer.Ordinal)
                .Select(codec => $"{codec.Algorithm}: inherited from the verified source image; encoder identity and original level remain unproven"))
        };
        // A partition image is normally padded past the end of its filesystem, so the rebuilt length is
        // only comparable to the source filesystem's own block count. Profiles written before that field
        // existed fall back to the padded image length and say so.
        // 分区镜像通常会被填充到超出其文件系统的长度，因此重建长度只能与源文件系统自身的块数比较。
        // 早于该字段写入的档案退回使用填充后的镜像长度，并明确说明。
        var paddedFallback = rebuild.SourceFilesystemBytes is not > 0;
        var target = paddedFallback ? rebuild.SourceLength : rebuild.SourceFilesystemBytes;
        if (target is > 0)
        {
            report["SourceFilesystemSize"] = target.Value.ToString(CultureInfo.InvariantCulture);
            report["FitsSourceFilesystemSize"] = (rebuiltLength <= target.Value).ToString();
            report["SizeExcess"] = Math.Max(0, rebuiltLength - target.Value).ToString(CultureInfo.InvariantCulture);
            if (paddedFallback)
            {
                report["SourceFilesystemSizeBasis"] = "padded image length (re-extract the source to record the filesystem block count)";
            }
        }

        return report;
    }

    private static void BuildEdited(string inputDirectory, Stream output, string format,
        IReadOnlyDictionary<string, FsConfigEntry> fsConfig, IReadOnlyDictionary<string, string> fileContexts,
        RebuildParameters? rebuild, IReadOnlyDictionary<string, string> options, FileStream publicationOutput,
        string temporaryDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (format != "Android Sparse Image (simg)"
            && options.TryGetValue("OutputSparseContainer", out var outerSparse)
            && IsTrue(outerSparse))
        {
            var nestedOptions = new Dictionary<string, string>(options, StringComparer.Ordinal)
            {
                ["OutputSparseContainer"] = "0"
            };
            var temporary = Path.Combine(temporaryDirectory, ".uotan-f2fs-raw-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var raw = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
            BuildEdited(inputDirectory, raw, format, fsConfig, fileContexts, rebuild, nestedOptions, publicationOutput,
                temporaryDirectory, token);
            raw.Flush(flushToDisk: true);
            raw.Position = 0;
            SparseImageConverter.ConvertRawToSparseValidated(raw, output, 4096, raw.Length, token, publicationOutput);
            return;
        }
        if (format == "Android Sparse Image (simg)")
        {
            if (rebuild is null || rebuild.InnerFormat is not string inner || rebuild.SparseBlockSize is not int sparseBlock)
                throw new InvalidDataException("Sparse image rebuild parameters are missing. Re-extract the source image before rebuilding it.");
            var temporary = Path.Combine(temporaryDirectory, ".uotan-edited-raw-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var raw = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
            BuildEdited(inputDirectory, raw, inner, fsConfig, fileContexts, rebuild, options, publicationOutput,
                temporaryDirectory, token);
            raw.Flush(flushToDisk: true);
            raw.Position = 0;
            SparseImageConverter.ConvertRawToSparseValidated(raw, output, (uint)sparseBlock, raw.Length, token,
                publicationOutput);
            return;
        }
        switch (format)
        {
            case "EXT4":
                {
                    var rebuildBlock = rebuild?.BlockSize ?? 4096;
                    if ((rebuildBlock & (rebuildBlock - 1)) != 0 || rebuildBlock is < 1024 or > 65536)
                        throw new InvalidDataException("Invalid EXT4 rebuild block size.");
                    var capacityPolicy = ParseExt4CapacityPolicy(options);
                    var blockSharingMode = ParseExt4BlockSharingMode(options);
                    var ext4Options = new Ext4Options
                    {
                        BlockSize = rebuildBlock,
                        InodeSize = rebuild?.InodeSize ?? 256,
                        CasefoldEncoding = rebuild?.Ext4Encoding,
                        CasefoldEncodingFlags = rebuild?.Ext4EncodingFlags ?? 0,
                        Uuid = rebuild?.Uuid,
                        VolumeName = options.GetValueOrDefault("VolumeName", rebuild?.VolumeName ?? "UOTANSTUDIO"),
                        EnableSparse = IsTrue(options.GetValueOrDefault("EnableSparse")),
                        TargetSizeBytes = ParseTargetSize(options),
                        CapacityMode = capacityPolicy == Ext4PreservedCapacityPolicy.Minimize
                            ? Ext4CapacityMode.Minimize
                            : Ext4CapacityMode.Default,
                        ReadOnly = IsTrue(options.GetValueOrDefault("ReadOnly")),
                        BlockSharingMode = blockSharingMode,
                        CancellationToken = token,
                    };
                    ext4Options.FsConfig = fsConfig;
                    ext4Options.FileContexts = fileContexts;
                    Ext4Builder.Build(inputDirectory, output, ext4Options);
                    break;
                }
            case "EROFS":
                {
                    var erofsOptions = rebuild?.Erofs is { } profile
                        ? ErofsRebuildMetadata.Restore(profile)
                        : new ErofsOptions();
                    if (rebuild?.BlockSize is { } blockSize)
                    {
                        erofsOptions.BlockSize = blockSize;
                    }

                    erofsOptions.Uuid = rebuild?.Uuid;
                    var volumeName = options.GetValueOrDefault("VolumeName", rebuild?.VolumeName ?? "erofs");
                    if (volumeName != rebuild?.VolumeName)
                    {
                        erofsOptions.SourceVolumeNameBytes = null;
                    }

                    erofsOptions.VolumeName = volumeName;
                    erofsOptions.FsConfig = fsConfig;
                    erofsOptions.FileContexts = fileContexts;
                    new ErofsBuilder().Build(inputDirectory, output, erofsOptions);
                    break;
                }
            case "F2FS":
                {
                    var f2fsOptions = new F2fsOptions
                    {
                        BlockSize = rebuild?.BlockSize ?? 4096,
                        SegmentSize = rebuild?.SegmentSize ?? 2L * 1024 * 1024,
                        Uuid = rebuild?.Uuid is { Length: 16 } uuid ? new Guid(uuid) : null,
                        VolumeName = options.GetValueOrDefault("VolumeName", rebuild?.VolumeName ?? "f2fs"),
                        EnableCompression = options.TryGetValue("EnableCompression", out var compression)
                            ? IsTrue(compression)
                            : rebuild?.F2fsCompressionEnabled ?? false,
                        CompressionAlgorithm = ParseF2fsCompressionAlgorithm(options, rebuild),
                        CompressionLevel = IntegerOption(options, "CompressionLevel", 0),
                        CancellationToken = token,
                    };
                    f2fsOptions.FsConfig = fsConfig;
                    f2fsOptions.FileContexts = fileContexts;
                    F2fsBuilder.Build(inputDirectory, output, f2fsOptions);
                    break;
                }
            default: throw new InvalidDataException("Editing this filesystem container is not supported.");
        }
    }

    private static long? ParseTargetSize(IReadOnlyDictionary<string, string> options)
    {
        if (!string.Equals(options.GetValueOrDefault("CapacityPolicy"), "Explicit", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = options.GetValueOrDefault("TargetSizeBytes");
        return string.IsNullOrEmpty(value)
            ? throw new InvalidDataException("EXT4 explicit capacity requires a target size in bytes.")
            : long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Ext4PreservedCapacityPolicy ParseExt4CapacityPolicy(IReadOnlyDictionary<string, string> options)
    {
        var value = options.GetValueOrDefault("CapacityPolicy", "Inherit");
        return Enum.TryParse<Ext4PreservedCapacityPolicy>(value, ignoreCase: true, out var policy)
            && Enum.IsDefined(policy)
                ? policy
                : throw new InvalidDataException($"Unsupported EXT4 capacity policy '{value}'.");
    }

    private static Ext4BlockSharingMode ParseExt4BlockSharingMode(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("BlockSharingMode", out var value))
        {
            return Ext4BlockSharingMode.Independent;
        }

        return Enum.TryParse<Ext4BlockSharingMode>(value, ignoreCase: true, out var mode)
            && Enum.IsDefined(mode)
                ? mode
                : throw new InvalidDataException($"Unsupported EXT4 block sharing mode '{value}'.");
    }

    private enum Ext4PreservedCapacityPolicy
    {
        Inherit,
        Explicit,
        Minimize,
    }

    private static CompressionAlgorithm ParseF2fsCompressionAlgorithm(
        IReadOnlyDictionary<string, string> options,
        RebuildParameters? rebuild)
    {
        var value = options.TryGetValue("CompressionAlgorithm", out var selected)
            ? selected
            : rebuild?.F2fsCompressionAlgorithm;
        if (value is null)
        {
            if (rebuild?.F2fsCompressionEnabled == true
                && FileSystemEditPlan.Current?.Entries.Values.Any(entry => entry.Metadata?.F2fsCompression is not null) != true)
            {
                throw new NotSupportedException(
                    "The source F2FS image uses multiple compression algorithms; edited rebuild requires an explicit algorithm override.");
            }

            return CompressionAlgorithm.Lz4;
        }

        return Enum.TryParse<CompressionAlgorithm>(value, ignoreCase: true, out var algorithm)
            && Enum.IsDefined(algorithm)
            ? algorithm
            : throw new InvalidDataException($"Unsupported F2FS compression algorithm '{value}'.");
    }

    private static int IntegerOption(IReadOnlyDictionary<string, string> options, string key, int fallback)
        => options.TryGetValue(key, out var value)
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;

    private static bool IsTrue(string? value)
        => value is "1" || bool.TryParse(value, out var enabled) && enabled;

    private static string DetectFilesystem(Stream stream)
    {
        var probe = new byte[2048];
        stream.Position = 0;
        stream.ReadExactly(probe);
        stream.Position = 0;
        if (BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(1080)) == 0xEF53) return "EXT4";
        return BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(1024)) switch
        {
            0xE0F5E1E2 => "EROFS",
            0xF2F52010 => "F2FS",
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

    private static Dictionary<string, string> StructuredSidecars(Dictionary<string, string> sidecars)
        => sidecars.Where(pair => pair.Key == "metadata.json").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static Dictionary<string, string> ReadSidecars(string directory, string source, string preservationId, CancellationToken token,
        bool includeReferenceFiles = true)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (includeReferenceFiles)
        {
            foreach (var suffix in new[] { "_fs_config", "_file_contexts", "_info" })
            {
                var name = RepackMetadata.DetectPartitionName(source) + suffix;
                var path = FileExtraction.ResolvePath(directory, name);
                if (File.Exists(path)) result.Add(name, HashFile(path, token));
            }
        }
        var metadata = FileExtraction.ResolvePath(directory, FolderName + "/" + preservationId + "/metadata.json");
        if (File.Exists(metadata)) result.Add("metadata.json", HashFile(metadata, token));
        return result;
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
                Path = e.Path,
                Size = e.Size,
                Inode = e.Inode,
                Flags = e.Flags,
                DeviceId = e.DeviceId,
                Mode = e.Mode,
                Uid = e.Uid,
                Gid = e.Gid,
                Metadata = e.Metadata,
                SymlinkTarget = (e.Mode & 0xF000) == 0xA000 ? ReadSymlink(output => parser.ReadFile(e.Path, output)) : null,
                Xattrs = e.Xattrs?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            }).ToList();
        }
        if (format == "EROFS")
        {
            using var parser = OpenErofsParser(source, requireCompleteMetadata: true);
            return parser.EnumerateEntries().Select(e => new PreservedInodeEntry
            {
                Path = e.Path,
                Size = e.Size,
                Inode = e.Nid,
                DeviceId = e.DeviceId,
                Mode = e.Mode,
                Uid = e.Uid,
                Gid = e.Gid,
                Metadata = new FileSystemInodeMetadata
                {
                    ModificationTime = new(checked((long)e.MTime), e.MTimeNanoseconds),
                    LinkCount = checked((uint)e.LinkCount)
                },
                SymlinkTarget = (e.Mode & 0xF000) == 0xA000 ? ReadSymlink(output => parser.ReadFile(e.Path, output)) : null,
                Xattrs = e.Xattrs?.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal)
            }).ToList();
        }
        if (format == "F2FS")
        {
            using var parser = new F2fsParser(source);
            return parser.ListEntriesWithRoot().Select(e => new PreservedInodeEntry
            {
                Path = e.Path,
                Size = e.Size,
                Inode = e.Inode,
                Flags = e.Flags,
                DeviceId = e.DeviceId,
                Mode = e.Mode,
                Uid = e.Uid,
                Gid = e.Gid,
                Metadata = e.Metadata,
                SymlinkTarget = (e.Mode & 0xF000) == 0xA000 ? ReadSymlink(output => parser.ReadFile(e.Path, output)) : null,
                Xattrs = e.Xattrs?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            }).ToList();
        }
        return [];
    }
}

internal sealed class PreservedInodeEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public ulong Inode { get; set; }
    public uint Flags { get; set; }
    public uint DeviceId { get; set; }
    public ushort Mode { get; set; }
    public uint Uid { get; set; }
    public uint Gid { get; set; }
    public string? SymlinkTarget { get; set; }
    public Dictionary<string, byte[]>? Xattrs { get; set; }
    public FileSystemInodeMetadata? Metadata { get; set; }
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
    public Dictionary<string, PreservedContentObject> ContentObjects { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record PreservedContentObject(string Hash, long Length);

[JsonSerializable(typeof(FileSystemPreservationManifest))]
internal partial class FileSystemPreservationJsonContext : JsonSerializerContext;