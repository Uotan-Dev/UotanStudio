using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Diagnostics;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Parsers;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;
using FileExtraction = UotanStudio.Firmware.SharedKernel.FileExtraction;
using OppFormatDetector = UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Parsers.FormatDetector;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Extraction;

public sealed partial class OppReader
{
    private const int DefaultBufferSize = 81920;

    internal const string ZipPassword = "flash@realme$50E7F7D847732396F1582CD62DD385ED7ABB0897";

    private readonly IOppCryptoProvider? _cryptoProvider;
    private readonly IOppLogger _logger;

    /// <summary>
    /// Initializes a new instance of the OppReader class.
    /// <para>初始化 OppReader 类的新实例。</para>
    /// </summary>
    public OppReader(IOppCryptoProvider? cryptoProvider = null, IOppLogger? logger = null)
    {
        _cryptoProvider = cryptoProvider;
        _logger = logger ?? NullOppLogger.Instance;
        OppoInitializer.EnsureInitialized();
    }

    /// <summary>
    /// Reads metadata from an Oppo firmware file at the specified path.
    /// <para>从指定文件路径读取 Oppo 固件文件的元信息。</para>
    /// </summary>
    public OppMetadata ReadMetadata(string filePath)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            FileOptions.SequentialScan);

        return ReadMetadataFromStream(stream);
    }

    /// <summary>
    /// Asynchronously reads metadata from an Oppo firmware file at the specified path.
    /// <para>异步从指定文件路径读取 Oppo 固件文件的元信息。</para>
    /// </summary>
    public async ValueTask<OppMetadata> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ReadMetadataFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads metadata from a stream containing an Oppo firmware file.
    /// <para>从流中读取 Oppo 固件文件的元信息。</para>
    /// </summary>
    public OppMetadata ReadMetadataFromStream(Stream stream)
    {
        Guard.ThrowIfNull(stream);

        var format = OppFormatDetector.DetectFormatFromStream(stream);
        var fileSize = stream.Length;


        if (format == OppFormat.OfpQc && IsZipFile(stream))
        {
            return ReadMetadataFromZipWrappedOfp(stream, fileSize);
        }


        var parser = ParserRegistry.GetParser(format);
        return parser.ParseMetadata(stream, fileSize, _cryptoProvider);
    }

    /// <summary>
    /// Asynchronously reads metadata from a stream containing an Oppo firmware file.
    /// <para>异步从流中读取 Oppo 固件文件的元信息。</para>
    /// </summary>
    public ValueTask<OppMetadata> ReadMetadataFromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();


        var result = ReadMetadataFromStream(stream);
        return new ValueTask<OppMetadata>(result);
    }

    /// <summary>
    /// Fully parses an Oppo firmware archive at the specified path.
    /// <para>完整解析指定文件路径的 Oppo 固件归档。</para>
    /// </summary>
    public OppArchive Parse(string filePath)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            FileOptions.SequentialScan);

        return ParseFromStream(stream);
    }

    /// <summary>
    /// Asynchronously fully parses an Oppo firmware archive at the specified path.
    /// <para>异步完整解析指定文件路径的 Oppo 固件归档。</para>
    /// </summary>
    public async Task<OppArchive> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ParseFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fully parses an Oppo firmware archive from a stream.
    /// <para>从流中完整解析 Oppo 固件归档。</para>
    /// </summary>
    public OppArchive ParseFromStream(Stream stream)
    {
        Guard.ThrowIfNull(stream);

        var metadata = ReadMetadataFromStream(stream);


        if (metadata.IsZipWrapped)
        {
            return ParseZipWrappedArchive(stream, metadata);
        }

        var entries = ParseEntriesFromStream(stream, metadata);

        return new OppArchive
        {
            Metadata = metadata,
            Entries = entries
        };
    }

    /// <summary>
    /// Asynchronously fully parses an Oppo firmware archive from a stream.
    /// <para>异步从流中完整解析 Oppo 固件归档。</para>
    /// </summary>
    public Task<OppArchive> ParseFromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        var result = ParseFromStream(stream);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Extracts an Oppo firmware archive at the specified path to an output directory.
    /// <para>将指定文件路径的 Oppo 固件归档提取到输出目录。</para>
    /// </summary>
    public OppExtractResult Extract(
        string filePath,
        string outputDir,
        OppExtractOptions? options = null,
        IProgress<OppExtractProgress>? progress = null)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);
        Guard.ThrowIfNullOrWhiteSpace(outputDir);

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            FileOptions.SequentialScan);

        return ExtractFromStream(stream, outputDir, options, progress);
    }

    /// <summary>
    /// Asynchronously extracts an Oppo firmware archive at the specified path to an output directory.
    /// <para>异步将指定文件路径的 Oppo 固件归档提取到输出目录。</para>
    /// </summary>
    public async Task<OppExtractResult> ExtractAsync(
        string filePath,
        string outputDir,
        OppExtractOptions? options = null,
        IProgress<OppExtractProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);
        Guard.ThrowIfNullOrWhiteSpace(outputDir);

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ExtractFromStreamAsync(stream, outputDir, options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts an Oppo firmware archive from a stream to an output directory.
    /// <para>从流中提取 Oppo 固件归档到输出目录。</para>
    /// </summary>
    public OppExtractResult ExtractFromStream(
        Stream stream,
        string outputDir,
        OppExtractOptions? options = null,
        IProgress<OppExtractProgress>? progress = null)
    {
        Guard.ThrowIfNull(stream);
        Guard.ThrowIfNullOrWhiteSpace(outputDir);

        var archive = ParseFromStream(stream);
        return ExtractCore(stream, archive, outputDir, options, progress);
    }

    /// <summary>
    /// Asynchronously extracts an Oppo firmware archive from a stream to an output directory.
    /// <para>异步从流中提取 Oppo 固件归档到输出目录。</para>
    /// </summary>
    public Task<OppExtractResult> ExtractFromStreamAsync(
        Stream stream,
        string outputDir,
        OppExtractOptions? options = null,
        IProgress<OppExtractProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(stream);
        Guard.ThrowIfNullOrWhiteSpace(outputDir);
        cancellationToken.ThrowIfCancellationRequested();

        var archive = ParseFromStream(stream);
        var result = ExtractCore(stream, archive, outputDir, options, progress, cancellationToken);
        return Task.FromResult(result);
    }

    #region Internal Core Implementation

    private IReadOnlyList<OppEntry> ParseEntriesFromStream(Stream stream, OppMetadata metadata)
    {
        var format = metadata.Format;
        var fileSize = stream.Length;

        var parser = ParserRegistry.GetParser(format);
        return parser.ParseEntries(stream, fileSize, metadata, _cryptoProvider);
    }

    private OppExtractResult ExtractCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        OppExtractOptions? options,
        IProgress<OppExtractProgress>? progress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var overwrite = options?.Overwrite ?? false;
        var keyId = options?.KeyId;

        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }


        if (archive.Metadata.Format == OppFormat.Ozip)
        {
            return ExtractOzipCore(stream, archive, outputDir, overwrite, keyId, progress, cancellationToken);
        }


        return archive.Metadata.IsZipWrapped
            ? ExtractZipWrappedCore(stream, archive, outputDir, overwrite, keyId, progress, cancellationToken)
            : ExtractEntriesCore(stream, archive, outputDir, overwrite, keyId, progress, cancellationToken);
    }

    private OppExtractResult ExtractZipWrappedCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        bool overwrite,
        string? keyId,
        IProgress<OppExtractProgress>? progress, CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"UotanStudio.Oppo.Ofp.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            ExtractZipToDirectory(stream, tempDir, cancellationToken);

            var innerFiles = Directory.GetFiles(tempDir, "*.ofp", SearchOption.AllDirectories);
            if (innerFiles.Length == 0)
            {
                innerFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
            }

            if (innerFiles.Length == 0)
            {
                throw new InvalidOppFormatException("ZIP-wrapped OFP contains no inner firmware file.");
            }

            using var innerStream = new FileStream(
                innerFiles[0],
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultBufferSize,
                FileOptions.SequentialScan);

            return ExtractEntriesCore(innerStream, archive, outputDir, overwrite, keyId, progress, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {

            }
        }
    }

    private OppExtractResult ExtractEntriesCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        bool overwrite,
        string? keyId,
        IProgress<OppExtractProgress>? progress, CancellationToken cancellationToken)
    {
        var entries = archive.Entries;
        var totalEntries = entries.Count;
        var completedEntries = 0;
        var extractedCount = 0;

        foreach (var entry in entries)
        {
            progress?.Report(new OppExtractProgress(totalEntries, completedEntries, entry.Name));
            cancellationToken.ThrowIfCancellationRequested();

            var sanitized = SanitizeFileName(entry.Name);
            if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "." || sanitized == "..")
            {
                sanitized = $"entry_{completedEntries}";
            }


            if (entry.Size <= 0)
            {
                _logger.Debug($"Skipping entry with zero size: {sanitized}");
                completedEntries++;
                continue;
            }

            var outputPath = FileExtraction.ResolvePath(outputDir, sanitized);
            ValidateOutputPath(outputDir, outputPath);

            if (File.Exists(outputPath) && !overwrite)
            {
                _logger.Warning($"Skipping existing file: {outputPath}");
                completedEntries++;
                continue;
            }

            var outputDirForEntry = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirForEntry) && !Directory.Exists(outputDirForEntry))
            {
                Directory.CreateDirectory(outputDirForEntry);
            }


            var parser = ParserRegistry.GetParser(archive.Metadata.Format);
            FileExtraction.WriteAtomic(outputPath, outputStream =>
            {
                using var limited = new CountingWriteStream(outputStream, entry.Size, leaveOpen: true,
                    cancellationToken: cancellationToken);
                parser.ExtractEntry(stream, limited, entry, archive.Metadata, keyId ?? archive.Metadata.KeyId, _cryptoProvider);
                if (limited.BytesWritten != entry.Size)
                    throw new InvalidOppFormatException($"Entry '{entry.Name}' extracted to an unexpected length.");
                cancellationToken.ThrowIfCancellationRequested();
            }, overwrite);

            extractedCount++;
            completedEntries++;

            progress?.Report(new OppExtractProgress(totalEntries, completedEntries, entry.Name));
        }

        return new OppExtractResult
        {
            ExtractedFileCount = extractedCount,
            OutputDirectory = outputDir,
            KeyId = keyId ?? archive.Metadata.KeyId
        };
    }

    /// <summary>
    /// Manually extracts a ZIP entry by its local file header (LFH) offset: locates the PK\x03\x04 header, reads name/extra lengths and the compression method, skips the header, then decompresses store/deflate data.
    /// <para>按本地文件头（LFH）偏移手动提取 zip 条目：定位 PK\x03\x04 头，读取文件名/extra 长度与压缩方法，跳过头部后按 store/deflate 解压数据。</para>
    /// </summary>
    private static void ExtractZipEntryByLocalHeader(Stream zip, OppEntry entry, Stream output)
    {
        zip.Position = entry.Offset;
        Span<byte> lfh = stackalloc byte[30];
        zip.ReadExactly(lfh);


        if (lfh[0] != 0x50 || lfh[1] != 0x4B || lfh[2] != 0x03 || lfh[3] != 0x04)
        {
            throw new InvalidDataException($"Invalid local file header at offset {entry.Offset}.");
        }

        var method = BinaryPrimitives.ReadUInt16LittleEndian(lfh[8..]);
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(lfh[26..]);
        var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(lfh[28..]);


        var compressedSize = entry.CompressedSize > 0
            ? entry.CompressedSize
            : BinaryPrimitives.ReadUInt32LittleEndian(lfh[18..]);
        var uncompressedSize = entry.Size > 0
            ? entry.Size
            : BinaryPrimitives.ReadUInt32LittleEndian(lfh[22..]);

        var dataStart = entry.Offset + 30L + nameLen + extraLen;
        if (dataStart > zip.Length)
        {
            throw new InvalidDataException($"Local header of '{entry.Name}' points beyond the file end.");
        }

        zip.Position = dataStart;
        using var bounded = new BoundedReadStream(zip, dataStart, compressedSize, leaveOpen: true);

        switch (method)
        {
            case 0:

                StreamCopy.CopyExactly(bounded, output, compressedSize);
                break;

            case 8:

                using (var counting = new CountingWriteStream(output, uncompressedSize, leaveOpen: true))
                using (var deflate = new DeflateStream(bounded, CompressionMode.Decompress, leaveOpen: true))
                {
                    deflate.CopyTo(counting);
                }
                break;

            default:
                throw new InvalidDataException($"Unsupported ZIP compression method {method} for '{entry.Name}'.");
        }
    }

    #endregion

    #region ZIP-wrapped OFP support

    /// <summary>
    /// Checks if the stream is a ZIP file.
    /// <para>检查流是否为 ZIP 文件。</para>
    /// </summary>
    private static bool IsZipFile(Stream stream)
    {
        if (stream.Length < 2)
        {
            return false;
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            var header = new byte[2];
            var bytesRead = stream.Read(header, 0, 2);
            return bytesRead == 2 && header[0] == 0x50 && header[1] == 0x4B;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Reads metadata from a ZIP-wrapped OFP file.
    /// <para>从 ZIP 包裹的 OFP 文件中读取元信息。</para>
    /// </summary>
    private OppMetadata ReadMetadataFromZipWrappedOfp(Stream stream, long fileSize)
    {
        _logger.Information("Detected ZIP-wrapped OFP file, extracting inner OFP...");


        var tempDir = Path.Combine(Path.GetTempPath(), $"UotanStudio.Oppo.Ofp.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            ExtractZipToDirectory(stream, tempDir);


            var innerFiles = Directory.GetFiles(tempDir, "*.ofp", SearchOption.AllDirectories);
            if (innerFiles.Length == 0)
            {

                innerFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
            }

            if (innerFiles.Length == 0)
            {
                throw new InvalidOppFormatException("ZIP-wrapped OFP contains no inner firmware file.");
            }


            using var innerStream = new FileStream(
                innerFiles[0],
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultBufferSize,
                FileOptions.SequentialScan);

            var innerFormat = OppFormatDetector.DetectFormatFromStream(innerStream);
            var parser = ParserRegistry.GetParser(innerFormat);
            var metadata = parser.ParseMetadata(innerStream, innerStream.Length, _cryptoProvider);


            return new OppMetadata
            {
                FormatName = metadata.FormatName,
                Format = metadata.Format,
                FormatVersion = metadata.FormatVersion,
                FileSize = fileSize,
                EntryCount = metadata.EntryCount,
                ProjectName = metadata.ProjectName,
                CpuModel = metadata.CpuModel,
                FlashType = metadata.FlashType,
                FirmwareName = metadata.FirmwareName,
                ProjectId = metadata.ProjectId,
                IsZipWrapped = true,
                KeyId = metadata.KeyId
            };
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {

            }
        }
    }

    /// <summary>
    /// Extracts a ZIP stream to the specified directory using the fixed password.
    /// <para>使用固定密码将 ZIP 流解压到指定目录。</para>
    /// </summary>
    private static void ExtractZipToDirectory(Stream stream, string outputDir, CancellationToken cancellationToken = default)
    {
        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/');
            var outputPath = FileExtraction.ResolvePath(outputDir, isDirectory ? entry.FullName.TrimEnd('/') : entry.FullName);
            if (isDirectory)
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            var entryDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
            {
                Directory.CreateDirectory(entryDir);
            }

            try
            {
                FileExtraction.WriteAtomic(outputPath, output =>
                {
                    using var entryStream = entry.Open();
                    StreamCopy.CopyExactly(entryStream, output, entry.Length, cancellationToken);
                    if (entryStream.ReadByte() != -1)
                        throw new InvalidDataException("ZIP entry exceeds its declared length.");
                    cancellationToken.ThrowIfCancellationRequested();
                });
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOppFormatException(
                    "The OFP file is wrapped in a password-protected ZIP archive that could not be decrypted " +
                    "with the built-in password. Please extract the ZIP file manually and then load the inner OFP file directly.",
                    ex);
            }
        }
    }

    /// <summary>
    /// Fully parses an archive (metadata + entries) from a ZIP-wrapped OFP file.
    /// <para>从 ZIP 包裹的 OFP 文件中完整解析归档（元数据 + 条目）。</para>
    /// </summary>
    private OppArchive ParseZipWrappedArchive(Stream stream, OppMetadata metadata)
    {
        _logger.Information("Parsing entries from ZIP-wrapped OFP file...");

        var tempDir = Path.Combine(Path.GetTempPath(), $"UotanStudio.Oppo.Ofp.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            ExtractZipToDirectory(stream, tempDir);

            var innerFiles = Directory.GetFiles(tempDir, "*.ofp", SearchOption.AllDirectories);
            if (innerFiles.Length == 0)
            {
                innerFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
            }

            if (innerFiles.Length == 0)
            {
                throw new InvalidOppFormatException("ZIP-wrapped OFP contains no inner firmware file.");
            }

            using var innerStream = new FileStream(
                innerFiles[0],
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultBufferSize,
                FileOptions.SequentialScan);

            var entries = ParseEntriesFromStream(innerStream, metadata);

            return new OppArchive
            {
                Metadata = metadata,
                Entries = entries
            };
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {

            }
        }
    }

    #endregion

    #region Helpers

    private static void ValidateOutputPath(string outputDir, string filePath)
    {
        var fullOutputDir = Path.GetFullPath(outputDir);
        var fullFilePath = Path.GetFullPath(filePath);
        if (!fullFilePath.StartsWith(fullOutputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !fullFilePath.Equals(fullOutputDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOppFormatException($"Entry path '{filePath}' attempts to write outside the output directory.");
        }
    }

    private static readonly HashSet<char> InvalidFileNameChars = [.. Path.GetInvalidFileNameChars()];

    /// <summary>
    /// Sanitizes illegal characters from a file name.
    /// <para>清理文件名中的非法字符。</para>
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            result.Append(InvalidFileNameChars.Contains(c) ? '_' : c);
        }

        return result.ToString();
    }

    #endregion
}