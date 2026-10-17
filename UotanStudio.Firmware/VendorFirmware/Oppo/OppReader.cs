using System.Buffers;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;
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

    private readonly IOppCryptoProvider? _cryptoProvider;
    private readonly IOppLogger _logger;
    private readonly ZipWrappedOfpPackage _zipWrappedOfpPackage;

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
        _zipWrappedOfpPackage = new ZipWrappedOfpPackage(_cryptoProvider, _logger);
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
    /// The package is unreadable, unrecognized or cannot be decrypted; concrete failures surface as subclasses such as <see cref="InvalidOppFormatException"/>, <see cref="UnsupportedOppVersionException"/> or <see cref="OppDecryptionException"/>.
    /// <para>包不可读、无法识别或无法解密；具体失败以 <see cref="InvalidOppFormatException"/>、<see cref="UnsupportedOppVersionException"/> 或 <see cref="OppDecryptionException"/> 等子类抛出。</para>
    /// </exception>
    public OppMetadata ReadMetadata(string filePath)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        using var stream = OpenPackageStream(filePath, async: false);

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

        using var stream = OpenPackageStream(filePath, async: true);

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


        if (format == OppFormat.OfpQc && ZipWrappedOfpPackage.IsZipFile(stream))
        {
            return _zipWrappedOfpPackage.ReadMetadataFromZipWrappedOfp(stream, fileSize);
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

        using var stream = OpenPackageStream(filePath, async: false);

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

        using var stream = OpenPackageStream(filePath, async: true);

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

        using var session = OpenArchiveSession(stream, CancellationToken.None);

        return session.Archive;
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

        using var stream = OpenPackageStream(filePath, async: false);

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

        using var stream = OpenPackageStream(filePath, async: true);

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

        using var session = OpenArchiveSession(stream, CancellationToken.None);

        return ExtractCore(session.EntryStream, session.Archive, outputDir, options, progress);
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

        using var session = OpenArchiveSession(stream, cancellationToken);
        var result = ExtractCore(session.EntryStream, session.Archive, outputDir, options, progress, cancellationToken);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Parses a package and extracts it in one operation, so a ZIP-wrapped OFP is unwrapped only once.
    /// <para>在一次操作中解析并解包固件包，使 ZIP 包裹的 OFP 只解压一次。</para>
    /// </summary>
    /// <param name="filePath">Path to the package file; opened once for reading and closed before completion.
    /// <para>包文件路径；仅打开一次用于读取，并在完成前关闭。</para></param>
    /// <param name="outputDir">Destination directory; created when missing.
    /// <para>目标目录；不存在时创建。</para></param>
    /// <param name="options">Extraction options; <see langword="null"/> uses defaults.
    /// <para>提取选项；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="progress">Receives progress snapshots; <see langword="null"/> reports nothing.
    /// <para>接收进度快照；为 <see langword="null"/> 时不上报。</para></param>
    /// <param name="cancellationToken">Checked before work and around every entry write.
    /// <para>在工作前及每个条目写入前后检查的取消令牌。</para></param>
    /// <returns>The parsed archive together with the extraction summary.
    /// <para>已解析归档及其提取摘要。</para></returns>
    internal Task<(OppArchive Archive, OppExtractResult Result)> ParseAndExtractAsync(
        string filePath,
        string outputDir,
        OppExtractOptions? options = null,
        IProgress<OppExtractProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);
        Guard.ThrowIfNullOrWhiteSpace(outputDir);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = OpenPackageStream(filePath, async: false);
            using var session = OpenArchiveSession(stream, cancellationToken);
            var result = ExtractCore(session.EntryStream, session.Archive, outputDir, options, progress, cancellationToken);
            return (session.Archive, result);
        }, cancellationToken);
    }

    private ArchiveSession OpenArchiveSession(Stream stream, CancellationToken cancellationToken)
    {
        var format = OppFormatDetector.DetectFormatFromStream(stream);
        var fileSize = stream.Length;

        if (format == OppFormat.OfpQc && ZipWrappedOfpPackage.IsZipFile(stream))
        {
            var unwrapped = _zipWrappedOfpPackage.Unwrap(stream, cancellationToken);
            try
            {
                var archive = _zipWrappedOfpPackage.ReadInnerArchive(unwrapped.InnerStream, fileSize);
                return new ArchiveSession(archive, unwrapped.InnerStream, unwrapped);
            }
            catch
            {
                unwrapped.Dispose();
                throw;
            }
        }

        var parser = ParserRegistry.GetParser(format);
        var metadata = parser.ParseMetadata(stream, fileSize, _cryptoProvider);
        var entries = parser.ParseEntries(stream, fileSize, metadata, _cryptoProvider);
        return new ArchiveSession(
            new OppArchive { Metadata = metadata, Entries = entries },
            stream,
            lease: null);
    }

    private sealed class ArchiveSession(OppArchive archive, Stream entryStream, IDisposable? lease) : IDisposable
    {
        internal OppArchive Archive { get; } = archive;

        internal Stream EntryStream { get; } = entryStream;

        public void Dispose() => lease?.Dispose();
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


        return ExtractEntriesCore(stream, archive, outputDir, overwrite, keyId, selected, progress, cancellationToken);
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
            OppOutputPathGuard.Validate(outputDir, outputPath);

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

    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

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

    /// <summary>
    /// Opens the package file for reading with the parameter block shared by all path-based entries.
    /// <para>以所有按路径入口共享的参数块打开固件包文件以进行读取。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the package file.
    /// <para>固件包文件路径。</para>
    /// </param>
    /// <param name="async">
    /// Indicates whether the handle is opened for asynchronous I/O.
    /// <para>指示是否以异步 I/O 方式打开句柄。</para>
    /// </param>
    /// <returns>
    /// The opened read-only package stream.
    /// <para>已打开的只读包流。</para>
    /// </returns>
    private static FileStream OpenPackageStream(string filePath, bool async) =>
        new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            (async ? FileOptions.Asynchronous : FileOptions.None) | FileOptions.SequentialScan);
}