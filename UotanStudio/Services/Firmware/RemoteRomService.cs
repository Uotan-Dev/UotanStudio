using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.Infrastructure.Downloads;
using UotanStudio.Firmware.OtaDelta.Payload;

namespace UotanStudio.Services.Firmware;

/// <summary>Describes a ROM discovered at a remote URL.<para>描述在远端 URL 发现的 ROM。</para></summary>
/// <param name="Url">Remote URL of the ROM.<para>ROM 的远端 URL。</para></param>
/// <param name="FileName">Resolved file name.<para>解析出的文件名。</para></param>
/// <param name="Kind">Detected container kind.<para>检测到的容器类型。</para></param>
/// <param name="Entries">Listed container entries.<para>列出的容器条目。</para></param>
/// <param name="ContentLength">Total byte length, or null when unknown.<para>总字节长度，未知时为空。</para></param>
/// <param name="SupportsPartitionSelection">Whether partition selection is supported.<para>是否支持分区选择。</para></param>
public sealed record RemoteRomAnalysis(
    Uri Url,
    string FileName,
    ContainerKind Kind,
    IReadOnlyList<ContainerEntry> Entries,
    long? ContentLength,
    bool SupportsPartitionSelection);

/// <summary>Describes a successfully published remote incremental-payload merge.<para>描述成功发布的远端增量载荷合并结果。</para></summary>
/// <param name="PartitionNames">Merged partition names.<para>已合并的分区名称。</para></param>
/// <param name="OutputPaths">Published output paths.<para>已发布的输出路径。</para></param>
public sealed record RemotePayloadMergeResult(IReadOnlyList<string> PartitionNames, IReadOnlyList<string> OutputPaths);

/// <summary>Probes direct ROM URLs and downloads selected content using HTTP range requests.<para>探测直接 ROM 链接并以 HTTP 范围请求下载所选内容。</para></summary>
/// <param name="client">HTTP client used for requests.<para>用于请求的 HTTP 客户端。</para></param>
public sealed class RemoteRomService(HttpClient client)
{
    private const int ProbeBytes = 8 * 1024 * 1024;
    private const int InitialProbeBytes = 4096;
    private const int ZipTailBytes = 1024 * 1024;
    private const int PayloadManifestProbeBytes = 1024 * 1024;
    private const int ParallelDownloadThreshold = 8 * 1024 * 1024;
    private const int ParallelChunkBytes = 16 * 1024 * 1024;
    private const int ParallelWorkers = 8;
    private const int TransferBufferBytes = 1024 * 1024;
    private static readonly FrozenSet<string> ImageExtensions =
        FrozenSet.ToFrozenSet([".img", ".raw", ".ext4", ".erofs", ".f2fs", ".simg", ".sparse"], StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _client = ConfigureClient(client);

    private static HttpClient ConfigureClient(HttpClient value)
    {
        if (!value.DefaultRequestHeaders.UserAgent.Any())
            value.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        if (!value.DefaultRequestHeaders.Accept.Any())
            value.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        if (!value.DefaultRequestHeaders.AcceptLanguage.Any())
            value.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return value;
    }

    public async Task<RemoteRomAnalysis> AnalyzeAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new UriFormatException("The ROM URL must be an absolute HTTP or HTTPS URL.");

        using var response = await SendRangeAsync(uri, 0, InitialProbeBytes - 1, cancellationToken).ConfigureAwait(false);
        await using var probeStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var probe = new MemoryStream(capacity: ProbeBytes);
        var buffer = new byte[128 * 1024];
        while (probe.Length < ProbeBytes)
        {
            var read = await probeStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, ProbeBytes - probe.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            probe.Write(buffer, 0, read);
        }
        var bytes = probe.ToArray();
        var length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        var fileName = GetFileName(uri, response.Content.Headers.ContentDisposition);

        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("CrAU"u8))
        {
            var payloadBytes = bytes.Length < PayloadManifestProbeBytes
                ? await ReadRangeAsync(uri, 0, PayloadManifestProbeBytes, cancellationToken).ConfigureAwait(false)
                : bytes;
            var metadata = PayloadParser.GetPayloadMetadata(new MemoryStream(payloadBytes, writable: false));
            var entries = metadata.Manifest.Partitions
                .Where(p => !string.IsNullOrWhiteSpace(p.PartitionName))
                .Select(p => new ContainerEntry(p.PartitionName, "payload", PayloadParser.GetPartitionSize(p, metadata.BlockSize), true))
                .ToArray();
            return new(uri, fileName, ContainerKind.Payload, entries, length, true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B)
        {
            var details = await ReadZipEntryDetailsAsync(uri, length, cancellationToken).ConfigureAwait(false);
            var entries = details.Values.Where(e => !e.IsDirectory && !e.Name.EndsWith("payload.bin", StringComparison.OrdinalIgnoreCase)
                    && ImageExtensions.Contains(Path.GetExtension(e.Name)))
                .Select(e => new ContainerEntry(e.Name, Path.GetExtension(e.Name).TrimStart('.'), checked((long)e.UncompressedSize), false)).ToList();
            var payload = details.Values.FirstOrDefault(e => e.Name.EndsWith("payload.bin", StringComparison.OrdinalIgnoreCase) && e.Method == 0);
            if (payload is not null)
            {
                var payloadOffset = await GetZipDataOffsetAsync(uri, payload, cancellationToken).ConfigureAwait(false);
                var manifestBytes = await ReadRangeAsync(uri, payloadOffset, PayloadManifestProbeBytes, cancellationToken).ConfigureAwait(false);
                try
                {
                    var metadata = PayloadParser.GetPayloadMetadata(new MemoryStream(manifestBytes, writable: false));
                    entries.AddRange(metadata.Manifest.Partitions.Where(p => !string.IsNullOrWhiteSpace(p.PartitionName)).Select(p =>
                        new ContainerEntry(p.PartitionName, "payload", PayloadParser.GetPartitionSize(p, metadata.BlockSize), true)
                        { PayloadMemberPath = payload.Name }));
                }
                catch { }
            }
            if (entries.Count > 0) return new(uri, fileName, ContainerKind.Zip, entries, length, true);
        }
        else if (LooksLikeSuper(bytes) && length is not null and <= 512L * 1024 * 1024)
        {
            var temp = await DownloadToTempAsync(uri, fileName, cancellationToken).ConfigureAwait(false);
            try { var structure = ContainerStructureAnalyzer.Analyze(temp); if (structure.Kind != ContainerKind.None) return new(uri, fileName, structure.Kind, structure.Entries, length, true); }
            finally { TryDelete(temp); }
        }

        return new(uri, fileName, ContainerKind.None, [], length, false);
    }

    public async Task<string> DownloadAsync(Uri uri, string destination, IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        await HttpDownload.DownloadAsync(_client, uri, destination, progress, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    /// <summary>Extracts selected raw-payload partitions with range reads, one partition at a time.<para>以范围读取逐个提取选定的原始载荷分区。</para></summary>
    /// <param name="uri">Remote payload URL.<para>远端载荷 URL。</para></param>
    /// <param name="partitionNames">Partitions to extract.<para>待提取的分区。</para></param>
    /// <param name="outputDirectory">Directory receiving extracted images.<para>接收提取镜像的目录。</para></param>
    /// <param name="baseOffset">Byte offset of the payload within the resource.<para>载荷在资源内的字节偏移。</para></param>
    /// <param name="progress">Optional byte progress reporter.<para>可选的字节进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    public async Task ExtractPayloadPartitionsAsync(Uri uri, IReadOnlyList<string> partitionNames, string outputDirectory,
        long baseOffset = 0,
        IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var stream = new HttpRangeStream(_client, uri, baseOffset);
        foreach (var partition in partitionNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PayloadParser.ExtractAsync(stream, [partition], outputDirectory, cancellationToken, byteProgress: progress).ConfigureAwait(false);
            stream.Seek(0, SeekOrigin.Begin);
        }
    }

    /// <summary>Applies selected partitions from a remote raw Payload against mapped local base images.<para>将远端原始载荷的选定分区应用到已映射的本地基础镜像。</para></summary>
    /// <param name="uri">Remote payload URL.<para>远端载荷 URL。</para></param>
    /// <param name="partitionNames">Partitions to apply.<para>待应用的分区。</para></param>
    /// <param name="sourceImages">Partition-to-base-image mapping.<para>分区到基础镜像的映射。</para></param>
    /// <param name="outputDirectory">Directory receiving published images.<para>接收发布镜像的目录。</para></param>
    /// <param name="baseOffset">Byte offset of the payload within the resource.<para>载荷在资源内的字节偏移。</para></param>
    /// <param name="progress">Optional byte progress reporter.<para>可选的字节进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The published merge result.<para>已发布的合并结果。</para></returns>
    public async Task<RemotePayloadMergeResult> MergeIncrementalPayloadAsync(
        Uri uri,
        IReadOnlyList<string> partitionNames,
        IReadOnlyDictionary<string, string> sourceImages,
        string outputDirectory,
        long baseOffset = 0,
        IProgress<(long Processed, long Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(partitionNames);
        ArgumentNullException.ThrowIfNull(sourceImages);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (baseOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseOffset));
        }

        var selected = partitionNames
            .Select(name => name?.Trim() ?? string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0)
        {
            throw new ArgumentException("At least one partition must be selected.", nameof(partitionNames));
        }

        var normalizedSources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (partition, sourcePath) in sourceImages)
        {
            if (string.IsNullOrWhiteSpace(partition) || string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source-image mappings require non-empty partition names and paths.", nameof(sourceImages));
            }

            var fullPath = Path.GetFullPath(sourcePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Source image for partition '{partition}' was not found.", fullPath);
            }
            normalizedSources[partition] = fullPath;
        }

        var outputRoot = Path.GetFullPath(outputDirectory);
        var parent = Directory.GetParent(outputRoot)?.FullName
            ?? throw new InvalidOperationException("The output directory must have a parent directory.");
        Directory.CreateDirectory(parent);
        var transactionId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.payload-{transactionId}.staging");
        var backup = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.payload-{transactionId}.backup");
        Directory.CreateDirectory(staging);

        try
        {
            await using var stream = new HttpRangeStream(_client, uri, baseOffset);
            foreach (var partition in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PayloadParser.ExtractAsync(stream, [partition], staging, cancellationToken,
                    sourceImages: normalizedSources, byteProgress: progress).ConfigureAwait(false);
                stream.Seek(0, SeekOrigin.Begin);

                var stagedPath = GetPartitionImagePath(staging, partition);
                if (!File.Exists(stagedPath))
                {
                    throw new InvalidDataException($"Payload did not produce the selected partition '{partition}'.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputRoot);
            return PublishMergedPartitions(selected, staging, backup, outputRoot, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteEmptyDirectory(backup);
        }
    }

    public async Task DownloadZipEntriesAsync(Uri uri, IReadOnlyList<string> names, string outputDirectory,
        IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var length = await GetLengthAsync(uri, cancellationToken).ConfigureAwait(false);
        var entries = await ReadZipEntryDetailsAsync(uri, length, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory);
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(name, out var entry)) throw new FileNotFoundException(name);
            var local = await ReadRangeAsync(uri, entry.LocalHeaderOffset, 30, cancellationToken).ConfigureAwait(false);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28, 2));
            var dataOffset = checked(entry.LocalHeaderOffset + 30L + nameLength + extraLength);
            var compressed = await ReadRangeAsync(uri, dataOffset, checked((int)entry.CompressedSize), cancellationToken).ConfigureAwait(false);
            await using var source = new MemoryStream(compressed, writable: false);
            await using Stream content = entry.Method == 8 ? new DeflateStream(source, CompressionMode.Decompress) : source;
            var destination = Path.Combine(outputDirectory, Path.GetFileName(name.Replace('/', Path.DirectorySeparatorChar)));
            await using var output = File.Create(destination);
            await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            progress?.Report((entry.UncompressedSize, entry.UncompressedSize));
        }
    }

    public async Task ExtractZipPayloadPartitionsAsync(Uri uri, IReadOnlyList<string> partitionNames, string outputDirectory,
        IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var length = await GetLengthAsync(uri, cancellationToken).ConfigureAwait(false);
        var entries = await ReadZipEntryDetailsAsync(uri, length, cancellationToken).ConfigureAwait(false);
        var payload = entries.Values.FirstOrDefault(e => e.Name.EndsWith("payload.bin", StringComparison.OrdinalIgnoreCase) && e.Method == 0)
            ?? throw new InvalidOperationException("The remote ZIP has no range-readable payload.bin entry.");
        var offset = await GetZipDataOffsetAsync(uri, payload, cancellationToken).ConfigureAwait(false);
        await ExtractPayloadPartitionsAsync(uri, partitionNames, outputDirectory, offset, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies selected partitions from a stored payload.bin inside a remote ZIP.<para>应用远端 ZIP 内已存储的 payload.bin 的选定分区。</para></summary>
    /// <param name="uri">Remote ZIP URL.<para>远端 ZIP 的 URL。</para></param>
    /// <param name="partitionNames">Partitions to apply.<para>待应用的分区。</para></param>
    /// <param name="sourceImages">Partition-to-base-image mapping.<para>分区到基础镜像的映射。</para></param>
    /// <param name="outputDirectory">Directory receiving published images.<para>接收发布镜像的目录。</para></param>
    /// <param name="progress">Optional byte progress reporter.<para>可选的字节进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The published merge result.<para>已发布的合并结果。</para></returns>
    public async Task<RemotePayloadMergeResult> MergeZipIncrementalPayloadAsync(
        Uri uri,
        IReadOnlyList<string> partitionNames,
        IReadOnlyDictionary<string, string> sourceImages,
        string outputDirectory,
        IProgress<(long Processed, long Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var length = await GetLengthAsync(uri, cancellationToken).ConfigureAwait(false);
        var entries = await ReadZipEntryDetailsAsync(uri, length, cancellationToken).ConfigureAwait(false);
        var payload = entries.Values.FirstOrDefault(entry =>
                entry.Name.EndsWith("payload.bin", StringComparison.OrdinalIgnoreCase) && entry.Method == 0)
            ?? throw new InvalidOperationException("The remote ZIP has no range-readable payload.bin entry.");
        var offset = await GetZipDataOffsetAsync(uri, payload, cancellationToken).ConfigureAwait(false);
        return await MergeIncrementalPayloadAsync(uri, partitionNames, sourceImages, outputDirectory, offset, progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadToTempAsync(Uri uri, string fileName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-remote-{Guid.NewGuid():N}-{fileName}");
        try { return await DownloadAsync(uri, path, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch { TryDelete(path); throw; }
    }

    private async Task<HttpResponseMessage> SendRangeAsync(Uri uri, long from, long to, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri);
        request.Headers.Range = new RangeHeaderValue(from, to);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        return request;
    }

    private static bool LooksLikeSuper(byte[] bytes) => bytes.Length >= 4100
        && bytes[4096] == (byte)'L' && bytes[4097] == (byte)'P' && bytes[4098] == (byte)'0' && bytes[4099] == (byte)'\0';

    private static string GetFileName(Uri uri, ContentDispositionHeaderValue? disposition)
    {
        var value = disposition?.FileNameStar ?? disposition?.FileName;
        if (!string.IsNullOrWhiteSpace(value)) return Path.GetFileName(value.Trim('"'));
        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? "remote-rom.bin" : name;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private static RemotePayloadMergeResult PublishMergedPartitions(
        IReadOnlyList<string> partitionNames,
        string stagingDirectory,
        string backupDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        List<(string Target, string? Backup)> published = [];
        var outputPaths = new List<string>(partitionNames.Count);
        try
        {
            foreach (var partition in partitionNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staged = GetPartitionImagePath(stagingDirectory, partition);
                var target = GetPartitionImagePath(outputDirectory, partition);
                string? saved = null;
                if (File.Exists(target))
                {
                    Directory.CreateDirectory(backupDirectory);
                    saved = GetPartitionImagePath(backupDirectory, partition);
                    File.Move(target, saved, overwrite: false);
                }

                try
                {
                    File.Move(staged, target, overwrite: false);
                }
                catch
                {
                    if (saved is not null)
                    {
                        File.Move(saved, target, overwrite: false);
                    }
                    throw;
                }

                published.Add((target, saved));
                outputPaths.Add(target);
            }
        }
        catch (Exception publicationError)
        {
            List<Exception>? rollbackErrors = null;
            for (var i = published.Count - 1; i >= 0; i--)
            {
                var (target, saved) = published[i];
                try
                {
                    File.Delete(target);
                    if (saved is not null)
                    {
                        File.Move(saved, target, overwrite: false);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors ??= [];
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors is not null)
            {
                rollbackErrors.Insert(0, publicationError);
                throw new AggregateException("Payload publication failed and rollback was incomplete.", rollbackErrors);
            }
            throw;
        }

        TryDeleteDirectory(backupDirectory);
        return new RemotePayloadMergeResult([.. partitionNames], outputPaths);
    }

    private static string GetPartitionImagePath(string directory, string partition)
    {
        if (Path.IsPathRooted(partition) || partition.Contains('/') || partition.Contains('\\')
            || partition.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid partition name '{partition}'.");
        }

        var root = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(Path.Combine(root, partition + ".img"));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException($"Partition path escapes the output directory: '{partition}'.");
        }
        return candidate;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private async Task<long?> GetLengthAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await SendRangeAsync(uri, 0, 0, cancellationToken).ConfigureAwait(false);
        return response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
    }

    private async Task<byte[]> ReadRangeAsync(Uri uri, long start, int count, CancellationToken cancellationToken)
    {
        using var response = await SendRangeAsync(uri, start, checked(start + count - 1), cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ContainerEntry>> ReadZipEntriesAsync(Uri uri, long? length, CancellationToken cancellationToken)
    {
        var details = await ReadZipEntryDetailsAsync(uri, length, cancellationToken).ConfigureAwait(false);
        return [.. details.Values.Where(e => !e.IsDirectory && (e.Name.EndsWith("payload.bin", StringComparison.OrdinalIgnoreCase)
                || ImageExtensions.Contains(Path.GetExtension(e.Name))))
            .Select(e => new ContainerEntry(e.Name, Path.GetExtension(e.Name).TrimStart('.'), checked((long)e.UncompressedSize), e.Name.EndsWith("payload.bin", StringComparison.OrdinalIgnoreCase)))];
    }

    private async Task<Dictionary<string, ZipEntryDetail>> ReadZipEntryDetailsAsync(Uri uri, long? length, CancellationToken cancellationToken)
    {
        if (length is null or <= 22) return [];
        var tailSize = (int)Math.Min(ZipTailBytes, length.Value);
        var tail = await ReadRangeAsync(uri, length.Value - tailSize, tailSize, cancellationToken).ConfigureAwait(false);
        var eocd = -1;
        for (var i = tail.Length - 22; i >= 0; i--) if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) == 0x06054B50) { eocd = i; break; }
        if (eocd < 0) return [];
        long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10, 2));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12, 4));
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16, 4));
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            var locator = -1;
            for (var i = eocd - 20; i >= 0; i--)
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) == 0x07064B50) { locator = i; break; }
            if (locator < 0) return [];
            var recordOffset = BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locator + 8, 8));
            var record = await ReadRangeAsync(uri, checked((long)recordOffset), 56, cancellationToken).ConfigureAwait(false);
            count = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32, 8)));
            size = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40, 8)));
            offset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48, 8)));
        }
        if (size > 32 * 1024 * 1024 || count > 65536 || offset < 0) return [];
        var central = await ReadRangeAsync(uri, offset, checked((int)size), cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, ZipEntryDetail>(StringComparer.OrdinalIgnoreCase);
        var pos = 0;
        for (var i = 0; i < count && pos + 46 <= central.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(pos, 4)) != 0x02014B50) break;
            var method = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(pos + 10, 2));
            var compressed32 = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(pos + 20, 4));
            var uncompressed32 = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(pos + 24, 4));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(pos + 28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(pos + 30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(pos + 32, 2));
            var localOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(pos + 42, 4));
            var recordLength = checked(46 + nameLength + extraLength + commentLength);
            if (pos + recordLength > central.Length) break;
            var name = Encoding.UTF8.GetString(central, pos + 46, nameLength);
            var extra = central.AsSpan(pos + 46 + nameLength, extraLength);
            if (!TryReadZip64Values(extra, compressed32, uncompressed32, localOffset32,
                    out var compressed, out var uncompressed, out var localOffset))
                return [];
            result[name] = new(name, method, compressed, uncompressed, localOffset, name.EndsWith('/'));
            pos += recordLength;
        }
        return result;
    }

    private static bool TryReadZip64Values(ReadOnlySpan<byte> extra, uint compressed32, uint uncompressed32, uint localOffset32,
        out long compressed, out long uncompressed, out long localOffset)
    {
        compressed = compressed32;
        uncompressed = uncompressed32;
        localOffset = localOffset32;
        if (compressed32 != uint.MaxValue && uncompressed32 != uint.MaxValue && localOffset32 != uint.MaxValue)
            return true;

        var cursor = 0;
        while (cursor + 4 <= extra.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra[cursor..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(cursor + 2)..]);
            cursor += 4;
            if (cursor + size > extra.Length) return false;
            if (id == 0x0001)
            {
                var values = extra.Slice(cursor, size);
                var valueOffset = 0;
                if (uncompressed32 == uint.MaxValue)
                {
                    if (valueOffset + 8 > values.Length) return false;
                    uncompressed = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(values[valueOffset..]));
                    valueOffset += 8;
                }
                if (compressed32 == uint.MaxValue)
                {
                    if (valueOffset + 8 > values.Length) return false;
                    compressed = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(values[valueOffset..]));
                    valueOffset += 8;
                }
                if (localOffset32 == uint.MaxValue)
                {
                    if (valueOffset + 8 > values.Length) return false;
                    localOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(values[valueOffset..]));
                }
                return true;
            }
            cursor += size;
        }
        return false;
    }

    private async Task<long> GetZipDataOffsetAsync(Uri uri, ZipEntryDetail entry, CancellationToken cancellationToken)
    {
        var local = await ReadRangeAsync(uri, entry.LocalHeaderOffset, 30, cancellationToken).ConfigureAwait(false);
        return checked(entry.LocalHeaderOffset + 30L + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26, 2)) + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28, 2)));
    }

    private sealed record ZipEntryDetail(string Name, ushort Method, long CompressedSize, long UncompressedSize, long LocalHeaderOffset, bool IsDirectory);

    private sealed class HttpRangeStream(HttpClient client, Uri uri, long baseOffset = 0) : Stream
    {
        private readonly HttpClient _client = client;
        private readonly Uri _uri = uri;
        private readonly long _baseOffset = baseOffset;
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override int Read(Span<byte> buffer)
        {
            var temporary = new byte[buffer.Length];
            var read = ReadAsync(temporary.AsMemory()).GetAwaiter().GetResult();
            temporary.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            using var request = CreateRequest(_uri);
            request.Headers.Range = new RangeHeaderValue(checked(_baseOffset + _position), checked(_baseOffset + _position + buffer.Length - 1));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent && _position > 0)
                throw new InvalidOperationException("The remote server does not support HTTP range requests.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await input.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => throw new NotSupportedException(),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}