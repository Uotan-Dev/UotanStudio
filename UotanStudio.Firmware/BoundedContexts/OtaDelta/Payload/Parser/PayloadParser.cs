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

/// <summary>
/// Entry point for reading and extracting Android OTA <c>payload.bin</c> files, accepting raw payload streams
/// and ZIP-wrapped update packages. Input is untrusted: sizes, extents and output paths are bounds-checked before use.
/// <para>Android OTA <c>payload.bin</c> 读取与提取的入口，支持原始载荷流与 ZIP 包裹的升级包。输入不可信：
/// 大小、extent 与输出路径在使用前均经边界检查。</para>
/// </summary>
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
    /// <param name="filepath">Local payload file or ZIP update package; must be non-blank. <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <returns>Block size, manifest/signature geometry, base offset, parsed manifest, max timestamp, dynamic
    /// partition metadata (empty when absent), signature fields and the list of non-empty partition names.
    /// <para>块大小、清单/签名几何、基准偏移、已解析清单、最大时间戳、动态分区元数据（缺失时为空对象）、
    /// 签名字段以及非空分区名列表。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filepath"/> is <see langword="null"/>. <para><paramref name="filepath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filepath"/> is blank. <para><paramref name="filepath"/> 为空白。</para></exception>
    /// <exception cref="InvalidPayloadMagicException">The file magic is neither a ZIP header nor the payload <c>CrAU</c> magic.
    /// <para>文件魔数既非 ZIP 头也非载荷 <c>CrAU</c> 魔数。</para></exception>
    /// <exception cref="InvalidManifestException">The ZIP lacks a payload entry, or the manifest/signature bytes are truncated.
    /// <para>ZIP 缺少载荷条目，或清单/签名字节被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
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
    /// <param name="filepath">Local payload file or ZIP update package; must be non-blank. <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <param name="cancellationToken">Cancels metadata reads and ZIP materialization. <para>取消元数据读取与 ZIP 物化。</para></param>
    /// <returns>The same metadata shape as <see cref="GetPayloadMetadata(string)"/>. <para>与 <see cref="GetPayloadMetadata(string)"/> 相同的元数据形态。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filepath"/> is <see langword="null"/>. <para><paramref name="filepath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filepath"/> is blank. <para><paramref name="filepath"/> 为空白。</para></exception>
    /// <exception cref="InvalidPayloadMagicException">The file is neither a payload nor a ZIP archive. <para>文件既不是载荷也不是 ZIP 归档。</para></exception>
    /// <exception cref="InvalidManifestException">The payload entry is missing or the manifest is truncated. <para>载荷条目缺失或清单被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
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
    /// <param name="stream">Payload stream positioned at the start of the header; the caller keeps ownership and
    /// the stream is not closed. When seekable, reading advances past the metadata signature; otherwise the
    /// signature bytes are consumed.
    /// <para>定位在头部起始处的载荷流；所有权仍属调用方且不会被关闭。可 seek 时读取会跳过元数据签名，
    /// 否则消耗签名字节。</para></param>
    /// <returns>The parsed metadata, same shape as <see cref="GetPayloadMetadata(string)"/>.
    /// <para>解析后的元数据，形态与 <see cref="GetPayloadMetadata(string)"/> 相同。</para></returns>
    /// <exception cref="InvalidManifestException">The manifest or metadata signature is truncated. <para>清单或元数据签名被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
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
    /// <param name="stream">Payload stream positioned at the start of the header; not closed, and the signature
    /// region is skipped or consumed as in the synchronous overload.
    /// <para>定位在头部起始处的载荷流；不会被关闭，签名区域按同步重载的方式跳过或消耗。</para></param>
    /// <param name="cancellationToken">Cancels between reads; a pending read is aborted mid-flight. <para>在读取间隙取消；挂起的读取会被中途打断。</para></param>
    /// <returns>The parsed metadata, same shape as <see cref="GetPayloadMetadata(string)"/>.
    /// <para>解析后的元数据，形态与 <see cref="GetPayloadMetadata(string)"/> 相同。</para></returns>
    /// <exception cref="InvalidManifestException">The manifest or metadata signature is truncated. <para>清单或元数据签名被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
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
    /// <param name="partition">Partition update from a payload manifest; must be non-null. <para>来自载荷清单的分区更新；不得为 null。</para></param>
    /// <param name="blockSize">Payload block size used to convert extent blocks to bytes. <para>载荷块大小，用于把 extent 块数换算为字节。</para></param>
    /// <returns>Destination size in bytes, either the declared size or the highest extent end offset scaled by <paramref name="blockSize"/>.
    /// <para>目标字节大小：为声明大小，或最高 extent 结束偏移按 <paramref name="blockSize"/> 换算的字节数。</para></returns>
    /// <exception cref="OverflowException">The declared size or extent arithmetic overflows <see langword="long"/>. <para>声明大小或 extent 运算溢出 <see langword="long"/>。</para></exception>
    public static long GetPartitionSize(PartitionUpdate partition, uint blockSize)
    {
        Guard.ThrowIfNull(partition, nameof(partition));
        return PayloadExtractor.ProgressSize(partition, blockSize);
    }

    /// <summary>
    /// Gets partition size information from a local payload file.
    /// <para>从本地载荷文件获取分区大小信息。</para>
    /// </summary>
    /// <param name="filepath">Local payload file or ZIP update package; must be non-blank. <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <returns>One entry per manifest partition with its name and computed size. <para>每个清单分区一条记录，含分区名与计算大小。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filepath"/> is <see langword="null"/>. <para><paramref name="filepath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filepath"/> is blank. <para><paramref name="filepath"/> 为空白。</para></exception>
    /// <exception cref="InvalidPayloadMagicException">The file is neither a payload nor a ZIP archive. <para>文件既不是载荷也不是 ZIP 归档。</para></exception>
    /// <exception cref="InvalidManifestException">The payload entry is missing or the manifest is truncated. <para>载荷条目缺失或清单被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
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
    /// <param name="filepath">Local payload file or ZIP update package; must be non-blank. <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <param name="cancellationToken">Cancels the manifest read. <para>取消清单读取。</para></param>
    /// <returns>One entry per manifest partition with its name and computed size. <para>每个清单分区一条记录，含分区名与计算大小。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filepath"/> is <see langword="null"/>. <para><paramref name="filepath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filepath"/> is blank. <para><paramref name="filepath"/> 为空白。</para></exception>
    /// <exception cref="InvalidManifestException">The payload entry is missing or the manifest is truncated. <para>载荷条目缺失或清单被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
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
    /// <param name="filepath">Local payload file or ZIP update package; must be non-blank. <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <param name="extractFiles">Partition names to extract; <see langword="null"/> or empty extracts every named partition in the manifest.
    /// <para>要提取的分区名；为 <see langword="null"/> 或空时提取清单中全部有名字分区。</para></param>
    /// <param name="outputDir">Destination directory for <c>{partition}.img</c> files; <see langword="null"/> or empty
    /// uses the process current directory and a missing directory is created. Partition names that would escape it are rejected.
    /// <para><c>{partition}.img</c> 的输出目录；为 <see langword="null"/> 或空时使用进程当前目录，缺失目录会被创建；逃逸该目录的分区名会被拒绝。</para></param>
    /// <param name="sourcePayloadPath">Full payload or ZIP used as base-image source for source-dependent operations;
    /// base images go to temporary files deleted in all paths. <para>为源依赖操作提供基座镜像的完整载荷或 ZIP；基座镜像写入临时文件并在任何路径下删除。</para></param>
    /// <param name="sourceImages">Per-partition pre-supplied base image paths; takes precedence over <paramref name="sourcePayloadPath"/> for a partition.
    /// <para>按分区预先提供的基座镜像路径；对某分区优先于 <paramref name="sourcePayloadPath"/>。</para></param>
    /// <param name="maximumPartitionBytes">Upper bound on one partition's output bytes, enforced before writing; must be positive.
    /// <para>单分区输出字节上限，写入前校验；必须为正数。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="filepath"/> is <see langword="null"/>. <para><paramref name="filepath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filepath"/> is blank. <para><paramref name="filepath"/> 为空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumPartitionBytes"/> is zero or negative. <para><paramref name="maximumPartitionBytes"/> 为零或负数。</para></exception>
    /// <exception cref="InvalidPayloadMagicException">The file is neither a payload nor a ZIP archive. <para>文件既不是载荷也不是 ZIP 归档。</para></exception>
    /// <exception cref="InvalidManifestException">The payload entry is missing or the manifest is truncated. <para>载荷条目缺失或清单被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="PartitionExtractionException">A partition escapes the output directory, exceeds the byte cap, or lacks its required base image.
    /// <para>分区逃逸输出目录、超出字节上限，或缺少所需的基座镜像。</para></exception>
    public static void Extract(string filepath, string[]? extractFiles = null, string? outputDir = null, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null, long maximumPartitionBytes = PayloadExtractor.DefaultMaximumPartitionBytes)
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
            new PayloadExtractor(maximumPartitionBytes).ExtractPartitions(
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
    /// <param name="filepath">Local payload file or ZIP update package; must be non-blank. <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <param name="extractFiles">Partition names to extract; <see langword="null"/> or empty extracts every named partition in the manifest.
    /// <para>要提取的分区名；为 <see langword="null"/> 或空时提取清单中全部有名字分区。</para></param>
    /// <param name="outputDir">Destination directory for <c>{partition}.img</c> files; <see langword="null"/> or empty
    /// uses the process current directory and a missing directory is created. Partition names that would escape it are rejected.
    /// <para><c>{partition}.img</c> 的输出目录；为 <see langword="null"/> 或空时使用进程当前目录，缺失目录会被创建；逃逸该目录的分区名会被拒绝。</para></param>
    /// <param name="cancellationToken">Cancels materialization and extraction between reads. <para>在读取间隙取消物化与提取。</para></param>
    /// <param name="sourcePayloadPath">Full payload or ZIP used as base-image source for source-dependent operations;
    /// base images go to temporary files deleted in all paths. <para>为源依赖操作提供基座镜像的完整载荷或 ZIP；基座镜像写入临时文件并在任何路径下删除。</para></param>
    /// <param name="sourceImages">Per-partition pre-supplied base image paths; takes precedence over <paramref name="sourcePayloadPath"/> for a partition.
    /// <para>按分区预先提供的基座镜像路径；对某分区优先于 <paramref name="sourcePayloadPath"/>。</para></param>
    /// <param name="maximumPartitionBytes">Upper bound on one partition's output bytes, enforced before writing; must be positive.
    /// <para>单分区输出字节上限，写入前校验；必须为正数。</para></param>
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
    /// <returns>A task that completes when all selected partitions are extracted. <para>在全部所选分区提取完成后结束的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filepath"/> is <see langword="null"/>. <para><paramref name="filepath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filepath"/> is blank. <para><paramref name="filepath"/> 为空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumPartitionBytes"/> is zero or negative. <para><paramref name="maximumPartitionBytes"/> 为零或负数。</para></exception>
    /// <exception cref="InvalidPayloadMagicException">The file is neither a payload nor a ZIP archive. <para>文件既不是载荷也不是 ZIP 归档。</para></exception>
    /// <exception cref="InvalidManifestException">The payload entry is missing, the manifest is truncated, or sizes exceed the supported range.
    /// <para>载荷条目缺失、清单被截断，或大小超出支持范围。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="PartitionExtractionException">A partition escapes the output directory, exceeds the byte cap, or lacks its required base image.
    /// <para>分区逃逸输出目录、超出字节上限，或缺少所需的基座镜像。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
    public static async Task ExtractAsync(string filepath, string[]? extractFiles = null, string? outputDir = null, CancellationToken cancellationToken = default, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null, long maximumPartitionBytes = PayloadExtractor.DefaultMaximumPartitionBytes)
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

            var extractor = new PayloadExtractor(maximumPartitionBytes);
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
    /// <param name="stream">Payload stream positioned at the start of the header; not closed, and reads are
    /// sequential from the current position. Caller-owned base-image extraction uses separate file handles.
    /// <para>定位在头部起始处的载荷流；不会被关闭，从当前位置顺序读取。调用方所有的基座镜像提取使用独立文件句柄。</para></param>
    /// <param name="extractFiles">Partition names to extract; <see langword="null"/> or empty extracts every named partition in the manifest.
    /// <para>要提取的分区名；为 <see langword="null"/> 或空时提取清单中全部有名字分区。</para></param>
    /// <param name="outputDir">Destination directory for <c>{partition}.img</c> files; <see langword="null"/> or empty
    /// uses the process current directory and a missing directory is created. Partition names that would escape it are rejected.
    /// <para><c>{partition}.img</c> 的输出目录；为 <see langword="null"/> 或空时使用进程当前目录，缺失目录会被创建；逃逸该目录的分区名会被拒绝。</para></param>
    /// <param name="sourcePayloadPath">Full payload or ZIP used as base-image source for source-dependent operations;
    /// base images go to temporary files deleted in all paths. <para>为源依赖操作提供基座镜像的完整载荷或 ZIP；基座镜像写入临时文件并在任何路径下删除。</para></param>
    /// <param name="sourceImages">Per-partition pre-supplied base image paths; takes precedence over <paramref name="sourcePayloadPath"/> for a partition.
    /// <para>按分区预先提供的基座镜像路径；对某分区优先于 <paramref name="sourcePayloadPath"/>。</para></param>
    /// <param name="maximumPartitionBytes">Upper bound on one partition's output bytes, enforced before writing; must be positive.
    /// <para>单分区输出字节上限，写入前校验；必须为正数。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>. <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumPartitionBytes"/> is zero or negative. <para><paramref name="maximumPartitionBytes"/> 为零或负数。</para></exception>
    /// <exception cref="InvalidManifestException">The manifest or metadata signature is truncated. <para>清单或元数据签名被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="PartitionExtractionException">A partition escapes the output directory, exceeds the byte cap, or lacks its required base image.
    /// <para>分区逃逸输出目录、超出字节上限，或缺少所需的基座镜像。</para></exception>
    public static void Extract(Stream stream, string[]? extractFiles = null, string? outputDir = null, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null, long maximumPartitionBytes = PayloadExtractor.DefaultMaximumPartitionBytes)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        extractFiles ??= [];
        var tempSourceFiles = new List<string>();
        try
        {
            var (manifest, header) = ReadManifest(stream);
            var resolvedSourceImages = ResolveSourceImages(manifest, extractFiles, sourcePayloadPath, sourceImages, tempSourceFiles);
            new PayloadExtractor(maximumPartitionBytes).ExtractPartitions(manifest, stream, header.BaseOffset, extractFiles, outputDir ?? string.Empty, resolvedSourceImages);
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
    /// <param name="stream">Payload stream positioned at the start of the header; not closed, and reads are
    /// sequential from the current position. Caller-owned base-image extraction uses separate file handles.
    /// <para>定位在头部起始处的载荷流；不会被关闭，从当前位置顺序读取。调用方所有的基座镜像提取使用独立文件句柄。</para></param>
    /// <param name="extractFiles">Partition names to extract; <see langword="null"/> or empty extracts every named partition in the manifest.
    /// <para>要提取的分区名；为 <see langword="null"/> 或空时提取清单中全部有名字分区。</para></param>
    /// <param name="outputDir">Destination directory for <c>{partition}.img</c> files; <see langword="null"/> or empty
    /// uses the process current directory and a missing directory is created. Partition names that would escape it are rejected.
    /// <para><c>{partition}.img</c> 的输出目录；为 <see langword="null"/> 或空时使用进程当前目录，缺失目录会被创建；逃逸该目录的分区名会被拒绝。</para></param>
    /// <param name="cancellationToken">Cancels between reads and between partitions. <para>在读取间隙与各分区之间取消。</para></param>
    /// <param name="sourcePayloadPath">Full payload or ZIP used as base-image source for source-dependent operations;
    /// base images go to temporary files deleted in all paths. <para>为源依赖操作提供基座镜像的完整载荷或 ZIP；基座镜像写入临时文件并在任何路径下删除。</para></param>
    /// <param name="sourceImages">Per-partition pre-supplied base image paths; takes precedence over <paramref name="sourcePayloadPath"/> for a partition.
    /// <para>按分区预先提供的基座镜像路径；对某分区优先于 <paramref name="sourcePayloadPath"/>。</para></param>
    /// <param name="maximumPartitionBytes">Upper bound on one partition's output bytes, enforced before writing; must be positive.
    /// <para>单分区输出字节上限，写入前校验；必须为正数。</para></param>
    /// <returns>A task that completes when all selected partitions are extracted. <para>在全部所选分区提取完成后结束的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>. <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumPartitionBytes"/> is zero or negative. <para><paramref name="maximumPartitionBytes"/> 为零或负数。</para></exception>
    /// <exception cref="InvalidManifestException">The manifest or metadata signature is truncated. <para>清单或元数据签名被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="PartitionExtractionException">A partition escapes the output directory, exceeds the byte cap, or lacks its required base image.
    /// <para>分区逃逸输出目录、超出字节上限，或缺少所需的基座镜像。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
    public static async Task ExtractAsync(Stream stream, string[]? extractFiles = null, string? outputDir = null, CancellationToken cancellationToken = default, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null, long maximumPartitionBytes = PayloadExtractor.DefaultMaximumPartitionBytes)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        extractFiles ??= [];
        var tempSourceFiles = new List<string>();
        try
        {
            var (manifest, header) = await ReadManifestAsync(stream, cancellationToken).ConfigureAwait(false);
            var resolvedSourceImages = ResolveSourceImages(manifest, extractFiles, sourcePayloadPath, sourceImages, tempSourceFiles);
            var extractor = new PayloadExtractor(maximumPartitionBytes);
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
    /// <param name="filepath">Properties file path; must be non-blank and exist on disk. <para>属性文件路径；不得为空白且必须存在于磁盘上。</para></param>
    /// <returns>Parsed hashes and sizes plus the complete case-insensitive key/value map; absent keys yield empty
    /// strings and zero sizes rather than failure.
    /// <para>解析出的摘要与大小，以及完整的不区分大小写键值映射；缺失键返回空字符串或零大小，而非失败。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filepath"/> is <see langword="null"/>. <para><paramref name="filepath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filepath"/> is blank. <para><paramref name="filepath"/> 为空白。</para></exception>
    /// <exception cref="FileNotFoundException">The properties file does not exist. <para>属性文件不存在。</para></exception>
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