using UotanStudio.Firmware.BoundedContexts.FileSystem.Romfs.Models;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Romfs;

/// <summary>
/// Read-only format handler for RomFS images: detection by the <c>-rom1fs-</c> magic, metadata reading
/// and tree extraction. RomFS carries no compression, so this handler never needs codec plumbing.
/// <para>RomFS 镜像的只读格式处理器：按 <c>-rom1fs-</c> 魔数识别、读取元数据并解包目录树。RomFS 不含
/// 压缩，因此本处理器无需任何编解码器接线。</para>
/// </summary>
public sealed class RomfsFormatHandler : IReadonlyFormatHandler
{
    /// <summary>Name of the format this handler serves.
    /// <para>本处理器所服务格式的名称。</para></summary>
    /// <value>The constant <c>"ROMFS"</c>. <para>常量 <c>"ROMFS"</c>。</para></value>
    public string FormatName => "ROMFS";

    /// <summary>File extensions this handler registers.
    /// <para>本处理器注册的文件扩展名。</para></summary>
    /// <value>The list <c>img</c>, <c>rom</c> and <c>romfs</c>; extension alone never overrides magic detection.
    /// <para>列表 <c>img</c>、<c>rom</c> 与 <c>romfs</c>；仅凭扩展名不会取代魔数检测。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["img", "rom", "romfs"];

    /// <summary>
    /// Determines whether the file starts with the eight-byte <c>-rom1fs-</c> magic.
    /// <para>判断文件是否以八字节 <c>-rom1fs-</c> 魔数开头。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path; <see langword="null"/>, blank or nonexistent paths
    /// short-circuit to <see langword="false"/>.
    /// <para>候选文件路径；为 <see langword="null"/>、空白或不存在的路径直接返回 <see langword="false"/>。</para></param>
    /// <returns><see langword="true"/> when the magic is present; <see langword="false"/> on any I/O failure,
    /// which is swallowed. <para>存在魔数时返回 <see langword="true"/>；任何 I/O 失败都返回 <see langword="false"/>
    /// 且被吞掉。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[8];
            if (fs.Read(magic) != 8)
            {
                return false;
            }

            return magic.SequenceEqual("-rom1fs-"u8);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the volume metadata and lists every entry path without extracting file data.
    /// <para>读取卷元数据并列出全部条目路径，不解出文件数据。</para>
    /// </summary>
    /// <param name="filePath">RomFS image file, opened read-only on a worker thread.
    /// <para>RomFS 镜像文件；在工作线程上以只读方式打开。</para></param>
    /// <param name="cancellationToken">Token checked around the tree walk.
    /// <para>在目录树遍历前后检查的令牌。</para></param>
    /// <returns>An <c>Ok</c> result whose metadata carries volume name, size and entry counts and whose
    /// entries list the extractable file paths; a <c>Fail</c> result with the error message when the image
    /// cannot be parsed. <para><c>Ok</c> 结果的元数据包含卷名、大小与条目计数，明细行列出可解出文件路径；
    /// 镜像无法解析时返回带错误消息的 <c>Fail</c> 结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var input = File.OpenRead(filePath);
                var parser = new RomfsParser(input, cancellationToken);
                var files = parser.EnumerateFiles();
                var metadata = new Dictionary<string, string>
                {
                    ["Format"] = "ROMFS",
                    ["VolumeName"] = parser.Name,
                    ["VolumeSize"] = parser.VolumeSize.ToString(),
                    ["EntryCount"] = files.Count.ToString(),
                    ["TotalDataBytes"] = parser.TotalDataBytes.ToString(),
                };
                return FirmwareOpResult.Ok(
                    $"ROMFS volume '{parser.Name}' with {files.Count} entries.",
                    [.. files.Select(entry => entry.Path)],
                    metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read ROMFS metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);

    /// <summary>
    /// Extracts the RomFS tree into the output directory.
    /// <para>将 RomFS 目录树解包到输出目录。</para>
    /// </summary>
    /// <param name="filePath">Source image; the input is never modified.
    /// <para>源镜像；输入文件不会被修改。</para></param>
    /// <param name="outputDirectory">Destination tree; entries are written with their image paths.
    /// <para>目标目录树；条目按镜像内路径写出。</para></param>
    /// <param name="options">Unpack options; <see langword="null"/> overwrites existing files, reports no
    /// byte progress and applies the default output budget.
    /// <para>解包选项；为 <see langword="null"/> 时覆盖已存在文件、不报告字节进度并使用默认输出预算。</para></param>
    /// <param name="cancellationToken">Token checked throughout parsing and extraction.
    /// <para>在解析与解包全程检查的令牌。</para></param>
    /// <returns>An <c>Ok</c> result listing the extracted file paths, or a <c>Fail</c> result when the image
    /// or extraction fails. <para>返回列出已解出文件路径的 <c>Ok</c> 结果；镜像或解包失败时返回 <c>Fail</c> 结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var input = File.OpenRead(filePath);
                var parser = new RomfsParser(input, cancellationToken);
                var byteProgress = FileSystemByteProgress.ForStage(options?.FileSystemProgress, FileSystemWorkStage.Extracting);
                var files = parser.ExtractToDirectory(
                    outputDirectory,
                    overwrite: options?.Overwrite ?? true,
                    maximumOutputBytes: options?.MaximumOutputBytes ?? new UnpackOptions().MaximumOutputBytes,
                    byteProgress,
                    cancellationToken);
                return FirmwareOpResult.Ok(
                    $"Extracted {files.Count} files from ROMFS volume '{parser.Name}' to '{outputDirectory}'.",
                    [.. files]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack ROMFS image '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
}