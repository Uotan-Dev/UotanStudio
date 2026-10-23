using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.OtaDelta.Payload.IO;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload;

/// <summary>
/// Entry point for reading and extracting Android OTA <c>payload.bin</c> files, accepting raw payload streams
/// and ZIP-wrapped update packages. Input is untrusted: sizes, extents and output paths are bounds-checked before use.
/// <para>Android OTA <c>payload.bin</c> 读取与提取的入口，支持原始载荷流与 ZIP 包裹的升级包。输入不可信：
/// 大小、extent 与输出路径在使用前均经边界检查。</para>
/// </summary>
/// <remarks>
/// <c>System.IO.Compression.ZipArchive</c> streams the payload entry for normal extraction but does not expose an
/// entry's raw local-header byte offset. A STORED payload is therefore located by walking the archive's local file
/// headers, which maps the entry back to its absolute data range so it can be read in place without decompression.
/// <para><c>System.IO.Compression.ZipArchive</c> 负责常规流式提取，但不暴露条目的原始本地头字节偏移。因此
/// STORED 载荷通过遍历归档的本地文件头定位，从而把条目映射回其绝对数据区间以便就地读取而无需解压。</para>
/// </remarks>
public static partial class PayloadParser
{

    private static readonly byte[] ZipMagicBytes = [0x50, 0x4B];
    private static readonly byte[] PayloadMagicBytes = [0x43, 0x72, 0x41, 0x55];
    private static readonly byte[] PayloadFilenameBytes = Encoding.ASCII.GetBytes(PayloadConstants.PayloadFilename);

    private const uint LocalFileHeaderSignature = 0x04034B50;
    private const uint CentralDirectorySignature = 0x02014B50;
    private const uint Zip64ExtraFieldType = 0x0001;
    private const int LocalFileHeaderSize = 30;
    private const int DataDescriptorFlag = 1 << 3;

    /// <summary>
    /// Determines the payload type of a local file by reading its magic bytes.
    /// <para>通过读取魔术字节确定本地文件的载荷类型。</para>
    /// </summary>
    internal static PayloadType DetectPayloadType(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return DetectPayloadTypeFromStream(stream);
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

    private static async Task<(DeltaArchiveManifest Manifest, PayloadHeader Header)> OpenAndReadManifestAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var (payloadStream, owner) = OpenPayloadStream(filePath);
        using (owner)
        using (payloadStream)
        {
            return await ReadManifestAsync(payloadStream, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ZipArchiveEntry? FindPayloadEntry(ZipArchive archive)
    {
        var entry = archive.GetEntry(PayloadConstants.PayloadFilename);
        if (entry != null)
        {
            return entry;
        }

        foreach (var candidate in archive.Entries)
        {
            if (string.Equals(candidate.Name, PayloadConstants.PayloadFilename, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens the payload bytes of a local payload file or ZIP update package. ZIP packages return the
    /// payload entry stream together with the owning archive, which the caller must dispose after the
    /// entry stream; raw payloads return the file stream with a <see langword="null"/> owner.
    /// <para>打开本地载荷文件或 ZIP 升级包的载荷字节。ZIP 包返回载荷条目流及其所属归档，调用方须在
    /// 条目流之后释放归档；原始载荷返回文件流，所有者为 <see langword="null"/>。</para>
    /// </summary>
    private static (Stream PayloadStream, ZipArchive? Owner) OpenPayloadStream(string filePath)
    {
        var payloadType = DetectPayloadType(filePath);
        switch (payloadType)
        {
            case PayloadType.ZipArchive:
                {
                    var archive = ZipContainer.OpenRead(filePath);
                    try
                    {
                        var payloadEntry = FindPayloadEntry(archive)
                            ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
                        return (payloadEntry.Open(), archive);
                    }
                    catch
                    {
                        archive.Dispose();
                        throw;
                    }
                }

            case PayloadType.RawPayload:
                return (File.OpenRead(filePath), null);

            case PayloadType.Unknown:
                throw new InvalidPayloadMagicException($"Unrecognized payload type: {payloadType}");
            default:
                throw new InvalidPayloadMagicException($"File does not appear to be a payload or ZIP archive: {filePath}");
        }
    }

    /// <summary>
    /// Materializes the ZIP-embedded payload to a temporary file (fallback for compressed entries),
    /// reporting decompressed bytes against <paramref name="totalBytes"/> as progress.
    /// <para>把 ZIP 内嵌的 payload 物化到临时文件（压缩条目的回退路径），并按解压字节逐块上报进度。</para>
    /// </summary>
    private static async Task<string> ExtractPayloadFromZipAsync(string filePath, CancellationToken cancellationToken,
        IProgress<(long Processed, long Total)>? byteProgress, long totalBytes)
    {
        var tempPath = TempFileHelper.CreateTempFile();
        var buffer = new byte[1 << 20];
        using (var archive = ZipContainer.OpenRead(filePath))
        {
            var payloadEntry = FindPayloadEntry(archive)
                ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
            using var entryStream = payloadEntry.Open();
            using var outFile = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan);
            byteProgress?.Report((0, totalBytes));
            long processed = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await entryStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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



    /// <summary>Reads payload metadata from a local file, handling both raw payload.bin and ZIP-wrapped payloads.
    /// <para>从本地文件读取载荷元数据，支持原始 payload.bin 和 ZIP 包裹的载荷。</para></summary>
    /// <param name="filePath">Local payload file or ZIP update package; must be non-blank.
    /// <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <returns>Block size, manifest/signature geometry, base offset, parsed manifest, max timestamp, dynamic partition metadata (empty when absent), signature fields and the list of non-empty partition names.
    /// <para>块大小、清单/签名几何、基准偏移、已解析清单、最大时间戳、动态分区元数据（缺失时为空对象）、 签名字段以及非空分区名列表。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is blank.
    /// <para><paramref name="filePath"/> 为空白。</para></exception>
    /// <exception cref="InvalidPayloadMagicException">The file magic is neither a ZIP header nor the payload <c>CrAU</c> magic.
    /// <para>文件魔数既非 ZIP 头也非载荷 <c>CrAU</c> 魔数。</para></exception>
    /// <exception cref="InvalidManifestException">The ZIP lacks a payload entry, or the manifest/signature bytes are truncated.
    /// <para>ZIP 缺少载荷条目，或清单/签名字节被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range.
    /// <para>清单 minor 版本超出支持范围。</para></exception>
    public static PayloadMetadata GetPayloadMetadata(string filePath)
    {
        Guard.ThrowIfNull(filePath);
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        var (payloadStream, owner) = OpenPayloadStream(filePath);
        using (owner)
        using (payloadStream)
        {
            var (manifest, header) = ReadManifest(payloadStream);
            return BuildMetadata(manifest, header);
        }
    }

    /// <summary>Asynchronously reads payload metadata from a local file.
    /// <para>从本地文件异步读取载荷元数据。</para></summary>
    /// <param name="filePath">Local payload file or ZIP update package; must be non-blank.
    /// <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <param name="cancellationToken">Cancels metadata reads and ZIP materialization.
    /// <para>取消元数据读取与 ZIP 物化。</para></param>
    /// <returns>The same metadata shape as <see cref="GetPayloadMetadata(string)"/>.
    /// <para>与 <see cref="GetPayloadMetadata(string)"/> 相同的元数据形态。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is blank.
    /// <para><paramref name="filePath"/> 为空白。</para></exception>
    /// <exception cref="InvalidPayloadMagicException">The file is neither a payload nor a ZIP archive.
    /// <para>文件既不是载荷也不是 ZIP 归档。</para></exception>
    /// <exception cref="InvalidManifestException">The payload entry is missing or the manifest is truncated.
    /// <para>载荷条目缺失或清单被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range.
    /// <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public static async Task<PayloadMetadata> GetPayloadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(filePath);
        Guard.ThrowIfNullOrWhiteSpace(filePath);
        var (manifest, header) = await OpenAndReadManifestAsync(filePath, cancellationToken).ConfigureAwait(false);
        return BuildMetadata(manifest, header);
    }

    /// <summary>Reads payload metadata from a stream.<para>从流中读取载荷元数据。</para></summary>
    /// <param name="stream">Payload stream positioned at the start of the header; the caller keeps ownership and the stream is not closed. When seekable, reading advances past the metadata signature; otherwise the signature bytes are consumed.
    /// <para>定位在头部起始处的载荷流；所有权仍属调用方且不会被关闭。可 seek 时读取会跳过元数据签名， 否则消耗签名字节。</para></param>
    /// <returns>The parsed metadata, same shape as <see cref="GetPayloadMetadata(string)"/>.
    /// <para>解析后的元数据，形态与 <see cref="GetPayloadMetadata(string)"/> 相同。</para></returns>
    /// <exception cref="InvalidManifestException">The manifest or metadata signature is truncated.
    /// <para>清单或元数据签名被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range.
    /// <para>清单 minor 版本超出支持范围。</para></exception>
    public static PayloadMetadata GetPayloadMetadata(Stream stream)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        var (manifest, header) = ReadManifest(stream);
        return BuildMetadata(manifest, header);
    }

    /// <summary>Asynchronously reads payload metadata from a stream.
    /// <para>从流中异步读取载荷元数据。</para></summary>
    /// <param name="stream">Payload stream positioned at the start of the header; not closed, and the signature region is skipped or consumed as in the synchronous overload.
    /// <para>定位在头部起始处的载荷流；不会被关闭，签名区域按同步重载的方式跳过或消耗。</para></param>
    /// <param name="cancellationToken">Cancels between reads; a pending read is aborted mid-flight.
    /// <para>在读取间隙取消；挂起的读取会被中途打断。</para></param>
    /// <returns>The parsed metadata, same shape as <see cref="GetPayloadMetadata(string)"/>.
    /// <para>解析后的元数据，形态与 <see cref="GetPayloadMetadata(string)"/> 相同。</para></returns>
    /// <exception cref="InvalidManifestException">The manifest or metadata signature is truncated.
    /// <para>清单或元数据签名被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range.
    /// <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public static async Task<PayloadMetadata> GetPayloadMetadataAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        var (manifest, header) = await ReadManifestAsync(stream, cancellationToken).ConfigureAwait(false);
        return BuildMetadata(manifest, header);
    }

    /// <summary>
    /// Returns the authoritative destination size in bytes for a partition update.
    /// <para>返回分区更新的权威目标大小（字节）。</para>
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
    /// <param name="filePath">Local payload file or ZIP update package; must be non-blank. <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <returns>One entry per manifest partition with its name and computed size. <para>每个清单分区一条记录，含分区名与计算大小。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>. <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is blank. <para><paramref name="filePath"/> 为空白。</para></exception>
    /// <exception cref="InvalidPayloadMagicException">The file is neither a payload nor a ZIP archive. <para>文件既不是载荷也不是 ZIP 归档。</para></exception>
    /// <exception cref="InvalidManifestException">The payload entry is missing or the manifest is truncated. <para>载荷条目缺失或清单被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
    public static List<PartitionInfoData> GetPartitionInfo(string filePath)
    {
        Guard.ThrowIfNull(filePath);
        Guard.ThrowIfNullOrWhiteSpace(filePath);
        var metadata = GetPayloadMetadata(filePath);
        return PartitionInfoHelper.ComputePartitionInfo(metadata.Manifest);
    }

    /// <summary>
    /// Asynchronously gets partition size information from a local payload file.
    /// <para>从本地载荷文件异步获取分区大小信息。</para>
    /// </summary>
    /// <param name="filePath">Local payload file or ZIP update package; must be non-blank. <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <param name="cancellationToken">Cancels the manifest read. <para>取消清单读取。</para></param>
    /// <returns>One entry per manifest partition with its name and computed size. <para>每个清单分区一条记录，含分区名与计算大小。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>. <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is blank. <para><paramref name="filePath"/> 为空白。</para></exception>
    /// <exception cref="InvalidManifestException">The payload entry is missing or the manifest is truncated. <para>载荷条目缺失或清单被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range. <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
    public static async Task<List<PartitionInfoData>> GetPartitionInfoAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(filePath);
        Guard.ThrowIfNullOrWhiteSpace(filePath);
        var metadata = await GetPayloadMetadataAsync(filePath, cancellationToken).ConfigureAwait(false);
        return PartitionInfoHelper.ComputePartitionInfo(metadata.Manifest);
    }

    /// <summary>Extracts partitions from a local payload file. ZIP-wrapped payloads first materialize the embedded payload.bin to a temporary file, then extract from that file.
    /// <para>从本地载荷文件中提取分区。ZIP 包裹的载荷先把内嵌 payload.bin 物化到临时文件，再从该文件提取。</para></summary>
    /// <param name="filePath">Local payload file or ZIP update package; must be non-blank.
    /// <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <param name="extractFiles">Partition names to extract; <see langword="null"/> or empty extracts every named partition in the manifest.
    /// <para>要提取的分区名；为 <see langword="null"/> 或空时提取清单中全部有名字分区。</para></param>
    /// <param name="outputDir">Destination directory for <c>{partition}.img</c> files; <see langword="null"/> or empty uses the process current directory and a missing directory is created. Partition names that would escape it are rejected.
    /// <para><c>{partition}.img</c> 的输出目录；为 <see langword="null"/> 或空时使用进程当前目录，缺失目录会被创建；逃逸该目录的分区名会被拒绝。</para></param>
    /// <param name="sourcePayloadPath">Full payload or ZIP used as base-image source for source-dependent operations; base images go to temporary files deleted in all paths.
    /// <para>为源依赖操作提供基座镜像的完整载荷或 ZIP；基座镜像写入临时文件并在任何路径下删除。</para></param>
    /// <param name="sourceImages">Per-partition pre-supplied base image paths; takes precedence over <paramref name="sourcePayloadPath"/> for a partition.
    /// <para>按分区预先提供的基座镜像路径；对某分区优先于 <paramref name="sourcePayloadPath"/>。</para></param>
    /// <param name="maximumPartitionBytes">Upper bound on one partition's output bytes, enforced before writing; must be positive.
    /// <para>单分区输出字节上限，写入前校验；必须为正数。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is blank.
    /// <para><paramref name="filePath"/> 为空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumPartitionBytes"/> is zero or negative.
    /// <para><paramref name="maximumPartitionBytes"/> 为零或负数。</para></exception>
    /// <exception cref="InvalidPayloadMagicException">The file is neither a payload nor a ZIP archive.
    /// <para>文件既不是载荷也不是 ZIP 归档。</para></exception>
    /// <exception cref="InvalidManifestException">The payload entry is missing or the manifest is truncated.
    /// <para>载荷条目缺失或清单被截断。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range.
    /// <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="PartitionExtractionException">A partition escapes the output directory, exceeds the byte cap, or lacks its required base image.
    /// <para>分区逃逸输出目录、超出字节上限，或缺少所需的基座镜像。</para></exception>
    /// <exception cref="Diff.Zucchini.ZucchiniException">A Zucchini operation blob is not decodable or declares an executable type this library cannot apply; it is raised before any partition output is created.
    /// <para>Zucchini 操作数据块无法解码，或声明了本库无法应用的可执行类型；该异常在创建任何分区输出之前抛出。</para></exception>
    public static void Extract(string filePath, string[]? extractFiles = null, string? outputDir = null, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null, long maximumPartitionBytes = PayloadExtractor.DefaultMaximumPartitionBytes)
        => ExtractFileCore(filePath, extractFiles, outputDir, sourcePayloadPath, sourceImages, maximumPartitionBytes, null, PayloadExtractor.DefaultMaximumZstdDictBytes);

    /// <summary>Extracts partitions from a payload file while threading external raw content dictionaries into REPLACE_ZSTD operations, honoring the <c>max_zstd_dict_raw_size</c> ceiling.
    /// <para>从载荷文件提取分区，并把外部原始内容字典贯穿到 REPLACE_ZSTD 操作，遵循 <c>max_zstd_dict_raw_size</c> 上限。</para></summary>
    /// <param name="filePath">Local payload file or ZIP update package; must be non-blank.
    /// <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <param name="extractFiles">Partition names to extract; null or empty extracts every named partition.
    /// <para>要提取的分区名；为 null 或空时提取全部有名字分区。</para></param>
    /// <param name="outputDir">Destination directory; null keeps the manifest-derived default.
    /// <para>目标目录；为 null 时使用按清单推导的默认值。</para></param>
    /// <param name="zstdDictionaries">Dictionaries keyed by partition name; a partition without an entry keeps the dictionary-free path.
    /// <para>按分区名索引的字典；缺少条目的分区沿用无字典路径。</para></param>
    /// <param name="maximumZstdDictRawSize">Ceiling for each supplied dictionary; larger dictionaries fail before decoding; must be positive.
    /// <para>单个字典的上限；超限字典在解码前失败；必须为正数。</para></param>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is blank.
    /// <para><paramref name="filePath"/> 为空白。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> or <paramref name="zstdDictionaries"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 或 <paramref name="zstdDictionaries"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumZstdDictRawSize"/> is not positive.
    /// <para><paramref name="maximumZstdDictRawSize"/> 不为正数。</para></exception>
    /// <exception cref="PayloadException">The payload is unsupported, a partition escapes the output directory, a dictionary exceeds its ceiling, or a frame cannot be decoded.
    /// <para>载荷不受支持、分区逃逸输出目录、字典超出上限，或帧无法解码。</para></exception>
    public static void ExtractWithZstdDictionaries(string filePath, string[]? extractFiles, string? outputDir,
        IReadOnlyDictionary<string, byte[]> zstdDictionaries,
        long maximumZstdDictRawSize = PayloadExtractor.DefaultMaximumZstdDictBytes)
        => ExtractFileCore(filePath, extractFiles, outputDir, null, null, PayloadExtractor.DefaultMaximumPartitionBytes,
            zstdDictionaries, maximumZstdDictRawSize);

    private static void ExtractFileCore(string filePath, string[]? extractFiles, string? outputDir,
        string? sourcePayloadPath, IReadOnlyDictionary<string, string>? sourceImages, long maximumPartitionBytes,
        IReadOnlyDictionary<string, byte[]>? zstdDictionaries, long maximumZstdDictRawSize)
    {
        Guard.ThrowIfNull(filePath);
        Guard.ThrowIfNullOrWhiteSpace(filePath);
        extractFiles ??= [];

        string? tempPath = null;
        var tempSourceFiles = new List<string>();
        try
        {
            using var plan = BuildExtractPlan(filePath, ref tempPath);
            var resolvedSourceImages = ResolveSourceImages(plan.Manifest, extractFiles, sourcePayloadPath, sourceImages, tempSourceFiles);
            new PayloadExtractor(maximumPartitionBytes, zstdDictionaries, maximumZstdDictRawSize).ExtractPartitions(
                plan.Manifest, plan.OuterStream, plan.Header.BaseOffset, extractFiles, outputDir ?? string.Empty, resolvedSourceImages,
                openPayloadStream: plan.OpenPayloadStream);
        }
        finally
        {
            if (tempPath != null)
            {
                TempFileHelper.SafeDelete(tempPath);
            }

            foreach (var tempFile in tempSourceFiles)
            {
                TempFileHelper.SafeDelete(tempFile);
            }
        }
    }

    /// <summary>Asynchronously extracts partitions from a local payload file. ZIP-wrapped payloads first materialize the embedded payload.bin to a temporary file with byte progress, then extract from that file.
    /// <para>从本地载荷文件中异步提取分区。ZIP 包裹的载荷先把内嵌 payload.bin 物化到临时文件并上报字节进度， 再从该文件提取。</para></summary>
    /// <param name="filePath">Local payload file or ZIP update package; must be non-blank.
    /// <para>本地载荷文件或 ZIP 升级包；不得为空白。</para></param>
    /// <param name="extractFiles">Partition names to extract; <see langword="null"/> or empty extracts every named partition in the manifest.
    /// <para>要提取的分区名；为 <see langword="null"/> 或空时提取清单中全部有名字分区。</para></param>
    /// <param name="outputDir">Destination directory for <c>{partition}.img</c> files; <see langword="null"/> or empty uses the process current directory and a missing directory is created. Partition names that would escape it are rejected.
    /// <para><c>{partition}.img</c> 的输出目录；为 <see langword="null"/> 或空时使用进程当前目录，缺失目录会被创建；逃逸该目录的分区名会被拒绝。</para></param>
    /// <param name="cancellationToken">Cancels materialization and extraction between reads.
    /// <para>在读取间隙取消物化与提取。</para></param>
    /// <param name="sourcePayloadPath">Full payload or ZIP used as base-image source for source-dependent operations; base images go to temporary files deleted in all paths.
    /// <para>为源依赖操作提供基座镜像的完整载荷或 ZIP；基座镜像写入临时文件并在任何路径下删除。</para></param>
    /// <param name="sourceImages">Per-partition pre-supplied base image paths; takes precedence over <paramref name="sourcePayloadPath"/> for a partition.
    /// <para>按分区预先提供的基座镜像路径；对某分区优先于 <paramref name="sourcePayloadPath"/>。</para></param>
    /// <param name="maximumPartitionBytes">Upper bound on one partition's output bytes, enforced before writing; must be positive.
    /// <para>单分区输出字节上限，写入前校验；必须为正数。</para></param>
    /// <param name="byteProgress">Optional byte-level progress callback receiving (Processed, Total) tuples covering the whole job; when a container (ZIP materialization) phase is present it maps to the leading bytes and the partition extraction to the remainder, so Total is the combined job size. Reports are throttled to at most one per 100 ms plus forced reports at stage boundaries and a final (total, total) flush. Total is 0 when no reliable estimate exists.
    /// <para>可选的字节级进度回调，覆盖整个作业并上报 (Processed, Total)；存在容器（ZIP 物化）阶段时 其映射到前置字节，分区提取映射到其余部分，Total 为作业总字节。上报节流为每 100ms 至多一次， 阶段边界与最终各上报一次 (total, total)。无可靠总量时 Total 为 0。</para></param>
    /// <returns>A task that completes when all selected partitions are extracted.
    /// <para>在全部所选分区提取完成后结束的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is blank.
    /// <para><paramref name="filePath"/> 为空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumPartitionBytes"/> is zero or negative.
    /// <para><paramref name="maximumPartitionBytes"/> 为零或负数。</para></exception>
    /// <exception cref="InvalidPayloadMagicException">The file is neither a payload nor a ZIP archive.
    /// <para>文件既不是载荷也不是 ZIP 归档。</para></exception>
    /// <exception cref="InvalidManifestException">The payload entry is missing, the manifest is truncated, or sizes exceed the supported range.
    /// <para>载荷条目缺失、清单被截断，或大小超出支持范围。</para></exception>
    /// <exception cref="DeltaPayloadNotSupportedException">The manifest minor version is outside the supported range.
    /// <para>清单 minor 版本超出支持范围。</para></exception>
    /// <exception cref="PartitionExtractionException">A partition escapes the output directory, exceeds the byte cap, or lacks its required base image.
    /// <para>分区逃逸输出目录、超出字节上限，或缺少所需的基座镜像。</para></exception>
    /// <exception cref="Diff.Zucchini.ZucchiniException">A Zucchini operation blob is not decodable or declares an executable type this library cannot apply; it is raised before any partition output is created.
    /// <para>Zucchini 操作数据块无法解码，或声明了本库无法应用的可执行类型；该异常在创建任何分区输出之前抛出。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public static async Task ExtractAsync(string filePath, string[]? extractFiles = null, string? outputDir = null, CancellationToken cancellationToken = default, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null, long maximumPartitionBytes = PayloadExtractor.DefaultMaximumPartitionBytes)
    {
        Guard.ThrowIfNull(filePath);
        Guard.ThrowIfNullOrWhiteSpace(filePath);
        extractFiles ??= [];

        string? tempPath = null;
        var tempSourceFiles = new List<string>();
        try
        {
            var (plan, planTempPath) = await BuildExtractPlanAsync(filePath, cancellationToken, byteProgress).ConfigureAwait(false);
            tempPath = planTempPath;
            using var planDisposable = plan;
            var resolvedSourceImages = ResolveSourceImages(plan.Manifest, extractFiles, sourcePayloadPath, sourceImages, tempSourceFiles);

            IProgress<(long Processed, long Total)>? sink = byteProgress;
            if (plan.ContainerOffset > 0 && byteProgress is not null)
            {
                sink = new OffsetByteProgress(byteProgress, plan.ContainerOffset, plan.TotalBytes);
            }

            var extractor = new PayloadExtractor(maximumPartitionBytes, null, PayloadExtractor.DefaultMaximumZstdDictBytes);
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

            foreach (var tempFile in tempSourceFiles)
            {
                TempFileHelper.SafeDelete(tempFile);
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
    /// <exception cref="Diff.Zucchini.ZucchiniException">A Zucchini operation blob is not decodable or declares an executable type this library cannot apply; it is raised before any partition output is created.
    /// <para>Zucchini 操作数据块无法解码，或声明了本库无法应用的可执行类型；该异常在创建任何分区输出之前抛出。</para></exception>
    public static void Extract(Stream stream, string[]? extractFiles = null, string? outputDir = null, string? sourcePayloadPath = null, IReadOnlyDictionary<string, string>? sourceImages = null, long maximumPartitionBytes = PayloadExtractor.DefaultMaximumPartitionBytes)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        extractFiles ??= [];
        var tempSourceFiles = new List<string>();
        try
        {
            var (manifest, header) = ReadManifest(stream);
            var resolvedSourceImages = ResolveSourceImages(manifest, extractFiles, sourcePayloadPath, sourceImages, tempSourceFiles);
            new PayloadExtractor(maximumPartitionBytes, null, PayloadExtractor.DefaultMaximumZstdDictBytes).ExtractPartitions(manifest, stream, header.BaseOffset, extractFiles, outputDir ?? string.Empty, resolvedSourceImages);
        }
        finally
        {
            foreach (var tempFile in tempSourceFiles)
            {
                TempFileHelper.SafeDelete(tempFile);
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
    /// <exception cref="Diff.Zucchini.ZucchiniException">A Zucchini operation blob is not decodable or declares an executable type this library cannot apply; it is raised before any partition output is created.
    /// <para>Zucchini 操作数据块无法解码，或声明了本库无法应用的可执行类型；该异常在创建任何分区输出之前抛出。</para></exception>
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
            var extractor = new PayloadExtractor(maximumPartitionBytes, null, PayloadExtractor.DefaultMaximumZstdDictBytes);
            await extractor.ExtractPartitionsAsync(manifest, stream, header.BaseOffset, extractFiles, outputDir ?? string.Empty, cancellationToken, resolvedSourceImages, byteProgress).ConfigureAwait(false);
        }
        finally
        {
            foreach (var tempFile in tempSourceFiles)
            {
                TempFileHelper.SafeDelete(tempFile);
            }
        }
    }

    /// <summary>
    /// Parses a payload_properties.txt file and returns the properties.
    /// <para>解析 payload_properties.txt 文件并返回属性。</para>
    /// </summary>
    /// <param name="filePath">Properties file path; must be non-blank and exist on disk. <para>属性文件路径；不得为空白且必须存在于磁盘上。</para></param>
    /// <returns>Parsed hashes and sizes plus the complete case-insensitive key/value map; absent keys yield empty
    /// strings and zero sizes rather than failure.
    /// <para>解析出的摘要与大小，以及完整的不区分大小写键值映射；缺失键返回空字符串或零大小，而非失败。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>. <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is blank. <para><paramref name="filePath"/> 为空白。</para></exception>
    /// <exception cref="FileNotFoundException">The properties file does not exist. <para>属性文件不存在。</para></exception>
    public static PayloadProperties ParsePayloadProperties(string filePath)
    {
        Guard.ThrowIfNull(filePath);
        Guard.ThrowIfNullOrWhiteSpace(filePath);
        return PayloadPropertiesParser.ParseFile(filePath);
    }



    /// <summary>
    /// Builds a synchronous extract plan (manifest, header, outer stream, per-partition payload
    /// handle) resolving the payload source once. ZIP payloads always materialize the embedded
    /// payload.bin to a temporary copy recorded in <paramref name="tempPath"/>.
    /// <para>构建同步提取计划（清单、头、外层流、每分区载荷句柄），一次性解析载荷来源。ZIP 载荷总是把
    /// 内嵌 payload.bin 物化到临时副本并记录到 <paramref name="tempPath"/>。</para>
    /// </summary>
    private static ExtractPlan BuildExtractPlan(string filePath, ref string? tempPath)
    {
        var payloadType = DetectPayloadType(filePath);
        switch (payloadType)
        {
            case PayloadType.RawPayload:
                {
                    var outer = File.OpenRead(filePath);
                    try
                    {
                        var (manifest, header) = ReadManifest(outer);
                        return new ExtractPlan(manifest, header, outer, RandomAccessOpener(filePath),
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
                    var (entryStream, owner) = OpenPayloadStream(filePath);
                    DeltaArchiveManifest manifest;
                    PayloadHeader header;
                    using (owner)
                    using (entryStream)
                    {
                        (manifest, header) = ReadManifest(entryStream);
                    }

                    if (TryGetStoredPayloadLocation(filePath, out var dataStart, out var payloadLength))
                    {
                        return BuildStoredZipPlan(filePath, manifest, header, dataStart, payloadLength);
                    }

                    tempPath = ExtractPayloadFromZip(filePath);
                    return BuildMaterializedZipPlan(tempPath, manifest, header, PayloadEntryUncompressedLength(filePath));
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
    private static async Task<(ExtractPlan Plan, string? TempPath)> BuildExtractPlanAsync(string filePath, CancellationToken cancellationToken,
        IProgress<(long Processed, long Total)>? byteProgress)
    {
        var payloadType = DetectPayloadType(filePath);
        switch (payloadType)
        {
            case PayloadType.RawPayload:
                {
                    var outer = File.OpenRead(filePath);
                    try
                    {
                        var (manifest, header) = await ReadManifestAsync(outer, cancellationToken).ConfigureAwait(false);
                        return (new ExtractPlan(manifest, header, outer, RandomAccessOpener(filePath),
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
                    var (entryStream, owner) = OpenPayloadStream(filePath);
                    DeltaArchiveManifest manifest;
                    PayloadHeader header;
                    using (owner)
                    using (entryStream)
                    {
                        (manifest, header) = await ReadManifestAsync(entryStream, cancellationToken).ConfigureAwait(false);
                    }

                    if (TryGetStoredPayloadLocation(filePath, out var dataStart, out var payloadLength))
                    {
                        return (BuildStoredZipPlan(filePath, manifest, header, dataStart, payloadLength), null);
                    }

                    var containerTotal = PayloadEntryUncompressedLength(filePath);
                    long totalBytes;
                    try
                    {
                        totalBytes = checked(containerTotal + ExtractTotalBytes(manifest));
                    }
                    catch (OverflowException ex)
                    {
                        throw new InvalidManifestException("Payload and extraction sizes exceed the supported range.", ex);
                    }

                    var tempFile = await ExtractPayloadFromZipAsync(filePath, cancellationToken, byteProgress, totalBytes).ConfigureAwait(false);
                    return (BuildMaterializedZipPlan(tempFile, manifest, header, containerTotal), tempFile);
                }

            default:
                throw new InvalidPayloadMagicException($"Unrecognized payload type: {payloadType}");
        }
    }

    /// <summary>
    /// Buffer size used when reopening payload files for the random-access reads performed during extraction.
    /// <para>提取期间以随机访问方式重新打开载荷文件时使用的缓冲区大小。</para>
    /// </summary>
    private const int PlanStreamBufferSize = 81920;

    private static Func<Stream> RandomAccessOpener(string path)
        => () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, PlanStreamBufferSize, FileOptions.RandomAccess);

    /// <summary>
    /// Builds the extract plan for a ZIP package whose payload stays stored inside the archive; the outer
    /// stream is the package file and per-partition streams decode the stored entry directly.
    /// <para>为载荷仍以存储方式驻留归档内的 ZIP 包构建提取计划；外层流是包文件本身，分区流直接解码存储条目。</para>
    /// </summary>
    private static ExtractPlan BuildStoredZipPlan(string filePath, DeltaArchiveManifest manifest, PayloadHeader header,
        long dataStart, long payloadLength)
    {
        var outer = File.OpenRead(filePath);
        try
        {
            return new ExtractPlan(manifest, header, outer, () => new StoredZipEntryStream(filePath, dataStart, payloadLength),
                0, ExtractTotalBytes(manifest));
        }
        catch
        {
            outer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the extract plan over a materialized standalone payload file (an extracted raw payload or the
    /// ZIP-embedded payload.bin copied to a temporary file), reporting <paramref name="containerOffset"/> as the
    /// leading container byte count.
    /// <para>基于已物化的独立载荷文件（解出的原始载荷或复制到临时文件的 ZIP 内嵌 payload.bin）构建提取计划，
    /// 并把 <paramref name="containerOffset"/> 作为前置容器字节量上报。</para>
    /// </summary>
    private static ExtractPlan BuildMaterializedZipPlan(string payloadPath, DeltaArchiveManifest manifest, PayloadHeader header,
        long containerOffset)
    {
        var outer = File.OpenRead(payloadPath);
        try
        {
            return new ExtractPlan(manifest, header, outer, RandomAccessOpener(payloadPath), containerOffset,
                ExtractTotalBytes(manifest));
        }
        catch
        {
            outer.Dispose();
            throw;
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
    private static long PayloadEntryUncompressedLength(string filePath)
    {
        using var archive = ZipContainer.OpenRead(filePath);
        var payloadEntry = FindPayloadEntry(archive)
            ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
        return payloadEntry.Length;
    }

    /// <summary>
    /// Locates a STORED (uncompressed) payload.bin entry inside a ZIP archive so extraction can read it
    /// in place. Returns whether the entry is STORED; when true, <paramref name="dataStart"/> is the
    /// absolute byte offset of the entry data within <paramref name="filePath"/> and
    /// <paramref name="length"/> is the payload byte count.
    /// <para>在 ZIP 归档中定位 STORED（未压缩）的 payload.bin 条目，以便解包原址直读。返回是否为
    /// STORED 条目；为 true 时 <paramref name="dataStart"/> 是条目数据在 <paramref name="filePath"/>
    /// 内的绝对字节偏移，<paramref name="length"/> 为载荷字节数。</para>
    /// </summary>
    /// <param name="filePath">Path of the ZIP archive; opened read-only and closed before returning.
    /// <para>ZIP 归档路径；以只读方式打开并在返回前关闭。</para></param>
    /// <param name="dataStart">Absolute offset of the entry data, or zero when the entry is not STORED.
    /// <para>条目数据的绝对偏移；条目不是 STORED 时为零。</para></param>
    /// <param name="length">Stored payload byte count, or zero when the entry is not STORED.
    /// <para>STORED 载荷字节数；条目不是 STORED 时为零。</para></param>
    /// <returns>
    /// <see langword="true"/> only for a STORED payload.bin whose local header carries its own sizes; a compressed,
    /// data-descriptor, ZIP64-indeterminate or absent entry reports <see langword="false"/> so the caller
    /// materializes the payload instead.
    /// <para>仅当 payload.bin 为 STORED 且其本地头自带大小时返回 <see langword="true"/>；被压缩、带数据描述符、
    /// ZIP64 信息不可判定或缺失的条目返回 <see langword="false"/>，由调用方改为物化载荷。</para></returns>
    private static bool TryGetStoredPayloadLocation(string filePath, out long dataStart, out long length)
    {
        dataStart = 0;
        length = 0;
        using var input = File.OpenRead(filePath);
        var header = new byte[LocalFileHeaderSize];
        while (input.Position + header.Length <= input.Length)
        {
            input.ReadExactly(header);
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (signature == CentralDirectorySignature)
            {
                return false;
            }

            if (signature != LocalFileHeaderSignature)
            {
                return false;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            var compressedSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18));
            var uncompressedSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            if (input.Length - input.Position < nameLength + (long)extraLength)
            {
                return false;
            }

            var name = new byte[nameLength];
            input.ReadExactly(name);
            var extra = new byte[extraLength];
            input.ReadExactly(extra);
            if ((compressedSize == uint.MaxValue || uncompressedSize == uint.MaxValue)
                && !TryReadZip64Sizes(extra, ref uncompressedSize, ref compressedSize))
            {
                return false;
            }

            if (method == 0 && (flags & DataDescriptorFlag) == 0 && compressedSize == uncompressedSize
                && name.AsSpan().SequenceEqual(PayloadFilenameBytes))
            {
                dataStart = input.Position;
                length = compressedSize;
                return true;
            }

            var nextEntry = input.Position + compressedSize;
            if (nextEntry < input.Position || nextEntry > input.Length)
            {
                return false;
            }

            input.Position = nextEntry;
        }

        return false;
    }

    /// <summary>
    /// Resolves the ZIP64 extended-information extra field for a local file header whose 32-bit sizes are
    /// saturated, so a payload larger than 4 GiB still maps to its true data range.
    /// <para>为 32 位大小已饱和的本地文件头解析 ZIP64 扩展信息额外字段，使超过 4 GiB 的载荷仍能映射到真实
    /// 数据区间。</para>
    /// </summary>
    /// <param name="extra">Raw extra-field bytes of the local file header. <para>本地文件头的额外字段原始字节。</para></param>
    /// <param name="uncompressedSize">Value read from the header, replaced when it was saturated.
    /// <para>从头部读出的大小，饱和时被替换。</para></param>
    /// <param name="compressedSize">Value read from the header, replaced when it was saturated.
    /// <para>从头部读出的大小，饱和时被替换。</para></param>
    /// <returns>
    /// <see langword="true"/> when every saturated size was resolved; <see langword="false"/> for a malformed
    /// extra field or a missing ZIP64 record, which makes the caller fall back to materialization.
    /// <para>所有饱和大小均解析成功时为 <see langword="true"/>；额外字段损坏或缺少 ZIP64 记录时为
    /// <see langword="false"/>，调用方随即回退到物化路径。</para></returns>
    private static bool TryReadZip64Sizes(ReadOnlySpan<byte> extra, ref long uncompressedSize, ref long compressedSize)
    {
        for (var offset = 0; offset + 4 <= extra.Length;)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(extra[offset..]);
            var fieldLength = BinaryPrimitives.ReadUInt16LittleEndian(extra[(offset + 2)..]);
            if (fieldLength > extra.Length - offset - 4)
            {
                return false;
            }

            if (type == Zip64ExtraFieldType)
            {
                var values = extra.Slice(offset + 4, fieldLength);
                var cursor = 0;
                if (uncompressedSize == uint.MaxValue)
                {
                    if (cursor + sizeof(ulong) > values.Length
                        || !TryReadZip64Size(values[cursor..], out uncompressedSize))
                    {
                        return false;
                    }

                    cursor += sizeof(ulong);
                }

                if (compressedSize == uint.MaxValue
                    && (cursor + sizeof(ulong) > values.Length
                        || !TryReadZip64Size(values[cursor..], out compressedSize)))
                {
                    return false;
                }

                return true;
            }

            offset += 4 + fieldLength;
        }

        return false;
    }

    /// <summary>
    /// Reads one ZIP64 64-bit size, rejecting values that cannot be represented as a signed byte offset.
    /// <para>读取一个 ZIP64 的 64 位大小，并拒绝无法表示为有符号字节偏移的取值。</para>
    /// </summary>
    /// <param name="source">Extra-field bytes starting at the 64-bit value. <para>从该 64 位值开始的额外字段字节。</para></param>
    /// <param name="size">Resolved size. <para>解析出的大小。</para></param>
    /// <returns><see langword="true"/> when the value fits; otherwise <see langword="false"/>.
    /// <para>取值可表示时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    private static bool TryReadZip64Size(ReadOnlySpan<byte> source, out long size)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(source);
        if (value > long.MaxValue)
        {
            size = 0;
            return false;
        }

        size = (long)value;
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

}