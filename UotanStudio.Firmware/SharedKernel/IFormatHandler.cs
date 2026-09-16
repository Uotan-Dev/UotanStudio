namespace UotanStudio.Firmware.SharedKernel;

/// <summary>
/// Read-only format handler: detection, metadata reading, and unpacking.
/// <para>只读格式处理器：检测、元数据读取、解包。</para>
/// </summary>
public interface IReadonlyFormatHandler
{
    string FormatName { get; }

    IReadOnlyList<string> FileExtensions { get; }

    /// <summary>
    /// Determines whether this handler can process the given file.
    /// <para>判断该处理器能否处理指定文件。</para>
    /// </summary>
    bool CanHandle(string filePath);

    /// <summary>
    /// Reads format metadata without unpacking.
    /// <para>读取格式元数据(不解包)。</para>
    /// </summary>
    Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unpacks the firmware file to the output directory.
    /// <para>将固件文件解包到输出目录。</para>
    /// </summary>
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
    Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken = default)
        => PackAsync(inputPath, outputPath, cancellationToken);
}

/// <summary>
/// Backward-compatible alias for <see cref="IWritableFormatHandler"/>.
/// New read-only formats should implement <see cref="IReadonlyFormatHandler"/> instead.
/// <para><see cref="IWritableFormatHandler"/> 的向后兼容别名。新的只读格式应实现 <see cref="IReadonlyFormatHandler"/>。</para>
/// </summary>
public interface IFormatHandler : IWritableFormatHandler { }