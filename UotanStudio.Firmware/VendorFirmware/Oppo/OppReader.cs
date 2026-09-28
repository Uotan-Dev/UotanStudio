using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.Oppo;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;
using FileExtraction = UotanStudio.Firmware.Core.FileExtraction;
using OppFormatDetector = UotanStudio.Firmware.VendorFirmware.Oppo.Parsers.FormatDetector;

namespace UotanStudio.Firmware.VendorFirmware.Oppo;

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
    /// <param name="cryptoProvider">
    /// Crypto provider used to decrypt encrypted packages; <see langword="null"/> means packages that require decryption beyond the built-in key databases cannot be opened.
    /// <para>用于解密加密包的加密提供程序；为 <see langword="null"/> 时，超出内置密钥库能力、需要解密的包无法打开。</para>
    /// </param>
    /// <param name="logger">
    /// Logger receiving diagnostic messages; <see langword="null"/> discards all log output.
    /// <para>接收诊断消息的日志器；为 <see langword="null"/> 时丢弃所有日志输出。</para>
    /// </param>
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
    /// <param name="filePath">
    /// Path to the Oppo package file; the file is opened for reading, shared for readers, and closed before returning.
    /// <para>Oppo 包文件路径；文件以只读、允许并发读取的方式打开，并在返回前关闭。</para>
    /// </param>
    /// <returns>
    /// The parsed archive-level metadata.
    /// <para>解析出的归档级元数据。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is empty or consists only of white-space characters.
    /// <para><paramref name="filePath"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The file does not exist.
    /// <para>文件不存在。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted; concrete failures surface as subclasses such as <see cref="InvalidOppFormatException"/>, <see cref="UnsupportedOppVersionException"/> or <see cref="UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions.OppDecryptionException"/>.
    /// <para>包不可读、无法识别或无法解密；具体失败以 <see cref="InvalidOppFormatException"/>、<see cref="UnsupportedOppVersionException"/> 或 <see cref="UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions.OppDecryptionException"/> 等子类抛出。</para>
    /// </exception>
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
    /// <param name="filePath">
    /// Path to the Oppo package file; the file is opened for asynchronous reading, shared for readers, and closed before the returned task completes.
    /// <para>Oppo 包文件路径；文件以异步只读、允许并发读取的方式打开，并在返回的任务完成前关闭。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed when the metadata read starts; the parsing that follows runs synchronously once the stream is open.
    /// <para>在元信息读取开始时观察的令牌；后续的解析在流打开后同步执行。</para>
    /// </param>
    /// <returns>
    /// A task whose result is the parsed archive-level metadata.
    /// <para>结果为解析出的归档级元数据的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is empty or consists only of white-space characters.
    /// <para><paramref name="filePath"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The file does not exist.
    /// <para>文件不存在。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled at the start of the read.
    /// <para>令牌在读取开始时已被取消。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted.
    /// <para>包不可读、无法识别或无法解密。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Readable, seekable stream holding the whole package; it stays open and ownership remains with the caller. A ZIP-wrapped OFP is decrypted to a temporary directory that is removed before returning.
    /// <para>包含完整固件包、可读且可寻址的流；流保持打开，所有权归调用方。ZIP 包裹的 OFP 会先解密到临时目录，并在返回前删除该目录。</para>
    /// </param>
    /// <returns>
    /// The parsed archive-level metadata; for a ZIP-wrapped OFP the reported file size is the outer wrapper size.
    /// <para>解析出的归档级元数据；对 ZIP 包裹的 OFP，报告的文件大小为外层包裹文件的大小。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted.
    /// <para>包不可读、无法识别或无法解密。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Readable, seekable stream holding the whole package; it stays open and ownership remains with the caller.
    /// <para>包含完整固件包、可读且可寻址的流；流保持打开，所有权归调用方。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token checked once before parsing starts; the metadata parse itself runs synchronously.
    /// <para>在解析开始前检查一次的令牌；元数据解析本身同步执行。</para>
    /// </param>
    /// <returns>
    /// A completed task carrying the parsed archive-level metadata.
    /// <para>携带解析出的归档级元数据的已完成任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was already cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted.
    /// <para>包不可读、无法识别或无法解密。</para>
    /// </exception>
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
    /// <param name="filePath">
    /// Path to the Oppo package file; the file is opened for reading, shared for readers, and closed before returning.
    /// <para>Oppo 包文件路径；文件以只读、允许并发读取的方式打开，并在返回前关闭。</para>
    /// </param>
    /// <returns>
    /// The archive with its metadata and all parsed entries.
    /// <para>包含元数据与全部已解析条目的归档。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is empty or consists only of white-space characters.
    /// <para><paramref name="filePath"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The file does not exist.
    /// <para>文件不存在。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted.
    /// <para>包不可读、无法识别或无法解密。</para>
    /// </exception>
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
    /// <param name="filePath">
    /// Path to the Oppo package file; the file is opened for asynchronous reading, shared for readers, and closed before the returned task completes.
    /// <para>Oppo 包文件路径；文件以异步只读、允许并发读取的方式打开，并在返回的任务完成前关闭。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed when parsing starts; the parse that follows runs synchronously once the stream is open.
    /// <para>在解析开始时观察的令牌；后续的解析在流打开后同步执行。</para>
    /// </param>
    /// <returns>
    /// A task whose result is the archive with its metadata and all parsed entries.
    /// <para>结果为包含元数据与全部已解析条目的归档的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is empty or consists only of white-space characters.
    /// <para><paramref name="filePath"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The file does not exist.
    /// <para>文件不存在。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled at the start of the parse.
    /// <para>令牌在解析开始时已被取消。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted.
    /// <para>包不可读、无法识别或无法解密。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Readable, seekable stream holding the whole package; it stays open and ownership remains with the caller. A ZIP-wrapped OFP is decrypted to a temporary directory that is removed before returning.
    /// <para>包含完整固件包、可读且可寻址的流；流保持打开，所有权归调用方。ZIP 包裹的 OFP 会先解密到临时目录，并在返回前删除该目录。</para>
    /// </param>
    /// <returns>
    /// The archive with its metadata and all parsed entries.
    /// <para>包含元数据与全部已解析条目的归档。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted.
    /// <para>包不可读、无法识别或无法解密。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Readable, seekable stream holding the whole package; it stays open and ownership remains with the caller.
    /// <para>包含完整固件包、可读且可寻址的流；流保持打开，所有权归调用方。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token checked once before parsing starts; the parse itself runs synchronously.
    /// <para>在解析开始前检查一次的令牌；解析本身同步执行。</para>
    /// </param>
    /// <returns>
    /// A completed task carrying the archive with its metadata and all parsed entries.
    /// <para>携带包含元数据与全部已解析条目的归档的已完成任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was already cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted.
    /// <para>包不可读、无法识别或无法解密。</para>
    /// </exception>
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
    /// <param name="filePath">
    /// Path to the Oppo package file; the file is opened for reading, shared for readers, and closed before returning.
    /// <para>Oppo 包文件路径；文件以只读、允许并发读取的方式打开，并在返回前关闭。</para>
    /// </param>
    /// <param name="outputDir">
    /// Destination directory; it is created when missing. Each entry is written to a temporary file and moved into place only after its declared length is reproduced exactly.
    /// <para>目标目录；不存在时会被创建。每个条目先写入临时文件，只有实际长度与声明完全一致后才移动到最终位置。</para>
    /// </param>
    /// <param name="options">
    /// Extraction options (overwrite, key selection, entry selection); <see langword="null"/> uses defaults, i.e. no overwrite, metadata/auto-detected keys, and all entries.
    /// <para>提取选项（覆盖、密钥选择、条目选择）；为 <see langword="null"/> 时使用默认值：不覆盖、按元数据或自动检测取密钥、提取全部条目。</para>
    /// </param>
    /// <param name="progress">
    /// Receives an <see cref="OppExtractProgress"/> snapshot before and after each considered entry; <see langword="null"/> reports nothing.
    /// <para>在每个被考虑的条目前后收到 <see cref="OppExtractProgress"/> 快照；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <returns>
    /// A summary with the number of files written, the output directory, and the key identifier actually used.
    /// <para>包含写入文件数、输出目录以及实际使用的密钥标识的摘要。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filePath"/> or <paramref name="outputDir"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 或 <paramref name="outputDir"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> or <paramref name="outputDir"/> is empty or consists only of white-space characters.
    /// <para><paramref name="filePath"/> 或 <paramref name="outputDir"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The package file does not exist.
    /// <para>固件包文件不存在。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted, <paramref name="options"/> selects an entry name that the archive does not contain, or an extracted entry does not match its declared length.
    /// <para>包不可读、无法识别或无法解密；<paramref name="options"/> 选择了归档中不存在的条目名；或提取出的条目与声明长度不符。</para>
    /// </exception>
    /// <example>
    /// <code>
    /// var reader = new OppReader();
    /// var result = reader.Extract("Package.ofp", "out", new OppExtractOptions { Overwrite = true });
    /// </code>
    /// </example>
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
    /// <param name="filePath">
    /// Path to the Oppo package file; the file is opened for asynchronous reading, shared for readers, and closed before the returned task completes.
    /// <para>Oppo 包文件路径；文件以异步只读、允许并发读取的方式打开，并在返回的任务完成前关闭。</para>
    /// </param>
    /// <param name="outputDir">
    /// Destination directory; it is created when missing and each entry is published atomically after its declared length is reproduced exactly.
    /// <para>目标目录；不存在时会被创建，每个条目在长度校验完全一致后原子发布。</para>
    /// </param>
    /// <param name="options">
    /// Extraction options; <see langword="null"/> uses defaults (no overwrite, metadata/auto-detected keys, all entries).
    /// <para>提取选项；为 <see langword="null"/> 时使用默认值（不覆盖、按元数据或自动检测取密钥、提取全部条目）。</para>
    /// </param>
    /// <param name="progress">
    /// Receives an <see cref="OppExtractProgress"/> snapshot before and after each considered entry; <see langword="null"/> reports nothing.
    /// <para>在每个被考虑的条目前后收到 <see cref="OppExtractProgress"/> 快照；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token checked before parsing and again around every entry write; cancelling stops the run with the files already published left in place.
    /// <para>在解析前以及每个条目写入前后都会检查的令牌；取消会中止运行，但已发布完成的文件保留在原处。</para>
    /// </param>
    /// <returns>
    /// A task whose result summarizes the files written, the output directory, and the key identifier used.
    /// <para>结果为写入文件数、输出目录与实际使用密钥标识摘要的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filePath"/> or <paramref name="outputDir"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 或 <paramref name="outputDir"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> or <paramref name="outputDir"/> is empty or consists only of white-space characters.
    /// <para><paramref name="filePath"/> 或 <paramref name="outputDir"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The package file does not exist.
    /// <para>固件包文件不存在。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌被取消。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted, <paramref name="options"/> selects an unknown entry name, or an extracted entry does not match its declared length.
    /// <para>包不可读、无法识别或无法解密；<paramref name="options"/> 选择了不存在的条目名；或提取出的条目与声明长度不符。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Readable, seekable stream holding the whole package; it stays open, the reader manages its position, and ownership remains with the caller.
    /// <para>包含完整固件包、可读且可寻址的流；流保持打开，读取位置由本方法自行管理，所有权归调用方。</para>
    /// </param>
    /// <param name="outputDir">
    /// Destination directory; it is created when missing and each entry is published atomically after its declared length is reproduced exactly.
    /// <para>目标目录；不存在时会被创建，每个条目在长度校验完全一致后原子发布。</para>
    /// </param>
    /// <param name="options">
    /// Extraction options; <see langword="null"/> uses defaults (no overwrite, metadata/auto-detected keys, all entries).
    /// <para>提取选项；为 <see langword="null"/> 时使用默认值（不覆盖、按元数据或自动检测取密钥、提取全部条目）。</para>
    /// </param>
    /// <param name="progress">
    /// Receives an <see cref="OppExtractProgress"/> snapshot before and after each considered entry; <see langword="null"/> reports nothing.
    /// <para>在每个被考虑的条目前后收到 <see cref="OppExtractProgress"/> 快照；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <returns>
    /// A summary with the number of files written, the output directory, and the key identifier used.
    /// <para>包含写入文件数、输出目录与实际使用密钥标识的摘要。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="outputDir"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 或 <paramref name="outputDir"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputDir"/> is empty or consists only of white-space characters.
    /// <para><paramref name="outputDir"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted, <paramref name="options"/> selects an unknown entry name, or an extracted entry does not match its declared length.
    /// <para>包不可读、无法识别或无法解密；<paramref name="options"/> 选择了不存在的条目名；或提取出的条目与声明长度不符。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Readable, seekable stream holding the whole package; it stays open, the reader manages its position, and ownership remains with the caller.
    /// <para>包含完整固件包、可读且可寻址的流；流保持打开，读取位置由本方法自行管理，所有权归调用方。</para>
    /// </param>
    /// <param name="outputDir">
    /// Destination directory; it is created when missing and each entry is published atomically after its declared length is reproduced exactly.
    /// <para>目标目录；不存在时会被创建，每个条目在长度校验完全一致后原子发布。</para>
    /// </param>
    /// <param name="options">
    /// Extraction options; <see langword="null"/> uses defaults (no overwrite, metadata/auto-detected keys, all entries).
    /// <para>提取选项；为 <see langword="null"/> 时使用默认值（不覆盖、按元数据或自动检测取密钥、提取全部条目）。</para>
    /// </param>
    /// <param name="progress">
    /// Receives an <see cref="OppExtractProgress"/> snapshot before and after each considered entry; <see langword="null"/> reports nothing.
    /// <para>在每个被考虑的条目前后收到 <see cref="OppExtractProgress"/> 快照；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token checked before parsing and again around every entry write; cancelling stops the run with the files already published left in place.
    /// <para>在解析前以及每个条目写入前后都会检查的令牌；取消会中止运行，但已发布完成的文件保留在原处。</para>
    /// </param>
    /// <returns>
    /// A completed task carrying the summary of files written, the output directory, and the key identifier used.
    /// <para>携带写入文件数、输出目录与实际使用密钥标识摘要的已完成任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="outputDir"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 或 <paramref name="outputDir"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputDir"/> is empty or consists only of white-space characters.
    /// <para><paramref name="outputDir"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌被取消。</para>
    /// </exception>
    /// <exception cref="OppException">
    /// The package is unreadable, unrecognized or cannot be decrypted, <paramref name="options"/> selects an unknown entry name, or an extracted entry does not match its declared length.
    /// <para>包不可读、无法识别或无法解密；<paramref name="options"/> 选择了不存在的条目名；或提取出的条目与声明长度不符。</para>
    /// </exception>
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
        var selected = options?.SelectedEntries is { } names
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : null;
        if (selected is not null && selected.Except(archive.Entries.Select(entry => entry.Name),
                StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new InvalidOppFormatException("Selected Oppo entry does not exist.");
        }

        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }


        if (archive.Metadata.Format == OppFormat.Ozip)
        {
            return ExtractOzipCore(stream, archive, outputDir, overwrite, keyId, selected, progress, cancellationToken);
        }


        return archive.Metadata.IsZipWrapped
            ? ExtractZipWrappedCore(stream, archive, outputDir, overwrite, keyId, selected, progress, cancellationToken)
            : ExtractEntriesCore(stream, archive, outputDir, overwrite, keyId, selected, progress, cancellationToken);
    }

    private OppExtractResult ExtractZipWrappedCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        bool overwrite,
        string? keyId,
        HashSet<string>? selected,
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

            return ExtractEntriesCore(innerStream, archive, outputDir, overwrite, keyId, selected, progress, cancellationToken);
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
        HashSet<string>? selected,
        IProgress<OppExtractProgress>? progress, CancellationToken cancellationToken)
    {
        var entries = archive.Entries;
        var totalEntries = entries.Count;
        var completedEntries = 0;
        var extractedCount = 0;

        foreach (var entry in entries)
        {
            if (selected is not null && !selected.Contains(entry.Name)) continue;
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