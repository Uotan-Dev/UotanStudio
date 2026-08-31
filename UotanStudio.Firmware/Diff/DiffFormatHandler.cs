using System.Buffers.Binary;
using System.Globalization;
using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Diff;

/// <summary>
/// Format handler for bsdiff binary patches (magic 'BSDIFF40'), as produced by <see cref="DeltaEngine"/>.
/// Exposes patch metadata only (header fields plus file length); applying or generating a patch is done
/// through <see cref="DeltaEngine"/>.
/// <para>bsdiff 二进制补丁（魔数 'BSDIFF40'，由 <see cref="DeltaEngine"/> 产出）的格式处理器。
/// 仅展示补丁元数据（头部字段与文件长度）；应用/生成补丁请使用 <see cref="DeltaEngine"/>。</para>
/// </summary>
public sealed class DiffFormatHandler : IFormatHandler
{
    private const string BsdiffMagic = "BSDIFF40";
    private const int BsdiffHeaderSize = 32;

    /// <summary>
    /// Gets the human-readable format name.
    /// <para>获取人类可读的格式名称。</para>
    /// </summary>
    public string FormatName => "bsdiff Binary Patch";

    /// <summary>
    /// Gets the typical file extensions (without dot, lowercase): <c>bsdiff</c>. Not claimed by any other
    /// registered handler.
    /// <para>获取典型文件扩展名（不带点，小写）：<c>bsdiff</c>。未被其他已注册处理器占用。</para>
    /// </summary>
    public IReadOnlyList<string> FileExtensions { get; } = ["bsdiff"];

    /// <summary>
    /// Determines whether this handler can process the given file: the leading bytes must be the ASCII
    /// magic 'BSDIFF40'.
    /// <para>判断该处理器能否处理指定文件：文件开头字节须为 ASCII 魔数 'BSDIFF40'。</para>
    /// </summary>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var header = FormatDetector.ReadHeader(filePath, 8);
            return FormatDetector.HasMagic(header, BsdiffMagic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads bsdiff header metadata without unpacking: patch type, file length, and the 32-byte header's
    /// control-block length, diff-block length and reconstructed new-file size.
    /// <para>不解包读取 bsdiff 头部元数据：补丁类型、文件长度，以及 32 字节头部中的控制块长度、差异块长度、
    /// 还原出的新文件大小。</para>
    /// </summary>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var header = FormatDetector.ReadHeader(filePath, BsdiffHeaderSize);
                if (header.Length < BsdiffHeaderSize || !FormatDetector.HasMagic(header, BsdiffMagic))
                {
                    return FirmwareOpResult.Fail($"Not a bsdiff (BSDIFF40) patch: '{filePath}'.  不是 bsdiff (BSDIFF40) 补丁。");
                }

                var controlLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8, 8));
                var diffLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16, 8));
                var newSize = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24, 8));
                if (controlLength < 0 || diffLength < 0 || newSize < 0)
                {
                    return FirmwareOpResult.Fail("Invalid negative length in bsdiff header.  bsdiff 头部存在非法负长度。");
                }

                var fileLength = new FileInfo(filePath).Length;
                var metadata = new Dictionary<string, string>
                {
                    ["PatchType"] = "bsdiff (BSDIFF40)",
                    ["ControlBlockLength"] = controlLength.ToString(CultureInfo.InvariantCulture),
                    ["DiffBlockLength"] = diffLength.ToString(CultureInfo.InvariantCulture),
                    ["NewFileSize"] = newSize.ToString(CultureInfo.InvariantCulture),
                    ["FileLength"] = fileLength.ToString(CultureInfo.InvariantCulture),
                };

                var summary =
                    $"bsdiff binary patch: control {controlLength} bytes, diff {diffLength} bytes, " +
                    $"reconstructs a {newSize}-byte target file.";

                return FirmwareOpResult.Ok(summary, metadata: metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read bsdiff metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Not supported: applying a patch needs a source file; use <see cref="DeltaEngine.ApplyDeltaAsync"/>.
    /// <para>不支持：应用补丁需要源文件；请使用 <see cref="DeltaEngine.ApplyDeltaAsync"/>。</para>
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FirmwareOpResult.Fail(
            "Applying a bsdiff patch requires a source file; use DeltaEngine.ApplyDeltaAsync.  应用 bsdiff 补丁需要源文件，请使用 DeltaEngine.ApplyDeltaAsync。"));

    /// <summary>
    /// Not supported: generating a patch needs source and target files; use <see cref="DeltaEngine.GenerateDeltaAsync"/>.
    /// <para>不支持：生成补丁需要源文件与目标文件；请使用 <see cref="DeltaEngine.GenerateDeltaAsync"/>。</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => Task.FromResult(FirmwareOpResult.Fail(
            "Generating a bsdiff patch requires source and target files; use DeltaEngine.GenerateDeltaAsync.  生成 bsdiff 补丁需要源文件与目标文件，请使用 DeltaEngine.GenerateDeltaAsync。"));
}
