using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>
/// Retains source semantics and resolves edited host files into filesystem builders with original or inherited Linux attributes.
/// <para>保留源镜像语义，并把编辑后的宿主文件树解析为使用原始或继承 Linux 属性的文件系统构建器。</para>
/// </summary>
public static partial class FileSystemPreservation
{
    private const int MaximumEntries = 100000;
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
        if (metadataPath is not null && RepackMetadata.IsMetadataFile(metadataPath)
            && RepackMetadata.Read(directory)?.FormatName is "EXT4" or "EROFS" or "F2FS" or "Android Sparse Image (simg)")
            throw new FileSystemUnsupportedFeatureException("Use the filesystem format handler for an extracted image; a fresh directory build would discard preserved source semantics.");
    }

    /// <summary>
    /// Reads the filesystem preservation payload carried by a metadata instance. The JSON tree is
    /// materialized on demand, so callers that only need the outer metadata never pay for it.
    /// <para>读取元数据实例携带的文件系统保留负载。JSON 树按需物化，只关心外层元数据的调用方无需承担开销。</para>
    /// </summary>
    /// <param name="metadata">The parsed metadata instance. <para>已读取的元数据实例。</para></param>
    /// <returns>
    /// The typed payload, or <see langword="null"/> when the format recorded none or the tree is invalid.
    /// <para>类型化负载；格式未记录或 JSON 树非法时返回 <see langword="null"/>。</para>
    /// </returns>
    internal static FileSystemPreservationData? ReadPayload(RepackMetadata? metadata)
    {
        if (metadata?.FileSystemPreservation is not { } payload) return null;
        FileSystemPreservationData? data;
        try
        {
            data = payload.Deserialize(FileSystemPreservationJsonContext.Default.FileSystemPreservationData);
        }
        catch (JsonException)
        {
            return null;
        }

        return data is null || data.Version != 1 || data.PartitionInfo is null || data.FsConfig is null
            || data.FileContexts is null || data.Projection is null || data.ParsedEntries is null
            || string.IsNullOrWhiteSpace(data.Partition)
            ? null
            : data;
    }

    /// <summary>
    /// Captures the preservation payload of an extracted filesystem image: rebuild parameters and inode entries
    /// are read back from the source image itself, and the capture is verified against it.
    /// <para>采集已解包文件系统镜像的保留负载：重建参数与 inode 条目都从源镜像本身读回，并据此校验采集结果。</para>
    /// </summary>
    /// <param name="source">Source image file the extraction was taken from.
    /// <para>解包所依据的源镜像文件。</para></param>
    /// <param name="extraction">Extraction output tree that is projected. <para>被投影的解包输出目录树。</para></param>
    /// <param name="metadataDirectory">Directory receiving <c>metadata.json</c>.
    /// <para>接收 <c>metadata.json</c> 的目录。</para></param>
    /// <param name="metadata">Metadata instance the payload is attached to.
    /// <para>负载所附加到的元数据实例。</para></param>
    /// <param name="cancellationToken">Cancels hashing, projection and verification.
    /// <para>取消摘要计算、投影与校验。</para></param>
    internal static void CaptureAsync(string source, string extraction, string metadataDirectory,
        RepackMetadata metadata, CancellationToken cancellationToken)
    {
        var rebuild = BuildRebuildParameters(source, metadata.FormatName, cancellationToken);
        var parsedEntries = ReadEntries(source, metadata.FormatName);
        ValidateInodeFlags(parsedEntries, rebuild?.InnerFormat ?? metadata.FormatName);
        CapturePayload(source, extraction, metadataDirectory, metadata, rebuild, parsedEntries,
            verifyContents: true, cancellationToken);
    }

    /// <summary>
    /// Captures the preservation payload of an extracted archive container, whose per-entry ownership, group and
    /// permission bits are supplied by the caller from the archive headers rather than from a filesystem parser.
    /// No rebuild parameters exist for a container, and its contents are not re-read from the source image here:
    /// the projected host tree and the recorded header table are the preserved record.
    /// <para>为已解包的归档容器采集保留负载：每个条目的属主、属组与权限位由调用方从归档头提供，而非来自文件系统
    /// 解析器。容器没有重建参数，其内容也不在此从源镜像重新读取：宿主投影与记录的条目表就是保留记录。</para>
    /// </summary>
    /// <param name="source">Source archive file the extraction was taken from.
    /// <para>解包所依据的源归档文件。</para></param>
    /// <param name="extraction">Extraction output tree that is projected.
    /// <para>被投影的解包输出目录树。</para></param>
    /// <param name="metadataDirectory">Directory receiving <c>metadata.json</c>.
    /// <para>接收 <c>metadata.json</c> 的目录。</para></param>
    /// <param name="metadata">Metadata instance the payload is attached to.
    /// <para>负载所附加到的元数据实例。</para></param>
    /// <param name="entries">Per-entry POSIX attributes read from the archive headers.
    /// <para>从归档头读出的每条目 POSIX 属性。</para></param>
    /// <param name="cancellationToken">Cancels hashing and projection. <para>取消摘要计算与投影。</para></param>
    internal static void CaptureArchivePayload(string source, string extraction, string metadataDirectory,
        RepackMetadata metadata, IReadOnlyList<PreservedInodeEntry> entries, CancellationToken cancellationToken)
        => CapturePayload(source, extraction, metadataDirectory, metadata, null, entries,
            verifyContents: false, cancellationToken);

    private static void CapturePayload(string source, string extraction, string metadataDirectory,
        RepackMetadata metadata, RebuildParameters? rebuild, IReadOnlyList<PreservedInodeEntry> parsedEntries,
        bool verifyContents, CancellationToken cancellationToken)
    {
        var imageBytes = new ExtractionByteCounter(new FileInfo(source).Length,
            FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.VerifyingImage), cancellationToken);
        var sourceHash = HashFile(source, cancellationToken, imageBytes.Add);
        imageBytes.Complete();
        var data = new FileSystemPreservationData
        {
            Version = 1,
            Format = metadata.FormatName,
            Partition = RepackMetadata.DetectPartitionName(metadata.SourceFileName),
            SourceLength = new FileInfo(source).Length,
            SourceHash = sourceHash,
            ConfigurationJson = JsonSerializer.Serialize(metadata, RepackJsonContext.Default.RepackMetadata),
            Rebuild = rebuild,
            PartitionInfo = metadata.Configuration["superblock"] is JsonObject superblock
                ? superblock.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty,
                    StringComparer.Ordinal)
                : new(StringComparer.Ordinal),
            FsConfig = parsedEntries.ToDictionary(entry => FileSystemEditPlan.Normalize(entry.Path),
                entry => new FsConfigEntry(entry.Uid, entry.Gid, (ushort)(entry.Mode & 0xFFF)), StringComparer.Ordinal),
            FileContexts = parsedEntries.Where(entry => entry.Xattrs?.ContainsKey("security.selinux") == true)
                .ToDictionary(entry => FileSystemEditPlan.Normalize(entry.Path),
                    entry => Encoding.UTF8.GetStringTrimNul(entry.Xattrs!["security.selinux"]), StringComparer.Ordinal),
            ImagePaths = CapturedPaths.Value is { } paths ? new(paths, StringComparer.Ordinal) : [],
            ParsedEntries = [.. parsedEntries],
            Projection = Scan(extraction, metadataDirectory, metadata.SourceFileName, cancellationToken,
                ActiveProgress.Value is null ? null : FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.VerifyingFiles))
        };
        if (verifyContents)
        {
            VerifyCapturedContents(source, data, cancellationToken);
        }
        var payload = JsonSerializer.SerializeToNode(data,
            FileSystemPreservationJsonContext.Default.FileSystemPreservationData)!.AsObject();
        metadata.FileSystemPreservation = payload;
        payload["configurationHash"] = HashConfiguration(metadata);
    }

    private static bool Same(Dictionary<string, string> actual, Dictionary<string, string> expected)
        => actual.Count == expected.Count && actual.All(pair => expected.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>
    /// Hashes the metadata document with the preservation payload's own digest removed, so the digest
    /// covers the payload content and can be compared both at capture and at read time.
    /// <para>对移除保留负载自身摘要后的元数据文档计算哈希，使摘要覆盖负载内容，并可在采集与读取两侧比对。</para>
    /// </summary>
    internal static string HashConfiguration(RepackMetadata metadata)
    {
        var json = JsonSerializer.SerializeToNode(metadata, RepackJsonContext.Default.RepackMetadata)!.AsObject();
        if (json["fileSystemPreservation"] is JsonObject payload) payload.Remove("configurationHash");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToJsonString())));
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
                    && (RepackMetadata.IsMetadataFile(path)
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