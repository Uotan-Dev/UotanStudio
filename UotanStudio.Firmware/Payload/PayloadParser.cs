using System.IO.Compression;
using UotanStudio.Firmware.Payload.Core;
using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Models;
using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload;

/// <summary>
/// Provides parsing and extraction services for Android OTA payload.bin files.
/// Supports both raw payload.bin files and OTA ZIP archives containing a payload.bin entry.
/// <para>提供 Android OTA payload.bin 文件的解析与提取服务。
/// 支持原始 payload.bin 文件以及包含 payload.bin 条目的 OTA ZIP 归档。</para>
/// </summary>
public static class PayloadParser
{
    #region File Type Detection

    private static readonly byte[] ZipMagicBytes = [0x50, 0x4B]; // "PK"
    private static readonly byte[] PayloadMagicBytes = [0x43, 0x72, 0x41, 0x55]; // "CrAU"

    /// <summary>
    /// Determines the payload type of a local file by reading its magic bytes.
    /// <para>通过读取魔术字节确定本地文件的载荷类型。</para>
    /// </summary>
    /// <param name="filepath">The path to the file to inspect.
    /// <para>要检查的文件路径。</para>
    /// </param>
    /// <returns>The detected <see cref="PayloadType"/>.
    /// <para>检测到的 <see cref="PayloadType"/>。</para>
    /// </returns>
    internal static PayloadType DetectPayloadType(string filepath)
    {
        using var f = File.OpenRead(filepath);
        return DetectPayloadTypeFromStream(f);
    }

    /// <summary>
    /// Determines the payload type from a stream by reading its magic bytes.
    /// The stream position is restored after detection.
    /// <para>通过读取魔术字节从流中确定载荷类型。检测后流位置会被恢复。</para>
    /// </summary>
    /// <param name="stream">The stream to inspect. Must be positioned at the beginning of the data.
    /// <para>要检查的流。必须定位在数据的起始位置。</para>
    /// </param>
    /// <returns>The detected <see cref="PayloadType"/>.
    /// <para>检测到的 <see cref="PayloadType"/>。</para>
    /// </returns>
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

    private static async Task<string> ExtractPayloadFromZipAsync(string filepath, CancellationToken cancellationToken = default)
    {
        using var zr = ZipFile.OpenRead(filepath);
        var zf = FindPayloadEntry(zr)
            ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
        var tempPath = TempFileHelper.CreateTempFile();
        using (var outFile = File.Create(tempPath))
        using (var zfStream = zf.Open())
        {
            await zfStream.CopyToAsync(outFile, cancellationToken).ConfigureAwait(false);
        }

        return tempPath;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Reads payload metadata from a local file, handling both raw payload.bin and ZIP-wrapped payloads.
    /// <para>从本地文件读取载荷元数据，支持原始 payload.bin 和 ZIP 包裹的载荷。</para>
    /// </summary>
    /// <param name="filepath">The path to the payload file or ZIP archive.
    /// <para>载荷文件或 ZIP 归档的路径。</para>
    /// </param>
    /// <returns>A <see cref="PayloadMetadata"/> instance containing the parsed metadata.
    /// <para>包含解析后元数据的 <see cref="PayloadMetadata"/> 实例。</para>
    /// </returns>
    /// <exception cref="InvalidManifestException">Thrown when payload.bin is not found in the ZIP archive.
    /// <para>当在 ZIP 归档中未找到 payload.bin 时抛出。</para>
    /// </exception>
    public static PayloadMetadata GetPayloadMetadata(string filepath)
    {
        Guard.ThrowIfNull(filepath, nameof(filepath));
        Guard.ThrowIfNullOrWhiteSpace(filepath, nameof(filepath), "File path cannot be empty or whitespace.");

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
    /// <param name="filepath">The path to the payload file or ZIP archive.
    /// <para>载荷文件或 ZIP 归档的路径。</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// <para>用于监视取消请求的令牌。</para>
    /// </param>
    /// <returns>A task whose result contains the parsed metadata.
    /// <para>结果包含解析后元数据的任务。</para>
    /// </returns>
    /// <exception cref="InvalidManifestException">Thrown when payload.bin is not found in the ZIP archive.
    /// <para>当在 ZIP 归档中未找到 payload.bin 时抛出。</para>
    /// </exception>
    public static async Task<PayloadMetadata> GetPayloadMetadataAsync(string filepath, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(filepath, nameof(filepath));
        Guard.ThrowIfNullOrWhiteSpace(filepath, nameof(filepath), "File path cannot be empty or whitespace.");
        var (manifest, header) = await OpenAndReadManifestAsync(filepath, cancellationToken).ConfigureAwait(false);
        return BuildMetadata(manifest, header);
    }

    /// <summary>
    /// Reads payload metadata from a stream.
    /// <para>从流中读取载荷元数据。</para>
    /// </summary>
    /// <param name="stream">The stream to read the payload from. Must be positioned at the beginning of the payload.
    /// <para>要从中读取载荷的流。必须定位在载荷的起始位置。</para>
    /// </param>
    /// <returns>A <see cref="PayloadMetadata"/> instance containing the parsed metadata.
    /// <para>包含解析后元数据的 <see cref="PayloadMetadata"/> 实例。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.
    /// <para><paramref name="stream"/> 为 <c>null</c>。</para>
    /// </exception>
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
    /// <param name="stream">The stream to read the payload from. Must be positioned at the beginning of the payload.
    /// <para>要从中读取载荷的流。必须定位在载荷的起始位置。</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// <para>用于监视取消请求的令牌。</para>
    /// </param>
    /// <returns>A task whose result contains the parsed metadata.
    /// <para>结果包含解析后元数据的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.
    /// <para><paramref name="stream"/> 为 <c>null</c>。</para>
    /// </exception>
    public static async Task<PayloadMetadata> GetPayloadMetadataAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        var (manifest, header) = await ReadManifestAsync(stream, cancellationToken).ConfigureAwait(false);
        return BuildMetadata(manifest, header);
    }

    /// <summary>
    /// Gets partition size information from a local payload file.
    /// <para>从本地载荷文件获取分区大小信息。</para>
    /// </summary>
    /// <param name="filepath">The path to the payload file or ZIP archive.
    /// <para>载荷文件或 ZIP 归档的路径。</para>
    /// </param>
    /// <returns>A list of <see cref="PartitionInfoData"/> objects with partition names and sizes.
    /// <para>包含分区名称和大小的 <see cref="PartitionInfoData"/> 对象列表。</para>
    /// </returns>
    public static List<PartitionInfoData> GetPartitionInfo(string filepath)
    {
        Guard.ThrowIfNull(filepath, nameof(filepath));
        Guard.ThrowIfNullOrWhiteSpace(filepath, nameof(filepath), "File path cannot be empty or whitespace.");
        var metadata = GetPayloadMetadata(filepath);
        return PartitionInfoHelper.ComputePartitionInfo(metadata.Manifest);
    }

    /// <summary>
    /// Asynchronously gets partition size information from a local payload file.
    /// <para>从本地载荷文件异步获取分区大小信息。</para>
    /// </summary>
    /// <param name="filepath">The path to the payload file or ZIP archive.
    /// <para>载荷文件或 ZIP 归档的路径。</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// <para>用于监视取消请求的令牌。</para>
    /// </param>
    /// <returns>A task whose result contains partition info list.
    /// <para>结果包含分区信息列表的任务。</para>
    /// </returns>
    public static async Task<List<PartitionInfoData>> GetPartitionInfoAsync(string filepath, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(filepath, nameof(filepath));
        Guard.ThrowIfNullOrWhiteSpace(filepath, nameof(filepath), "File path cannot be empty or whitespace.");
        var metadata = await GetPayloadMetadataAsync(filepath, cancellationToken).ConfigureAwait(false);
        return PartitionInfoHelper.ComputePartitionInfo(metadata.Manifest);
    }

    /// <summary>
    /// Extracts partitions from a local payload file. If <paramref name="extractFiles"/> is null or empty, all partitions are extracted.
    /// <para>从本地载荷文件中提取分区。如果 <paramref name="extractFiles"/> 为 null 或空，则提取所有分区。</para>
    /// </summary>
    /// <param name="filepath">The path to the payload file or ZIP archive.
    /// <para>载荷文件或 ZIP 归档的路径。</para>
    /// </param>
    /// <param name="extractFiles">Optional partition names to extract; null or empty extracts all.
    /// <para>可选的要提取的分区名称；null 或空则提取所有。</para>
    /// </param>
    /// <param name="outputDir">Output directory for extracted images.
    /// <para>提取镜像的输出目录。</para>
    /// </param>
    /// <exception cref="InvalidManifestException">Thrown when payload.bin is not found in the ZIP archive.
    /// <para>当在 ZIP 归档中未找到 payload.bin 时抛出。</para>
    /// </exception>
    /// <exception cref="PartitionExtractionException">Thrown when a partition extraction fails.
    /// <para>当分区提取失败时抛出。</para>
    /// </exception>
    public static void Extract(string filepath, string[]? extractFiles = null, string? outputDir = null)
    {
        Guard.ThrowIfNull(filepath, nameof(filepath));
        Guard.ThrowIfNullOrWhiteSpace(filepath, nameof(filepath), "File path cannot be empty or whitespace.");
        extractFiles ??= [];

        string? tempPath = null;
        try
        {
            var payloadType = DetectPayloadType(filepath);
            if (payloadType == PayloadType.ZipArchive)
            {
                tempPath = ExtractPayloadFromZip(filepath);
            }

            using var stream = File.OpenRead(tempPath ?? filepath);
            var (manifest, header) = ReadManifest(stream);
            new PayloadExtractor().ExtractPartitions(manifest, stream, header.BaseOffset, extractFiles, outputDir ?? string.Empty);
        }
        finally
        {
            if (tempPath != null)
            {
                TempFileHelper.SafeDelete(tempPath);
            }
        }
    }

    /// <summary>
    /// Asynchronously extracts partitions from a local payload file.
    /// <para>从本地载荷文件中异步提取分区。</para>
    /// </summary>
    /// <param name="filepath">The path to the payload file or ZIP archive.
    /// <para>载荷文件或 ZIP 归档的路径。</para>
    /// </param>
    /// <param name="extractFiles">Optional partition names to extract; null or empty extracts all.
    /// <para>可选的要提取的分区名称；null 或空则提取所有。</para>
    /// </param>
    /// <param name="outputDir">Output directory for extracted images.
    /// <para>提取镜像的输出目录。</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// <para>用于监视取消请求的令牌。</para>
    /// </param>
    /// <returns>A task representing the asynchronous extraction operation.
    /// <para>表示异步提取操作的任务。</para>
    /// </returns>
    /// <exception cref="InvalidManifestException">Thrown when payload.bin is not found in the ZIP archive.
    /// <para>当在 ZIP 归档中未找到 payload.bin 时抛出。</para>
    /// </exception>
    /// <exception cref="PartitionExtractionException">Thrown when a partition extraction fails.
    /// <para>当分区提取失败时抛出。</para>
    /// </exception>
    public static async Task ExtractAsync(string filepath, string[]? extractFiles = null, string? outputDir = null, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(filepath, nameof(filepath));
        Guard.ThrowIfNullOrWhiteSpace(filepath, nameof(filepath), "File path cannot be empty or whitespace.");
        extractFiles ??= [];

        string? tempPath = null;
        try
        {
            var payloadType = DetectPayloadType(filepath);
            if (payloadType == PayloadType.ZipArchive)
            {
                tempPath = await ExtractPayloadFromZipAsync(filepath, cancellationToken).ConfigureAwait(false);
            }

            using var stream = File.OpenRead(tempPath ?? filepath);
            var (manifest, header) = await ReadManifestAsync(stream, cancellationToken).ConfigureAwait(false);
            var extractor = new PayloadExtractor();
            await extractor.ExtractPartitionsAsync(manifest, stream, header.BaseOffset, extractFiles, outputDir ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (tempPath != null)
            {
                TempFileHelper.SafeDelete(tempPath);
            }
        }
    }

    /// <summary>
    /// Extracts partitions from a payload stream.
    /// <para>从载荷流中提取分区。</para>
    /// </summary>
    /// <param name="stream">The stream to read the payload from. Must be positioned at the beginning of the payload and support seeking.
    /// <para>要从中读取载荷的流。必须定位在载荷的起始位置并支持寻址。</para>
    /// </param>
    /// <param name="extractFiles">Optional partition names to extract; null or empty extracts all.
    /// <para>可选的要提取的分区名称；null 或空则提取所有。</para>
    /// </param>
    /// <param name="outputDir">Output directory for extracted images.
    /// <para>提取镜像的输出目录。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.
    /// <para><paramref name="stream"/> 为 <c>null</c>。</para>
    /// </exception>
    /// <exception cref="PartitionExtractionException">Thrown when a partition extraction fails.
    /// <para>当分区提取失败时抛出。</para>
    /// </exception>
    public static void Extract(Stream stream, string[]? extractFiles = null, string? outputDir = null)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        extractFiles ??= [];
        var (manifest, header) = ReadManifest(stream);
        new PayloadExtractor().ExtractPartitions(manifest, stream, header.BaseOffset, extractFiles, outputDir ?? string.Empty);
    }

    /// <summary>
    /// Asynchronously extracts partitions from a payload stream.
    /// <para>从载荷流中异步提取分区。</para>
    /// </summary>
    /// <param name="stream">The stream to read the payload from. Must be positioned at the beginning of the payload.
    /// <para>要从中读取载荷的流。必须定位在载荷的起始位置。</para>
    /// </param>
    /// <param name="extractFiles">Optional partition names to extract; null or empty extracts all.
    /// <para>可选的要提取的分区名称；null 或空则提取所有。</para>
    /// </param>
    /// <param name="outputDir">Output directory for extracted images.
    /// <para>提取镜像的输出目录。</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// <para>用于监视取消请求的令牌。</para>
    /// </param>
    /// <returns>A task representing the asynchronous extraction operation.
    /// <para>表示异步提取操作的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.
    /// <para><paramref name="stream"/> 为 <c>null</c>。</para>
    /// </exception>
    /// <exception cref="PartitionExtractionException">Thrown when a partition extraction fails.
    /// <para>当分区提取失败时抛出。</para>
    /// </exception>
    public static async Task ExtractAsync(Stream stream, string[]? extractFiles = null, string? outputDir = null, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(stream, nameof(stream));
        extractFiles ??= [];
        var (manifest, header) = await ReadManifestAsync(stream, cancellationToken).ConfigureAwait(false);
        var extractor = new PayloadExtractor();
        await extractor.ExtractPartitionsAsync(manifest, stream, header.BaseOffset, extractFiles, outputDir ?? string.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a payload_properties.txt file and returns the properties.
    /// <para>解析 payload_properties.txt 文件并返回属性。</para>
    /// </summary>
    /// <param name="filepath">The path to the payload_properties.txt file.
    /// <para>payload_properties.txt 文件的路径。</para>
    /// </param>
    /// <returns>A <see cref="PayloadProperties"/> instance containing the parsed properties.
    /// <para>包含解析属性的 <see cref="PayloadProperties"/> 实例。</para>
    /// </returns>
    public static PayloadProperties ParsePayloadProperties(string filepath)
    {
        Guard.ThrowIfNull(filepath, nameof(filepath));
        Guard.ThrowIfNullOrWhiteSpace(filepath, nameof(filepath), "File path cannot be empty or whitespace.");
        return PayloadPropertiesParser.ParseFile(filepath);
    }

    #endregion

    private static string ExtractPayloadFromZip(string filepath)
    {
        using var zr = ZipFile.OpenRead(filepath);
        var zf = FindPayloadEntry(zr)
            ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
        var tempPath = TempFileHelper.CreateTempFile();
        using (var outFile = File.Create(tempPath))
        using (var zfStream = zf.Open())
        {
            zfStream.CopyTo(outFile);
        }

        return tempPath;
    }
}