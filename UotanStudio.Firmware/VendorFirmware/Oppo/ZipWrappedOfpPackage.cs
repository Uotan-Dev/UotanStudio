using System.IO.Compression;
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

        var tempDir = CreateTemporaryDirectory();

        try
        {
            ExtractZipToDirectory(stream, tempDir);

            using var innerStream = OpenInnerOfpStream(tempDir);

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
            DeleteTemporaryDirectory(tempDir);
        }
    }

    /// <summary>
    /// Extracts a ZIP stream to the specified directory using the fixed password.
    /// <para>使用固定密码将 ZIP 流解压到指定目录。</para>
    /// </summary>
    internal static void ExtractZipToDirectory(Stream stream, string outputDir, CancellationToken cancellationToken = default)
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
    internal OppArchive ParseZipWrappedArchive(Stream stream, OppMetadata metadata)
    {
        _logger.Information("Parsing entries from ZIP-wrapped OFP file...");

        var tempDir = CreateTemporaryDirectory();

        try
        {
            ExtractZipToDirectory(stream, tempDir);

            using var innerStream = OpenInnerOfpStream(tempDir);

            var parser = ParserRegistry.GetParser(metadata.Format);
            var entries = parser.ParseEntries(innerStream, innerStream.Length, metadata, _cryptoProvider);

            return new OppArchive
            {
                Metadata = metadata,
                Entries = entries
            };
        }
        finally
        {
            DeleteTemporaryDirectory(tempDir);
        }
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