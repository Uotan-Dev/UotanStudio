namespace UotanStudio.Firmware.Core;

/// <summary>
/// Read-only format handler: detection, metadata reading, and unpacking.
/// <para>只读格式处理器：检测、元数据读取、解包。</para>
/// </summary>
public interface IReadonlyFormatHandler
{
    /// <summary>
    /// Stable format identifier, also used as the registration key in <see cref="FormatDetector"/>; a
    /// second handler with the same name is not added.
    /// <para>稳定的格式标识，同时作为 <see cref="FormatDetector"/> 中的注册键；同名的第二个处理器不会被加入。</para>
    /// </summary>
    /// <value>
    /// The format name, unique among registered handlers.
    /// <para>格式名称，在已注册处理器之间保持唯一。</para>
    /// </value>
    string FormatName { get; }

    /// <summary>
    /// Lower-case file extensions, without the leading dot, that this handler claims for fast lookup.
    /// <para>本处理器声明的小写、不含前导点的文件扩展名，用于快速查找。</para>
    /// </summary>
    /// <value>
    /// The claimed extensions; an empty list means detection has to rely on content only.
    /// <para>所声明的扩展名；为空时表示识别只能依赖内容。</para>
    /// </value>
    IReadOnlyList<string> FileExtensions { get; }

    /// <summary>
    /// Determines whether this handler can process the given file.
    /// <para>判断该处理器能否处理指定文件。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the candidate file; implementations typically inspect only the leading bytes.
    /// <para>候选文件路径；实现通常只检查起始字节。</para>
    /// </param>
    /// <returns><see langword="true"/> when the file belongs to this format.
    /// <para>文件属于本格式时为 <see langword="true"/>。</para></returns>
    bool CanHandle(string filePath);

    /// <summary>
    /// Reads format metadata without unpacking.
    /// <para>读取格式元数据(不解包)。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the firmware file to inspect.
    /// <para>待检查的固件文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the read; a cancelled read surfaces as an <see cref="OperationCanceledException"/>.
    /// <para>取消本次读取；被取消时以 <see cref="OperationCanceledException"/> 呈现。</para>
    /// </param>
    /// <returns>An outcome whose <see cref="FirmwareOpResult.Entries"/> and
    /// <see cref="FirmwareOpResult.Metadata"/> carry the discovered fields.
    /// <para>结果对象；其 <see cref="FirmwareOpResult.Entries"/> 与 <see cref="FirmwareOpResult.Metadata"/> 承载发现的字段。</para></returns>
    Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unpacks the firmware file to the output directory.
    /// <para>将固件文件解包到输出目录。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the firmware file being unpacked; the input is never modified.
    /// <para>被解包的固件文件路径；输入文件不会被修改。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory that receives the extracted content.
    /// <para>接收解出内容的目标目录。</para>
    /// </param>
    /// <param name="options">
    /// Extraction limits and overwrite policy; <see langword="null"/> uses <see cref="UnpackOptions"/> defaults.
    /// <para>解包限制与覆盖策略；传 <see langword="null"/> 时使用 <see cref="UnpackOptions"/> 的默认值。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the extraction; a cancelled extraction surfaces as an <see cref="OperationCanceledException"/>.
    /// <para>取消解包；被取消时以 <see cref="OperationCanceledException"/> 呈现。</para>
    /// </param>
    /// <returns>An outcome listing what was extracted in <see cref="FirmwareOpResult.Entries"/> and
    /// <see cref="FirmwareOpResult.Components"/>.
    /// <para>结果对象在 <see cref="FirmwareOpResult.Entries"/> 与 <see cref="FirmwareOpResult.Components"/> 中列出解出的内容。</para></returns>
    Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Writable format handler: extends read-only with packing capability.
/// <para>可写格式处理器：在只读基础上扩展打包能力。</para>
/// </summary>
public interface IWritableFormatHandler : IReadonlyFormatHandler
{
    /// <summary>
    /// Packs input (directory or file, format-specific) into an output file.
    /// <para>将输入(目录或文件,依格式而定)打包为输出文件。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Source directory or file whose meaning is defined by the format.
    /// <para>源目录或文件，其含义由具体格式定义。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination file that the packed image is written to.
    /// <para>写入打包结果的目标文件。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the pack; a cancelled pack surfaces as an <see cref="OperationCanceledException"/>.
    /// <para>取消打包；被取消时以 <see cref="OperationCanceledException"/> 呈现。</para>
    /// </param>
    /// <returns>An outcome describing the produced file and its <see cref="FirmwareOpResult.Components"/>.
    /// <para>结果对象，说明生成的文件及其 <see cref="FirmwareOpResult.Components"/>。</para></returns>
    Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Packs input with optional byte-level progress. Defaults to
    /// <see cref="PackAsync(string, string, CancellationToken)"/>; filesystem handlers override it
    /// to report real byte counts to the caller.
    /// <para>带可选字节级进度的打包。默认委托给 <see cref="PackAsync(string, string, CancellationToken)"/>；
    /// 文件系统处理器可重写它以向调用方上报真实字节进度。</para>
    /// </summary>
    /// <param name="inputPath"><para>输入路径（目录或文件，依格式而定）。</para>Input path (directory or file, format-specific).</param>
    /// <param name="outputPath"><para>输出文件路径。</para>Output file path.</param>
    /// <param name="progress"><para>可选的字节级进度回调。</para>Optional byte-level progress callback.</param>
    /// <param name="cancellationToken"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>An outcome describing the produced file, identical to the two-argument overload.
    /// <para>结果对象说明生成的文件，与两参数重载一致。</para></returns>
    Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken = default)
        => PackAsync(inputPath, outputPath, cancellationToken);
}

/// <summary>
/// Backward-compatible alias for <see cref="IWritableFormatHandler"/>.
/// New read-only formats should implement <see cref="IReadonlyFormatHandler"/> instead.
/// <para><see cref="IWritableFormatHandler"/> 的向后兼容别名。新的只读格式应实现 <see cref="IReadonlyFormatHandler"/>。</para>
/// </summary>
public interface IFormatHandler : IWritableFormatHandler { }