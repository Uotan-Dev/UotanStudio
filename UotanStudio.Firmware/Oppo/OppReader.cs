using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Diagnostics;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;
using UotanStudio.Firmware.Oppo.Parsers;

namespace UotanStudio.Firmware.Oppo;

/// <summary>
/// <para>提供 Oppo 固件文件的无状态读取操作。</para>
/// Provides stateless read operations for Oppo firmware files.
/// </summary>
/// <remarks>
/// <para>此类型是线程安全的，可跨线程调用。</para>
/// This type is thread-safe and can be called across threads.
/// </remarks>
public sealed class OppReader
{
    private const int DefaultBufferSize = 81920;

    /// <summary>
    /// <para>ZIP 包裹的 OFP 文件的固定解压密码。</para>
    /// The fixed password for extracting ZIP-wrapped OFP files.
    /// </summary>
    internal const string ZipPassword = "flash@realme$50E7F7D847732396F1582CD62DD385ED7ABB0897";

    private readonly IOppCryptoProvider? _cryptoProvider;
    private readonly IOppLogger _logger;

    /// <summary>
    /// <para>初始化 <see cref="OppReader"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="OppReader"/> class.
    /// </summary>
    /// <param name="cryptoProvider">
    /// <para>可选的加密提供者实例，为 null 时使用默认实现。</para>
    /// Optional crypto provider instance; uses default implementation when null.
    /// </param>
    /// <param name="logger">
    /// <para>可选的日志记录器实例，为 null 时不记录日志。</para>
    /// Optional logger instance; no logging when null.
    /// </param>
    public OppReader(IOppCryptoProvider? cryptoProvider = null, IOppLogger? logger = null)
    {
        _cryptoProvider = cryptoProvider;
        _logger = logger ?? NullOppLogger.Instance;
        OppoInitializer.EnsureInitialized();
    }

    /// <summary>
    /// <para>从指定文件路径读取 Oppo 固件文件的元信息。</para>
    /// Reads metadata from an Oppo firmware file at the specified path.
    /// </summary>
    /// <param name="filePath"><para>固件文件路径。</para>The path to the firmware file.</param>
    /// <returns><para>包含文件基本信息的对象。</para>An object containing basic file information.</returns>
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
    /// <para>异步从指定文件路径读取 Oppo 固件文件的元信息。</para>
    /// Asynchronously reads metadata from an Oppo firmware file at the specified path.
    /// </summary>
    /// <param name="filePath"><para>固件文件路径。</para>The path to the firmware file.</param>
    /// <param name="cancellationToken"><para>取消操作的令牌。</para>The token to cancel the operation.</param>
    /// <returns><para>包含文件基本信息的对象。</para>An object containing basic file information.</returns>
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
    /// <para>从流中读取 Oppo 固件文件的元信息。</para>
    /// Reads metadata from a stream containing an Oppo firmware file.
    /// </summary>
    /// <param name="stream"><para>可读取的流。调用方负责释放流。</para>A readable stream. The caller is responsible for disposing the stream.</param>
    /// <returns><para>包含文件基本信息的对象。</para>An object containing basic file information.</returns>
    public OppMetadata ReadMetadataFromStream(Stream stream)
    {
        Guard.ThrowIfNull(stream);

        var format = FormatDetector.DetectFormatFromStream(stream);
        var fileSize = stream.Length;

        // Handle ZIP-wrapped OFP: extract inner OFP and process it
        if (format == OppFormat.OfpQc && IsZipFile(stream))
        {
            return ReadMetadataFromZipWrappedOfp(stream, fileSize);
        }

        // Use ParserRegistry to find the appropriate parser
        var parser = ParserRegistry.GetParser(format);
        return parser.ParseMetadata(stream, fileSize, _cryptoProvider);
    }

    /// <summary>
    /// <para>异步从流中读取 Oppo 固件文件的元信息。</para>
    /// Asynchronously reads metadata from a stream containing an Oppo firmware file.
    /// </summary>
    public ValueTask<OppMetadata> ReadMetadataFromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        // Metadata parsing is primarily synchronous I/O; wrap in ValueTask for API consistency
        var result = ReadMetadataFromStream(stream);
        return new ValueTask<OppMetadata>(result);
    }

    /// <summary>
    /// <para>完整解析指定文件路径的 Oppo 固件归档。</para>
    /// Fully parses an Oppo firmware archive at the specified path.
    /// </summary>
    /// <param name="filePath"><para>固件文件路径。</para>The path to the firmware file.</param>
    /// <returns><para>完全解析的固件归档对象。</para>A fully parsed firmware archive object.</returns>
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
    /// <para>异步完整解析指定文件路径的 Oppo 固件归档。</para>
    /// Asynchronously fully parses an Oppo firmware archive at the specified path.
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
    /// <para>从流中完整解析 Oppo 固件归档。</para>
    /// Fully parses an Oppo firmware archive from a stream.
    /// </summary>
    public OppArchive ParseFromStream(Stream stream)
    {
        Guard.ThrowIfNull(stream);

        var metadata = ReadMetadataFromStream(stream);

        // For ZIP-wrapped OFP files, the inner OFP must be extracted
        // and parsed separately since the outer stream is a ZIP, not OFP
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
    /// <para>异步从流中完整解析 Oppo 固件归档。</para>
    /// Asynchronously fully parses an Oppo firmware archive from a stream.
    /// </summary>
    public Task<OppArchive> ParseFromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        var result = ParseFromStream(stream);
        return Task.FromResult(result);
    }

    /// <summary>
    /// <para>将指定文件路径的 Oppo 固件归档提取到输出目录。</para>
    /// Extracts an Oppo firmware archive at the specified path to an output directory.
    /// </summary>
    /// <param name="filePath"><para>固件文件路径。</para>The path to the firmware file.</param>
    /// <param name="outputDir"><para>输出目录路径。</para>The output directory path.</param>
    /// <param name="options"><para>提取选项，为 null 时使用默认选项。</para>Extraction options; uses default options when null.</param>
    /// <param name="progress"><para>进度报告器，为 null 时不报告进度。</para>Progress reporter; no progress reporting when null.</param>
    /// <returns><para>提取操作的结果。</para>The result of the extraction operation.</returns>
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
    /// <para>异步将指定文件路径的 Oppo 固件归档提取到输出目录。</para>
    /// Asynchronously extracts an Oppo firmware archive at the specified path to an output directory.
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
    /// <para>从流中提取 Oppo 固件归档到输出目录。</para>
    /// Extracts an Oppo firmware archive from a stream to an output directory.
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
    /// <para>异步从流中提取 Oppo 固件归档到输出目录。</para>
    /// Asynchronously extracts an Oppo firmware archive from a stream to an output directory.
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
        var result = ExtractCore(stream, archive, outputDir, options, progress);
        return Task.FromResult(result);
    }

    #region Internal Core Implementation

    private IReadOnlyList<OppEntry> ParseEntriesFromStream(Stream stream, OppMetadata metadata)
    {
        var format = metadata.Format;
        var fileSize = metadata.FileSize;

        var parser = ParserRegistry.GetParser(format);
        return parser.ParseEntries(stream, fileSize, metadata, _cryptoProvider);
    }

    private OppExtractResult ExtractCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        OppExtractOptions? options,
        IProgress<OppExtractProgress>? progress)
    {
        var overwrite = options?.Overwrite ?? false;
        var keyId = options?.KeyId;

        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // OZip needs a dedicated extraction path: decrypt the whole file once,
        // then copy each inner ZIP entry to the output directory.
        if (archive.Metadata.Format == OppFormat.Ozip)
        {
            return ExtractOzipCore(stream, archive, outputDir, overwrite, keyId, progress);
        }

        // ZIP-wrapped OFP: the entries belong to the inner OFP file, so the inner
        // file must be re-extracted before the generic entry loop can run.
        return archive.Metadata.IsZipWrapped
            ? ExtractZipWrappedCore(stream, archive, outputDir, overwrite, keyId, progress)
            : ExtractEntriesCore(stream, archive, outputDir, overwrite, keyId, progress);
    }

    private OppExtractResult ExtractZipWrappedCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        bool overwrite,
        string? keyId,
        IProgress<OppExtractProgress>? progress)
    {
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

            return ExtractEntriesCore(innerStream, archive, outputDir, overwrite, keyId, progress);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private OppExtractResult ExtractEntriesCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        bool overwrite,
        string? keyId,
        IProgress<OppExtractProgress>? progress)
    {
        var entries = archive.Entries;
        var totalEntries = entries.Count;
        var completedEntries = 0;
        var extractedCount = 0;

        foreach (var entry in entries)
        {
            progress?.Report(new OppExtractProgress(totalEntries, completedEntries, entry.Name));

            var sanitized = SanitizeFileName(entry.Name);
            if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "." || sanitized == "..")
            {
                sanitized = $"entry_{completedEntries}";
            }

            // Skip entries with zero or negative size to avoid creating empty output files
            if (entry.Size <= 0)
            {
                _logger.Debug($"Skipping entry with zero size: {sanitized}");
                completedEntries++;
                continue;
            }

            var outputPath = Path.Combine(outputDir, sanitized);
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

            // Use the parser registry to extract entries
            var parser = ParserRegistry.GetParser(archive.Metadata.Format);
            using var outputStream = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DefaultBufferSize,
                FileOptions.SequentialScan);
            parser.ExtractEntry(stream, outputStream, entry, archive.Metadata, keyId ?? archive.Metadata.KeyId, _cryptoProvider);

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

    private OppExtractResult ExtractOzipCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        bool overwrite,
        string? keyId,
        IProgress<OppExtractProgress>? progress)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"UotanStudio.Oppo.Ozip.{Guid.NewGuid():N}.zip");
        var extractedCount = 0;
        var completed = 0;
        var total = archive.Entries.Count;

        try
        {
            // If the single fallback encrypted entry is present (decryption during
            // parsing failed and no inner ZIP layout is known), decrypt the whole
            // file into a single "decrypted.zip" output.
            if (archive.Entries.Count == 1 && archive.Entries[0].EncryptedSize > 0)
            {
                var fallbackPath = Path.Combine(outputDir, "decrypted.zip");
                using (var fs = new FileStream(fallbackPath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.SequentialScan))
                {
                    OzipDecryptor.DecryptFileWithAutoDetection(stream, fs);
                }

                return new OppExtractResult
                {
                    ExtractedFileCount = 1,
                    OutputDirectory = outputDir,
                    KeyId = keyId ?? archive.Metadata.KeyId
                };
            }

            // Decrypt the OZip file once into a temporary ZIP
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.SequentialScan))
            {
                OzipDecryptor.DecryptFileWithAutoDetection(stream, fs);
            }

            using var zip = ZipFile.OpenRead(tempFile);

            foreach (var entry in archive.Entries)
            {
                progress?.Report(new OppExtractProgress(total, completed, entry.Name));

                var sanitized = SanitizeFileName(entry.Name);
                if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "." || sanitized == "..")
                {
                    sanitized = $"entry_{completed}";
                }

                var outputPath = Path.Combine(outputDir, sanitized);
                ValidateOutputPath(outputDir, outputPath);

                if (File.Exists(outputPath) && !overwrite)
                {
                    completed++;
                    continue;
                }

                var zipEntry = zip.GetEntry(entry.Name);
                if (zipEntry == null)
                {
                    completed++;
                    continue;
                }

                var outputDirForEntry = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirForEntry) && !Directory.Exists(outputDirForEntry))
                {
                    Directory.CreateDirectory(outputDirForEntry);
                }

                using var entryStream = zipEntry.Open();
                using var outputStream = new FileStream(
                    outputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    DefaultBufferSize,
                    FileOptions.SequentialScan);
                entryStream.CopyTo(outputStream);

                extractedCount++;
                completed++;

                progress?.Report(new OppExtractProgress(total, completed, entry.Name));
            }

            return new OppExtractResult
            {
                ExtractedFileCount = extractedCount,
                OutputDirectory = outputDir,
                KeyId = keyId ?? archive.Metadata.KeyId
            };
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

    #endregion

    #region ZIP-wrapped OFP support

    /// <summary>
    /// <para>检查流是否为 ZIP 文件。</para>
    /// Checks if the stream is a ZIP file.
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
    /// <para>从 ZIP 包裹的 OFP 文件中读取元信息。</para>
    /// Reads metadata from a ZIP-wrapped OFP file.
    /// </summary>
    private OppMetadata ReadMetadataFromZipWrappedOfp(Stream stream, long fileSize)
    {
        _logger.Information("Detected ZIP-wrapped OFP file, extracting inner OFP...");

        // Extract the inner OFP to a temporary file
        var tempDir = Path.Combine(Path.GetTempPath(), $"UotanStudio.Oppo.Ofp.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            ExtractZipToDirectory(stream, tempDir);

            // Find the inner OFP file
            var innerFiles = Directory.GetFiles(tempDir, "*.ofp", SearchOption.AllDirectories);
            if (innerFiles.Length == 0)
            {
                // Try any file if no .ofp found
                innerFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
            }

            if (innerFiles.Length == 0)
            {
                throw new InvalidOppFormatException("ZIP-wrapped OFP contains no inner firmware file.");
            }

            // Process the inner OFP file
            using var innerStream = new FileStream(
                innerFiles[0],
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultBufferSize,
                FileOptions.SequentialScan);

            var innerFormat = FormatDetector.DetectFormatFromStream(innerStream);
            var parser = ParserRegistry.GetParser(innerFormat);
            var metadata = parser.ParseMetadata(innerStream, innerStream.Length, _cryptoProvider);

            // Mark as ZIP-wrapped
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
                // Best effort cleanup
            }
        }
    }

    /// <summary>
    /// <para>使用固定密码将 ZIP 流解压到指定目录。</para>
    /// Extracts a ZIP stream to the specified directory using the fixed password.
    /// </summary>
    private static void ExtractZipToDirectory(Stream stream, string outputDir)
    {
        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            var outputPath = Path.GetFullPath(Path.Combine(outputDir, entry.Name));
            if (!outputPath.StartsWith(Path.GetFullPath(outputDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !outputPath.Equals(Path.GetFullPath(outputDir), StringComparison.OrdinalIgnoreCase))
            {
                continue; // Skip entries that attempt path traversal
            }

            var entryDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
            {
                Directory.CreateDirectory(entryDir);
            }

            try
            {
                using var entryStream = entry.Open();
                using var fileStream = new FileStream(
                    outputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    DefaultBufferSize,
                    FileOptions.SequentialScan);
                entryStream.CopyTo(fileStream);
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
    /// <para>从 ZIP 包裹的 OFP 文件中完整解析归档（元数据 + 条目）。</para>
    /// Fully parses an archive (metadata + entries) from a ZIP-wrapped OFP file.
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
                // Best effort cleanup
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
    /// <para>清理文件名中的非法字符。</para>
    /// Sanitizes illegal characters from a file name.
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