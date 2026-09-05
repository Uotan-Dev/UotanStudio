namespace UotanStudio.Firmware.Common;

/// <summary>
/// Unified handler contract for a firmware file format.
/// <para>固件文件格式的统一处理器契约。</para>
/// </summary>
public interface IFormatHandler
{
    /// <summary>
    /// Gets the human-readable format name.
    /// <para>获取人类可读的格式名称。</para>
    /// </summary>
    string FormatName { get; }

    /// <summary>
    /// Gets the typical file extensions (without dot, lowercase).
    /// <para>获取典型文件扩展名(不带点,小写)。</para>
    /// </summary>
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

    /// <summary>
    /// Packs input (directory or file, format-specific) into an output file.
    /// <para>将输入(目录或文件,依格式而定)打包为输出文件。</para>
    /// </summary>
    Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default);
}