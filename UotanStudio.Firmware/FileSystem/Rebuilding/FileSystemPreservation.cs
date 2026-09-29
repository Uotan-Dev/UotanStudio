using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Extraction;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>
/// Retains source semantics and resolves edited host files into filesystem builders with original or inherited Linux attributes.
/// <para>保留源镜像语义，并把编辑后的宿主文件树解析为使用原始或继承 Linux 属性的文件系统构建器。</para>
/// </summary>
public static partial class FileSystemPreservation
{
    private const string FolderName = ".filesystem";
    private const int MaximumEntries = 100000;
    private const long MaximumManifestBytes = 32 * 1024 * 1024;
    private static readonly AsyncLocal<Dictionary<string, string>?> CapturedPaths = new();
    private static readonly AsyncLocal<IReadOnlyList<string>?> FrozenErofsDevices = new();
    private static readonly AsyncLocal<IProgress<FileSystemByteProgress>?> ActiveProgress = new();

    /// <summary>
    /// Opens the frozen EROFS device set for the current preservation operation.
    /// <para>为当前保留操作打开已冻结的 EROFS 设备集。</para>
    /// </summary>
    /// <param name="source">Main EROFS image stream; ownership transfers to the returned parser.
    /// <para>主 EROFS 镜像流；所有权转移给返回的解析器。</para></param>
    /// <param name="token">Cancels parser reads; <para>取消解析器读取。</para></param>
    /// <param name="requireCompleteMetadata">When <see langword="true"/>, the parser demands complete metadata coverage.
    /// <para>为 <see langword="true"/> 时，解析器要求元数据完整覆盖。</para></param>
    /// <returns>A parser over <paramref name="source"/> alone, or over the source plus the frozen external device
    /// files captured by the active <see cref="UnpackAsync"/> operation when one is in scope on this async flow.
    /// <para>仅基于 <paramref name="source"/> 的解析器；若当前异步流上存在活动的 <see cref="UnpackAsync"/> 操作，
    /// 则返回基于源镜像及其冻结外部设备文件的解析器。</para></returns>
    /// <exception cref="IOException">A frozen external device file cannot be opened; already-opened device streams are disposed.
    /// <para>无法打开冻结的外部设备文件；已打开的设备流会被释放。</para></exception>
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
            throw new FileSystemUnsupportedFeatureException("Use the filesystem format handler for an extracted image; a fresh directory build would discard preserved source semantics.");
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
                if (result.Count > MaximumEntries) throw new FileSystemCorruptedException("Filesystem projection exceeds 100000 entries.");
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
}