using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// EROFS 镜像格式处理器（解包 / 打包 / 元数据读取）。
/// <para>EROFS image format handler (unpack / pack / metadata read).</para>
/// </summary>
public sealed class ErofsFormatHandler : IFormatHandler
{
    /// <summary>
    /// 获取人类可读的格式名称。
    /// <para>Gets the human-readable format name.</para>
    /// </summary>
    public string FormatName => "EROFS";

    /// <summary>
    /// 获取典型文件扩展名（不带点，小写）。
    /// <para>Gets the typical file extensions (without dot, lowercase).</para>
    /// </summary>
    public IReadOnlyList<string> FileExtensions { get; } = ["img", "erofs"];

    /// <summary>
    /// 判断该处理器能否处理指定文件：读取偏移 1024 处的 4 字节魔数（小端 0xE0F5E1E2），
    /// 或扩展名为 .erofs。
    /// <para>Determines whether this handler can process the given file by reading the 4-byte magic
    /// at offset 1024 (little-endian 0xE0F5E1E2), or by a .erofs extension.</para>
    /// </summary>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        if (Path.GetExtension(filePath).Equals(".erofs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var fs = File.OpenRead(filePath);
            if (fs.Length < ErofsSuperblock.SuperblockOffset + 4)
            {
                return false;
            }

            Span<byte> magic = stackalloc byte[4];
            fs.Position = ErofsSuperblock.SuperblockOffset;
            fs.ReadExactly(magic);
            return ErofsSuperblock.IsMagicBytes(magic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取格式元数据而不解包。
    /// <para>Reads format metadata without unpacking.</para>
    /// </summary>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var parser = new ErofsParser(filePath);
                var files = parser.EnumerateFiles();
                var metadata = new Dictionary<string, string>
                {
                    ["Format"] = "EROFS",
                    ["BlockSize"] = parser.BlockSize.ToString(),
                    ["Blocks"] = parser.BlockCount.ToString(),
                    ["ImageSize"] = parser.ImageSize.ToString(),
                    ["InodeCount"] = parser.InodeCount.ToString(),
                    ["FileCount"] = files.Count.ToString(),
                    ["RootNid"] = parser.RootNid.ToString(),
                    ["VolumeName"] = parser.VolumeName,
                };
                var components = files.Select(file => FirmwareComponent.Of(file.Path, file.Size)).ToList();
                return FirmwareOpResult.Ok(
                    $"{files.Count} files in EROFS image '{filePath}'.",
                    [.. files.Select(file => file.Path)],
                    metadata,
                    components);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read EROFS metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 将 erofs 镜像解包到输出目录。
    /// <para>Unpacks the erofs image to the output directory.</para>
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var parser = new ErofsParser(filePath);
                parser.ExtractToDirectory(outputDirectory, options?.Overwrite ?? true);
                var files = parser.EnumerateFiles();
                return FirmwareOpResult.Ok(
                    $"Extracted {files.Count} files from '{filePath}' to '{outputDirectory}'.",
                    [.. files.Select(file => file.Path)]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack EROFS image '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 将目录打包为 erofs 镜像。
    /// <para>Packs a directory into an erofs image.</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(inputPath))
                {
                    return FirmwareOpResult.Fail($"Input path is not a directory: '{inputPath}'.");
                }

                var builder = new ErofsBuilder();
                builder.Build(inputPath, outputPath, new ErofsOptions());

                return FirmwareOpResult.Ok($"Packed '{inputPath}' into '{outputPath}' (EROFS).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to pack '{inputPath}': {ex.Message}");
            }
        }, cancellationToken);
    }
}