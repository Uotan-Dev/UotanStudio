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
}

/// <summary>
/// Backward-compatible alias for <see cref="IWritableFormatHandler"/>.
/// New read-only formats should implement <see cref="IReadonlyFormatHandler"/> instead.
/// <para><see cref="IWritableFormatHandler"/> 的向后兼容别名。新的只读格式应实现 <see cref="IReadonlyFormatHandler"/>。</para>
/// </summary>
public interface IFormatHandler : IWritableFormatHandler { }