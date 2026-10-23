using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;
using FileExtraction = UotanStudio.Firmware.Core.FileExtraction;
using OppFormatDetector = UotanStudio.Firmware.VendorFirmware.Oppo.Parsers.FormatDetector;

namespace UotanStudio.Firmware.VendorFirmware.Oppo;

/// <summary>
/// Unwraps ZIP-sealed OFP vendor firmware packages: detects the ZIP wrapper, extracts the sealed entries with the fixed password, locates the inner firmware file, and reads metadata and archive entries from it.
/// <para>解包 ZIP 封装的 OFP 厂商固件包：检测 ZIP 包裹、使用固定口令解压密封条目、定位内部固件文件，并从中读取元数据与归档条目。</para>
/// </summary>
internal sealed class ZipWrappedOfpPackage
{
    private const int DefaultBufferSize = 81920;

    private readonly IOppCryptoProvider? _cryptoProvider;
    private readonly IOppLogger _logger;

    /// <summary>
    /// Initializes a new instance of the ZipWrappedOfpPackage class.
    /// <para>初始化 ZipWrappedOfpPackage 类的新实例。</para>
    /// </summary>
    /// <param name="cryptoProvider">
    /// Crypto provider used by the inner-firmware parsers; <see langword="null"/> limits parsing to the built-in key databases.
    /// <para>内部固件解析器使用的加密提供程序；为 <see langword="null"/> 时解析仅限内置密钥库。</para>
    /// </param>
    /// <param name="logger">
    /// Logger receiving diagnostic messages.
    /// <para>接收诊断消息的日志器。</para>
    /// </param>
    internal ZipWrappedOfpPackage(IOppCryptoProvider? cryptoProvider, IOppLogger logger)
    {
        _cryptoProvider = cryptoProvider;
        _logger = logger;
    }

    /// <summary>
    /// Checks if the stream is a ZIP file.
    /// <para>检查流是否为 ZIP 文件。</para>
    /// </summary>
    internal static bool IsZipFile(Stream stream)
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
    internal OppMetadata ReadMetadataFromZipWrappedOfp(Stream stream, long fileSize)
    {
        _logger.Information("Detected ZIP-wrapped OFP file, extracting inner OFP...");

        using var unwrapped = Unwrap(stream, CancellationToken.None);

        return ReadInnerArchive(unwrapped.InnerStream, fileSize).Metadata;
    }

    /// <summary>
    /// Extracts a ZIP-wrapped OFP exactly once and keeps the inner file open until the returned scope is disposed.
    /// <para>仅解压一次 ZIP 包裹的 OFP，并保持内部文件打开，直到返回的作用域被释放。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable ZIP stream; its position is moved to zero and it is never closed.
    /// <para>可读、可寻址的 ZIP 流；其位置会被移到零且不会被关闭。</para></param>
    /// <param name="cancellationToken">Checked before and between ZIP entries.
    /// <para>在 ZIP 条目之前与之间检查的取消令牌。</para></param>
    /// <returns>The temporary directory and the opened inner firmware stream.
    /// <para>临时目录与已打开的内部固件流。</para></returns>
    internal UnwrappedPackage Unwrap(Stream stream, CancellationToken cancellationToken)
    {
        var tempDir = CreateTemporaryDirectory();
        try
        {
            ExtractZipToDirectory(stream, tempDir, cancellationToken);
            return new UnwrappedPackage(tempDir, OpenInnerOfpStream(tempDir));
        }
        catch
        {
            DeleteTemporaryDirectory(tempDir);
            throw;
        }
    }

    /// <summary>
    /// Reads inner firmware metadata and entries from an already unwrapped ZIP member; the same parse pass supplies both, so the entry list is not rebuilt.
    /// <para>从已解包 ZIP 成员读取内部固件元数据与条目；同一解析过程同时给出两者，条目列表不会重建。</para>
    /// </summary>
    /// <param name="innerStream">Readable, seekable inner firmware stream owned by the caller; it stays open.
    /// <para>调用方持有的可读、可寻址内部固件流；保持打开。</para></param>
    /// <param name="outerFileSize">Size of the outer ZIP wrapper reported as the archive file size.
    /// <para>作为归档文件大小报告的外层 ZIP 大小。</para></param>
    /// <returns>The archive with metadata flagged as ZIP-wrapped and the parsed entries.
    /// <para>元数据标记为 ZIP 包裹的归档及其已解析条目。</para></returns>
    internal OppArchive ReadInnerArchive(Stream innerStream, long outerFileSize)
    {
        var innerFormat = OppFormatDetector.DetectFormatFromStream(innerStream);
        var parser = ParserRegistry.GetParser(innerFormat);
        var metadata = parser.ParseMetadata(innerStream, innerStream.Length, _cryptoProvider);
        var entries = parser.ParseEntries(innerStream, innerStream.Length, metadata, _cryptoProvider);

        return new OppArchive
        {
            Metadata = new OppMetadata
            {
                FormatName = metadata.FormatName,
                Format = metadata.Format,
                FormatVersion = metadata.FormatVersion,
                FileSize = outerFileSize,
                EntryCount = metadata.EntryCount,
                ProjectName = metadata.ProjectName,
                CpuModel = metadata.CpuModel,
                FlashType = metadata.FlashType,
                FirmwareName = metadata.FirmwareName,
                ProjectId = metadata.ProjectId,
                IsZipWrapped = true,
                KeyId = metadata.KeyId,
                ParsedEntries = entries
            },
            Entries = entries
        };
    }

    /// <summary>
    /// The temporary directory of one unwrapped ZIP-wrapped OFP plus the inner firmware stream read from it.
    /// <para>一次 ZIP 包裹 OFP 解包产生的临时目录，以及从中读取的内部固件流。</para>
    /// </summary>
    internal sealed class UnwrappedPackage : IDisposable
    {
        private bool _disposed;

        internal UnwrappedPackage(string directoryPath, FileStream innerStream)
        {
            DirectoryPath = directoryPath;
            InnerStream = innerStream;
        }

        /// <summary>Directory holding the extracted ZIP members. <para>存放已解压 ZIP 成员的目录。</para></summary>
        /// <value>The absolute temporary directory path. <para>临时目录的绝对路径。</para></value>
        internal string DirectoryPath { get; }

        /// <summary>Inner firmware file opened for sequential reading. <para>以顺序读取方式打开的内部固件文件。</para></summary>
        /// <value>An open read-only stream owned by this scope. <para>由本作用域持有的已打开只读流。</para></value>
        internal FileStream InnerStream { get; }

        /// <summary>Closes the inner stream and deletes the temporary directory once.
        /// <para>关闭内部流并仅删除一次临时目录。</para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            InnerStream.Dispose();
            DeleteTemporaryDirectory(DirectoryPath);
        }
    }

    /// <summary>
    /// Extracts a ZIP stream to the specified directory using the fixed password.
    /// <para>使用固定密码将 ZIP 流解压到指定目录。</para>
    /// </summary>
    internal static void ExtractZipToDirectory(Stream stream, string outputDir, CancellationToken cancellationToken = default)
    {
        stream.Position = 0;

        using var archive = ZipContainer.OpenRead(stream, leaveOpen: true);

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
    /// Fully parses an archive (metadata + entries) from a ZIP-wrapped OFP file with one inflate pass.
    /// <para>以一次解压过程从 ZIP 包裹的 OFP 文件中完整解析归档（元数据 + 条目）。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable ZIP stream owned by the caller; it is never closed.
    /// <para>调用方持有的可读、可寻址 ZIP 流；不会被关闭。</para></param>
    /// <param name="outerFileSize">Size of the outer ZIP wrapper recorded as <see cref="OppMetadata.FileSize"/>.
    /// <para>记录为 <see cref="OppMetadata.FileSize"/> 的外层 ZIP 大小。</para></param>
    /// <param name="cancellationToken">Checked before and between ZIP entries.
    /// <para>在 ZIP 条目之前与之间检查的取消令牌。</para></param>
    /// <returns>The parsed archive whose inner file is removed before returning.
    /// <para>解析出的归档；内部文件在返回前删除。</para></returns>
    internal OppArchive ParseZipWrappedArchive(Stream stream, long outerFileSize, CancellationToken cancellationToken)
    {
        _logger.Information("Parsing entries from ZIP-wrapped OFP file...");

        using var unwrapped = Unwrap(stream, cancellationToken);

        return ReadInnerArchive(unwrapped.InnerStream, outerFileSize);
    }

    /// <summary>
    /// Creates a fresh temporary directory used to unwrap a ZIP-wrapped OFP.
    /// <para>创建用于解包 ZIP 包裹 OFP 的新临时目录。</para>
    /// </summary>
    /// <returns>
    /// The path of the created directory.
    /// <para>已创建目录的路径。</para>
    /// </returns>
    internal static string CreateTemporaryDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"UotanStudio.Oppo.Ofp.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    /// <summary>
    /// Deletes a temporary unwrap directory; cleanup failures are ignored so they never mask the original result.
    /// <para>删除临时解包目录；清理失败将被忽略，以免掩盖原始结果。</para>
    /// </summary>
    /// <param name="directory">
    /// Path of the directory to delete.
    /// <para>要删除的目录路径。</para>
    /// </param>
    internal static void DeleteTemporaryDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Locates the inner firmware file inside an unwrapped ZIP-wrapped OFP directory and opens it for sequential reading; the caller owns and must dispose the returned stream.
    /// <para>在解包后的 ZIP 包裹 OFP 目录中定位内部固件文件并以顺序读取方式打开；返回的流由调用方持有并负责释放。</para>
    /// </summary>
    /// <param name="directory">
    /// Directory containing the extracted ZIP entries.
    /// <para>包含已解压 ZIP 条目的目录。</para>
    /// </param>
    /// <returns>
    /// An open read-only stream over the inner firmware file.
    /// <para>内部固件文件上已打开的只读流。</para>
    /// </returns>
    /// <exception cref="InvalidOppFormatException">
    /// The directory contains no inner firmware file.
    /// <para>目录中不含内部固件文件。</para>
    /// </exception>
    internal static FileStream OpenInnerOfpStream(string directory)
    {
        var innerFiles = Directory.GetFiles(directory, "*.ofp", SearchOption.AllDirectories);
        if (innerFiles.Length == 0)
        {
            innerFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        }

        if (innerFiles.Length == 0)
        {
            throw new InvalidOppFormatException("ZIP-wrapped OFP contains no inner firmware file.");
        }

        return new FileStream(
            innerFiles[0],
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            FileOptions.SequentialScan);
    }
}