using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Extraction;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.IO;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Models;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Firmware.SharedKernel;
using SharpZipCompressionMethod = ICSharpCode.SharpZipLib.Zip.CompressionMethod;
using SharpZipZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser;

public static partial class PayloadParser
{
    #region File Type Detection

    private static readonly byte[] ZipMagicBytes = [0x50, 0x4B];
    private static readonly byte[] PayloadMagicBytes = [0x43, 0x72, 0x41, 0x55];

    /// <summary>
    /// Determines the payload type of a local file by reading its magic bytes.
    /// <para>通过读取魔术字节确定本地文件的载荷类型。</para>
    /// </summary>
    internal static PayloadType DetectPayloadType(string filepath)
    {
        using var f = File.OpenRead(filepath);
        return DetectPayloadTypeFromStream(f);
    }

    /// <summary>
    /// Determines the payload type from a stream by reading its magic bytes.
    /// <para>通过读取魔术字节从流中确定载荷类型。</para>
    /// </summary>
    internal static PayloadType DetectPayloadTypeFromStream(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        var bytesRead = stream.Read(header);
        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        if (bytesRead < 2)
        {
            return PayloadType.Unknown;
        }

        ReadOnlySpan<byte> span = header[..bytesRead];
        return span[..2].SequenceEqual(ZipMagicBytes)
            ? PayloadType.ZipArchive
            : bytesRead >= PayloadConstants.PayloadMagic.Length &&
            span[..PayloadConstants.PayloadMagic.Length].SequenceEqual(PayloadMagicBytes)
            ? PayloadType.RawPayload
            : PayloadType.Unknown;
    }

    #endregion

    #region Manifest Reading

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    internal static (DeltaArchiveManifest Manifest, PayloadHeader Header) ReadManifest(Stream stream)
    {
        var header = PayloadHeader.ReadFrom(stream);
        var manifestRaw = new byte[header.ManifestLength];
        var bytesRead = ReadExact(stream, manifestRaw, 0, manifestRaw.Length);
        if ((ulong)bytesRead != header.ManifestLength)
        {
            throw new InvalidManifestException($"Failed to read manifest: expected {header.ManifestLength} bytes, got {bytesRead}.");
        }

        var manifest = DeltaArchiveManifest.Parser.ParseFrom(manifestRaw);
        header.ValidateMinorVersion(manifest.MinorVersion);
        if (stream.CanSeek)
        {
            stream.Seek(header.MetadataSignatureLength, SeekOrigin.Current);
        }
        else
        {
            var signatureBuffer = new byte[header.MetadataSignatureLength];
            var sigBytesRead = ReadExact(stream, signatureBuffer, 0, signatureBuffer.Length);
            if ((uint)sigBytesRead != header.MetadataSignatureLength)
            {
                throw new InvalidManifestException(
                    $"Failed to read metadata signature: expected {header.MetadataSignatureLength} bytes, got {sigBytesRead}.");
            }
        }

        return (manifest, header);
    }

    internal static async Task<(DeltaArchiveManifest Manifest, PayloadHeader Header)> ReadManifestAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = await PayloadHeader.ReadFromAsync(stream, cancellationToken).ConfigureAwait(false);
        var manifestRaw = new byte[header.ManifestLength];
        var bytesRead = await ReadExactAsync(stream, manifestRaw, 0, manifestRaw.Length, cancellationToken).ConfigureAwait(false);
        if ((ulong)bytesRead != header.ManifestLength)
        {
            throw new InvalidManifestException($"Failed to read manifest: expected {header.ManifestLength} bytes, got {bytesRead}.");
        }

        var manifest = DeltaArchiveManifest.Parser.ParseFrom(manifestRaw);
        header.ValidateMinorVersion(manifest.MinorVersion);
        if (stream.CanSeek)
        {
            stream.Seek(header.MetadataSignatureLength, SeekOrigin.Current);
        }
        else
        {
            var signatureBuffer = new byte[header.MetadataSignatureLength];
            var sigBytesRead = await ReadExactAsync(stream, signatureBuffer, 0, signatureBuffer.Length, cancellationToken).ConfigureAwait(false);
            if ((uint)sigBytesRead != header.MetadataSignatureLength)
            {
                throw new InvalidManifestException(
                    $"Failed to read metadata signature: expected {header.MetadataSignatureLength} bytes, got {sigBytesRead}.");
            }
        }

        return (manifest, header);
    }

    internal static PayloadMetadata BuildMetadata(DeltaArchiveManifest manifest, PayloadHeader header)
    {
        var meta = new PayloadMetadata
        {
            BlockSize = manifest.BlockSize,
            ManifestLength = header.ManifestLength,
            MetadataSignatureLength = header.MetadataSignatureLength,
            BaseOffset = header.BaseOffset,
            Manifest = manifest,
            MaxTimestamp = manifest.MaxTimestamp,
            DynamicPartitionMetadata = manifest.DynamicPartitionMetadata ?? new DynamicPartitionMetadata(),
            SignaturesOffset = manifest.SignaturesOffset,
            SignaturesSize = manifest.SignaturesSize
        };

        foreach (var p in manifest.Partitions)
        {
            if (!string.IsNullOrEmpty(p.PartitionName))
            {
                meta.PartitionNames.Add(p.PartitionName);
            }
        }

        return meta;
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var bytesRead = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private static async Task<(DeltaArchiveManifest Manifest, PayloadHeader Header)> OpenAndReadManifestAsync(string filepath, CancellationToken cancellationToken = default)
    {
        var payloadType = DetectPayloadType(filepath);
        switch (payloadType)
        {
            case PayloadType.ZipArchive:
                using (var zr = ZipFile.OpenRead(filepath))
                {
                    var zf = FindPayloadEntry(zr)
                        ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
                    using var zfStream = zf.Open();
                    return await ReadManifestAsync(zfStream, cancellationToken).ConfigureAwait(false);
                }

            case PayloadType.RawPayload:
                using (var f = File.OpenRead(filepath))
                {
                    return await ReadManifestAsync(f, cancellationToken).ConfigureAwait(false);
                }

            case PayloadType.Unknown:
                break;
            default:
                throw new InvalidPayloadMagicException($"File does not appear to be a payload or ZIP archive: {filepath}");
        }

        throw new InvalidPayloadMagicException($"Unrecognized payload type: {payloadType}");
    }

    private static ZipArchiveEntry? FindPayloadEntry(ZipArchive archive)
    {
        var entry = archive.GetEntry(PayloadConstants.PayloadFilename);
        if (entry != null)
        {
            return entry;
        }

        foreach (var e in archive.Entries)
        {
            if (string.Equals(e.Name, PayloadConstants.PayloadFilename, StringComparison.OrdinalIgnoreCase))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>
    /// Materializes the ZIP-embedded payload to a temporary file (fallback for compressed entries),
    /// reporting decompressed bytes against <paramref name="totalBytes"/> as progress.
    /// <para>把 ZIP 内嵌的 payload 物化到临时文件（压缩条目的回退路径），并按解压字节逐块上报进度。</para>
    /// </summary>
    private static async Task<string> ExtractPayloadFromZipAsync(string filepath, CancellationToken cancellationToken,
        IProgress<(long Processed, long Total)>? byteProgress, long totalBytes)
    {
        var tempPath = TempFileHelper.CreateTempFile();
        var buffer = new byte[1 << 20];
        using (var zr = ZipFile.OpenRead(filepath))
        {
            var zf = FindPayloadEntry(zr)
                ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
            using var zfStream = zf.Open();
            using var outFile = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan);
            byteProgress?.Report((0, totalBytes));
            long processed = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await zfStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await outFile.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                processed = checked(processed + read);
                byteProgress?.Report((processed, totalBytes));
            }
        }

        return tempPath;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Reads payload metadata from a local file, handling both raw payload.bin and ZIP-wrapped payloads.
    /// <para>从本地文件读取载荷元数据，支持原始 payload.bin 和 ZIP 包裹的载荷。</para>
    /// </summary>
    public static PayloadMetadata GetPayloadMetadata(string filepath)
    {
        Guard.ThrowIfNull(filepath);
        Guard.ThrowIfNullOrWhiteSpace(filepath);

        var payloadType = DetectPayloadType(filepath);
        switch (payloadType)
        {
            case PayloadType.ZipArchive:
                using (var zr = ZipFile.OpenRead(filepath))
                {
                    var zf = FindPayloadEntry(zr)
                        ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
                    using var zfStream = zf.Open();
                    var (manifest, header) = ReadManifest(zfStream);
                    return BuildMetadata(manifest, header);
                }

            case PayloadType.RawPayload:
                using (var f = File.OpenRead(filepath))
                {
                    var (manifest, header) = ReadManifest(f);
                    return BuildMetadata(manifest, header);
                }

            case PayloadType.Unknown:
                break;
            default:
                throw new InvalidPayloadMagicException($"File does not appear to be a payload or ZIP archive: {filepath}");
        }

        throw new InvalidPayloadMagicException($"Unrecognized payload type: {payloadType}");
    }

    /// <summary>
    /// Asynchronously reads payload metadata from a local file.
    /// <para>从本地文件异步读取载荷元数据。</para>
    /// </summary>
    public static async Task<PayloadMetadata> GetPayloadMetadataAsync(string filepath, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(filepath);
        Guard.ThrowIfNullOrWhiteSpace(filepath);
        var (manifest, header) = await OpenAndReadManifestAsync(filepath, cancellationToken).ConfigureAwait(false);
        return BuildMetadata(manifest, header);
    }

    /// <summary>
    /// Reads payload metadata from a stream.
    /// <para>从流中读取载荷元数据。</para>
    /// </summary>
    public static PayloadMetadata GetPayloadMetadata(Stream stream)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        var (manifest, header) = ReadManifest(stream);
        return BuildMetadata(manifest, header);
    }

    /// <summary>
    /// Asynchronously reads payload metadata from a stream.
    /// <para>从流中异步读取载荷元数据。</para>
    /// </summary>
    public static async Task<PayloadMetadata> GetPayloadMetadataAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        var (manifest, header) = await ReadManifestAsync(stream, cancellationToken).ConfigureAwait(false);
        return BuildMetadata(manifest, header);
    }

    /// <summary>
    /// Returns the authoritative destination size (in bytes) for a partition update. Prefers the
    /// manifest's declared <c>new_partition_info.size</c> and otherwise falls back to the highest
    /// destination-extent end offset. Incremental payloads dominated by <c>SOURCE_COPY</c> operations
    /// carry an empty destination-extent list, so the declared size is the only honest figure
    /// available before the partition is actually reconstructed.
    /// <para>返回分区更新的权威目标大小（字节）。优先使用 manifest 声明的
    /// <c>new_partition_info.size</c>，否则回退到最高目标 extent 结束偏移。以 <c>SOURCE_COPY</c>
    /// 为主的增量载荷目标 extent 为空，声明大小是唯一在重建前可信的数值。</para>
    /// </summary>
    public static long GetPartitionSize(PartitionUpdate partition, uint blockSize)
    {
        Guard.ThrowIfNull(partition, nameof(partition));
        return PayloadExtractor.ProgressSize(partition, blockSize);
    }

    /// <summary>
    /// Gets partition size information from a local payload file.
    /// <para>从本地载荷文件获取分区大小信息。</para>
    /// </summary>
    public static List<PartitionInfoData> GetPartitionInfo(string filepath)
    {
        Guard.ThrowIfNull(filepath);
        Guard.ThrowIfNullOrWhiteSpace(filepath);
        var metadata = GetPayloadMetadata(filepath);
        return PartitionInfoHelper.ComputePartitionInfo(metadata.Manifest);
    }

    /// <summary>
    /// Asynchronously gets partition size information from a local payload file.
    /// <para>从本地载荷文件异步获取分区大小信息。</para>
    /// </summary>
    public static async Task<List<PartitionInfoData>> GetPartitionInfoAsync(string filepath, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(filepath);
        Guard.ThrowIfNullOrWhiteSpace(filepath);
        var metadata = await GetPayloadMetadataAsync(filepath, cancellationToken).ConfigureAwait(false);
        return PartitionInfoHelper.ComputePartitionInfo(metadata.Manifest);
    }

    /// <summary>
    /// Extracts partitions from a local payload file. ZIP-wrapped payloads first materialize the
    /// embedded payload.bin to a temporary file, then extract from that file.
    /// <para>从本地载荷文件中提取分区。ZIP 包裹的载荷先把内嵌 payload.bin 物化到临时文件，再从该文件提取。</para>
    /// </summary>
    public static void Extract(string filepath, string[]? extractFiles = null, string? outputDir = null, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null)
    {
        Guard.ThrowIfNull(filepath);
        Guard.ThrowIfNullOrWhiteSpace(filepath);
        extractFiles ??= [];

        string? tempPath = null;
        var tempSourceFiles = new List<string>();
        try
        {
            using var plan = BuildExtractPlan(filepath, ref tempPath);
            var resolvedSourceImages = ResolveSourceImages(plan.Manifest, extractFiles, sourcePayloadPath, sourceImages, tempSourceFiles);
            new PayloadExtractor().ExtractPartitions(
                plan.Manifest, plan.OuterStream, plan.Header.BaseOffset, extractFiles, outputDir ?? string.Empty, resolvedSourceImages,
                openPayloadStream: plan.OpenPayloadStream);
        }
        finally
        {
            if (tempPath != null)
            {
                TempFileHelper.SafeDelete(tempPath);
            }

            foreach (var f in tempSourceFiles)
            {
                TempFileHelper.SafeDelete(f);
            }
        }
    }

    /// <summary>
    /// Asynchronously extracts partitions from a local payload file. ZIP-wrapped payloads first
    /// materialize the embedded payload.bin to a temporary file with byte progress, then extract
    /// from that file.
    /// <para>从本地载荷文件中异步提取分区。ZIP 包裹的载荷先把内嵌 payload.bin 物化到临时文件并上报字节进度，
    /// 再从该文件提取。</para>
    /// </summary>
    /// <param name="byteProgress">
    /// Optional byte-level progress callback receiving (Processed, Total) tuples covering the whole
    /// job; when a container (ZIP materialization) phase is present it maps to the leading bytes and
    /// the partition extraction to the remainder, so Total is the combined job size. Reports are
    /// throttled to at most one per 100 ms plus forced reports at stage boundaries and a final
    /// (total, total) flush. Total is 0 when no reliable estimate exists.
    /// <para>可选的字节级进度回调，覆盖整个作业并上报 (Processed, Total)；存在容器（ZIP 物化）阶段时
    /// 其映射到前置字节，分区提取映射到其余部分，Total 为作业总字节。上报节流为每 100ms 至多一次，
    /// 阶段边界与最终各上报一次 (total, total)。无可靠总量时 Total 为 0。</para>
    /// </param>
    public static async Task ExtractAsync(string filepath, string[]? extractFiles = null, string? outputDir = null, CancellationToken cancellationToken = default, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null)
    {
        Guard.ThrowIfNull(filepath);
        Guard.ThrowIfNullOrWhiteSpace(filepath);
        extractFiles ??= [];

        string? tempPath = null;
        var tempSourceFiles = new List<string>();
        try
        {
            var (plan, planTempPath) = await BuildExtractPlanAsync(filepath, cancellationToken, byteProgress).ConfigureAwait(false);
            tempPath = planTempPath;
            using var planDisposable = plan;
            var resolvedSourceImages = ResolveSourceImages(plan.Manifest, extractFiles, sourcePayloadPath, sourceImages, tempSourceFiles);

            IProgress<(long Processed, long Total)>? sink = byteProgress;
            if (plan.ContainerOffset > 0 && byteProgress is not null)
            {
                sink = new OffsetByteProgress(byteProgress, plan.ContainerOffset, plan.TotalBytes);
            }

            var extractor = new PayloadExtractor();
            await extractor.ExtractPartitionsAsync(
                plan.Manifest, plan.OuterStream, plan.Header.BaseOffset, extractFiles, outputDir ?? string.Empty, cancellationToken,
                resolvedSourceImages, sink, plan.OpenPayloadStream).ConfigureAwait(false);
        }
        finally
        {
            if (tempPath != null)
            {
                TempFileHelper.SafeDelete(tempPath);
            }

            foreach (var f in tempSourceFiles)
            {
                TempFileHelper.SafeDelete(f);
            }
        }
    }

    /// <summary>
    /// Extracts partitions from a payload stream.
    /// <para>从载荷流中提取分区。</para>
    /// </summary>
    public static void Extract(Stream stream, string[]? extractFiles = null, string? outputDir = null, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        extractFiles ??= [];
        var tempSourceFiles = new List<string>();
        try
        {
            var (manifest, header) = ReadManifest(stream);
            var resolvedSourceImages = ResolveSourceImages(manifest, extractFiles, sourcePayloadPath, sourceImages, tempSourceFiles);
            new PayloadExtractor().ExtractPartitions(manifest, stream, header.BaseOffset, extractFiles, outputDir ?? string.Empty, resolvedSourceImages);
        }
        finally
        {
            foreach (var f in tempSourceFiles)
            {
                TempFileHelper.SafeDelete(f);
            }
        }
    }

    /// <summary>
    /// Asynchronously extracts partitions from a payload stream.
    /// <para>从载荷流中异步提取分区。</para>
    /// </summary>
    /// <param name="byteProgress">
    /// Optional byte-level progress callback receiving (Processed, Total) tuples for the current
    /// partition; reports are throttled to at most one per 100 ms plus one forced report at stage
    /// boundaries and a final (total, total) flush. Total is 0 when no reliable estimate exists.
    /// <para>可选的字节级进度回调，上报 (Processed, Total) 元组；上报节流为每 100ms 至多一次，
    /// 阶段结束时强制上报一次，最终上报 (total, total)。无可靠总量时 Total 为 0。</para>
    /// </param>
    public static async Task ExtractAsync(Stream stream, string[]? extractFiles = null, string? outputDir = null, CancellationToken cancellationToken = default, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        extractFiles ??= [];
        var tempSourceFiles = new List<string>();
        try
        {
            var (manifest, header) = await ReadManifestAsync(stream, cancellationToken).ConfigureAwait(false);
            var resolvedSourceImages = ResolveSourceImages(manifest, extractFiles, sourcePayloadPath, sourceImages, tempSourceFiles);
            var extractor = new PayloadExtractor();
            await extractor.ExtractPartitionsAsync(manifest, stream, header.BaseOffset, extractFiles, outputDir ?? string.Empty, cancellationToken, resolvedSourceImages, byteProgress).ConfigureAwait(false);
        }
        finally
        {
            foreach (var f in tempSourceFiles)
            {
                TempFileHelper.SafeDelete(f);
            }
        }
    }

    /// <summary>
    /// Parses a payload_properties.txt file and returns the properties.
    /// <para>解析 payload_properties.txt 文件并返回属性。</para>
    /// </summary>
    public static PayloadProperties ParsePayloadProperties(string filepath)
    {
        Guard.ThrowIfNull(filepath);
        Guard.ThrowIfNullOrWhiteSpace(filepath);
        return PayloadPropertiesParser.ParseFile(filepath);
    }

    #endregion

    #region Extract Plan

    /// <summary>
    /// Builds a synchronous extract plan (manifest, header, outer stream, per-partition payload
    /// handle) resolving the payload source once. ZIP payloads always materialize the embedded
    /// payload.bin to a temporary copy recorded in <paramref name="tempPath"/>.
    /// <para>构建同步提取计划（清单、头、外层流、每分区载荷句柄），一次性解析载荷来源。ZIP 载荷总是把
    /// 内嵌 payload.bin 物化到临时副本并记录到 <paramref name="tempPath"/>。</para>
    /// </summary>
    private static ExtractPlan BuildExtractPlan(string filepath, ref string? tempPath)
    {
        var payloadType = DetectPayloadType(filepath);
        switch (payloadType)
        {
            case PayloadType.RawPayload:
                {
                    var outer = File.OpenRead(filepath);
                    try
                    {
                        var (manifest, header) = ReadManifest(outer);
                        var raw = filepath;
                        return new ExtractPlan(manifest, header, outer,
                            () => new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.RandomAccess),
                            0, ExtractTotalBytes(manifest));
                    }
                    catch
                    {
                        outer.Dispose();
                        throw;
                    }
                }

            case PayloadType.ZipArchive:
                {
                    DeltaArchiveManifest manifest;
                    PayloadHeader header;
                    using (var zr = ZipFile.OpenRead(filepath))
                    {
                        var zf = FindPayloadEntry(zr)
                            ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
                        using var zfStream = zf.Open();
                        (manifest, header) = ReadManifest(zfStream);
                    }

                    var extractionTotal = ExtractTotalBytes(manifest);

                    if (TryGetStoredPayloadLocation(filepath, out var dataStart, out var payloadLength))
                    {
                        var outer = File.OpenRead(filepath);
                        try
                        {
                            return new ExtractPlan(manifest, header, outer,
                                () => new StoredZipEntryStream(filepath, dataStart, payloadLength),
                                0, extractionTotal);
                        }
                        catch
                        {
                            outer.Dispose();
                            throw;
                        }
                    }

                    tempPath = ExtractPayloadFromZip(filepath);
                    var tempFile = tempPath;
                    var containerOuter = File.OpenRead(tempFile);
                    try
                    {
                        return new ExtractPlan(manifest, header, containerOuter,
                            () => new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.RandomAccess),
                            PayloadEntryUncompressedLength(filepath), extractionTotal);
                    }
                    catch
                    {
                        containerOuter.Dispose();
                        throw;
                    }
                }

            default:
                throw new InvalidPayloadMagicException($"Unrecognized payload type: {payloadType}");
        }
    }

    /// <summary>
    /// Builds an asynchronous extract plan (through <see cref="BuildExtractPlan"/>) and returns any
    /// materialized temporary payload path alongside it. Reports ZIP materialization bytes through
    /// <paramref name="byteProgress"/> when a container phase is used.
    /// <para>异步构建提取计划（依赖 <see cref="BuildExtractPlan"/>），并同时返回物化产生的临时载荷
    /// 路径。存在容器阶段时通过 <paramref name="byteProgress"/> 上报 ZIP 物化字节。</para>
    /// </summary>
    private static async Task<(ExtractPlan Plan, string? TempPath)> BuildExtractPlanAsync(string filepath, CancellationToken cancellationToken,
        IProgress<(long Processed, long Total)>? byteProgress)
    {
        var payloadType = DetectPayloadType(filepath);
        switch (payloadType)
        {
            case PayloadType.RawPayload:
                {
                    var outer = File.OpenRead(filepath);
                    try
                    {
                        var (manifest, header) = await ReadManifestAsync(outer, cancellationToken).ConfigureAwait(false);
                        var raw = filepath;
                        return (new ExtractPlan(manifest, header, outer,
                            () => new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.RandomAccess),
                            0, ExtractTotalBytes(manifest)), null);
                    }
                    catch
                    {
                        outer.Dispose();
                        throw;
                    }
                }

            case PayloadType.ZipArchive:
                {
                    DeltaArchiveManifest manifest;
                    PayloadHeader header;
                    using (var zr = ZipFile.OpenRead(filepath))
                    {
                        var zf = FindPayloadEntry(zr)
                            ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
                        using var zfStream = zf.Open();
                        (manifest, header) = await ReadManifestAsync(zfStream, cancellationToken).ConfigureAwait(false);
                    }

                    var extractionTotal = ExtractTotalBytes(manifest);

                    if (TryGetStoredPayloadLocation(filepath, out var dataStart, out var payloadLength))
                    {
                        var outer = File.OpenRead(filepath);
                        try
                        {
                            return (new ExtractPlan(manifest, header, outer,
                                () => new StoredZipEntryStream(filepath, dataStart, payloadLength),
                                0, extractionTotal), null);
                        }
                        catch
                        {
                            outer.Dispose();
                            throw;
                        }
                    }

                    var containerTotal = PayloadEntryUncompressedLength(filepath);
                    long totalBytes;
                    try
                    {
                        totalBytes = checked(containerTotal + extractionTotal);
                    }
                    catch (OverflowException ex)
                    {
                        throw new InvalidManifestException("Payload and extraction sizes exceed the supported range.", ex);
                    }

                    var tempFile = await ExtractPayloadFromZipAsync(filepath, cancellationToken, byteProgress, totalBytes).ConfigureAwait(false);
                    var containerOuter = File.OpenRead(tempFile);
                    try
                    {
                        return (new ExtractPlan(manifest, header, containerOuter,
                            () => new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.RandomAccess),
                            containerTotal, extractionTotal), tempFile);
                    }
                    catch
                    {
                        containerOuter.Dispose();
                        throw;
                    }
                }

            default:
                throw new InvalidPayloadMagicException($"Unrecognized payload type: {payloadType}");
        }
    }

    /// <summary>
    /// Computes the total estimated output bytes of all manifest partitions.
    /// <para>计算清单中所有分区的估计输出总字节数。</para>
    /// </summary>
    private static long ExtractTotalBytes(DeltaArchiveManifest manifest)
    {
        long total = 0;
        foreach (var partition in manifest.Partitions)
        {
            if (string.IsNullOrEmpty(partition.PartitionName))
            {
                continue;
            }

            total = checked(total + PayloadExtractor.ProgressSize(partition, manifest.BlockSize));
        }

        return total;
    }

    /// <summary>
    /// Reads the uncompressed length of the payload entry within a ZIP archive.
    /// <para>读取 ZIP 归档内 payload 条目的未压缩长度。</para>
    /// </summary>
    private static long PayloadEntryUncompressedLength(string filepath)
    {
        using var zr = ZipFile.OpenRead(filepath);
        var zf = FindPayloadEntry(zr)
            ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
        return zf.Length;
    }

    /// <summary>
    /// Locates a STORED (uncompressed) payload.bin entry inside a ZIP archive so extraction can read it
    /// in place. Returns whether the entry is STORED; when true, <paramref name="dataStart"/> is the
    /// absolute byte offset of the entry data within <paramref name="filepath"/> and
    /// <paramref name="length"/> is the payload byte count.
    /// <para>在 ZIP 归档中定位 STORED（未压缩）的 payload.bin 条目，以便解包原址直读。返回是否为
    /// STORED 条目；为 true 时 <paramref name="dataStart"/> 是条目数据在 <paramref name="filepath"/>
    /// 内的绝对字节偏移，<paramref name="length"/> 为载荷字节数。</para>
    /// </summary>
    private static bool TryGetStoredPayloadLocation(string filepath, out long dataStart, out long length)
    {
        dataStart = 0;
        length = 0;
        using var zip = new SharpZipZipFile(filepath);
        var entry = zip.GetEntry(PayloadConstants.PayloadFilename);
        if (entry is null || entry.CompressionMethod != SharpZipCompressionMethod.Stored || entry.Size < 0 || entry.Offset < 0)
        {
            return false;
        }

        length = entry.Size;
        using var fs = File.OpenRead(filepath);
        if (entry.Offset + 30 > fs.Length)
        {
            return false;
        }

        Span<byte> localHeader = stackalloc byte[30];
        fs.Seek(entry.Offset, SeekOrigin.Begin);
        if (fs.Read(localHeader) < localHeader.Length)
        {
            return false;
        }

        if ((localHeader[8] | (localHeader[9] << 8)) != 0)
        {
            return false;
        }

        var nameLength = localHeader[26] | (localHeader[27] << 8);
        var extraLength = localHeader[28] | (localHeader[29] << 8);
        dataStart = checked(entry.Offset + 30 + nameLength + extraLength);
        return true;
    }

    /// <summary>
    /// Offsets partition-extraction byte progress past a container (ZIP materialization) phase so a
    /// single continuous job progress can be reported.
    /// <para>把分区提取字节进度偏移到容器（ZIP 物化）阶段之后，从而上报单一连续的作业进度。</para>
    /// </summary>
    private sealed class OffsetByteProgress(IProgress<(long Processed, long Total)> sink, long offset, long total)
        : IProgress<(long Processed, long Total)>
    {
        /// <inheritdoc />
        public void Report((long Processed, long Total) sample)
            => sink.Report((offset + sample.Processed, total));
    }

    /// <summary>
    /// Holds the resolved payload source and cleaning contracts for an extract operation. The outer
    /// stream is a placeholder when per-partition handles (<see cref="OpenPayloadStream"/>) are used.
    /// <para>持有一次提取操作已解析的载荷来源与清理约定。当使用每分区句柄
    /// （<see cref="OpenPayloadStream"/>）时，外层流仅为占位。</para>
    /// </summary>
    private sealed class ExtractPlan : IDisposable
    {
        /// <summary>Gets the extracted manifest. <para>获取已解析的清单。</para></summary>
        public DeltaArchiveManifest Manifest { get; }

        /// <summary>Gets the payload header. <para>获取载荷头。</para></summary>
        public PayloadHeader Header { get; }

        /// <summary>Gets the outer stream (used only as a fallback when no per-partition handle is present). <para>获取外层流（仅在无每分区句柄时作为回退使用）。</para></summary>
        public Stream OuterStream { get; }

        /// <summary>Gets the factory opening an exclusive read-only payload handle per partition. <para>获取每分区打开专属只读载荷句柄的工厂。</para></summary>
        public Func<Stream> OpenPayloadStream { get; }

        /// <summary>Gets the container (ZIP materialization) byte count; 0 when absent. <para>获取容器（ZIP 物化）阶段字节数；不存在时为 0。</para></summary>
        public long ContainerOffset { get; }

        /// <summary>Gets the sum of partition output bytes. <para>获取分区输出字节总和。</para></summary>
        public long ExtractionTotal { get; }

        /// <summary>Gets the combined job byte total (<see cref="ContainerOffset"/> + <see cref="ExtractionTotal"/>). <para>获取作业总字节数（<see cref="ContainerOffset"/> + <see cref="ExtractionTotal"/>）。</para></summary>
        public long TotalBytes { get; }

        /// <summary>
        /// Initializes a new extract plan.
        /// <para>初始化一个新的提取计划。</para>
        /// </summary>
        public ExtractPlan(DeltaArchiveManifest manifest, PayloadHeader header, Stream outerStream,
            Func<Stream> openPayloadStream, long containerOffset, long extractionTotal)
        {
            Manifest = manifest;
            Header = header;
            OuterStream = outerStream;
            OpenPayloadStream = openPayloadStream;
            ContainerOffset = containerOffset;
            ExtractionTotal = extractionTotal;
            TotalBytes = checked(containerOffset + extractionTotal);
        }

        /// <inheritdoc />
        public void Dispose() => OuterStream.Dispose();
    }

    #endregion
}