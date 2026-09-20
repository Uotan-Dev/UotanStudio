using System.Globalization;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Bsdiff;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff;

public sealed class DiffFormatHandler : IFormatHandler
{
    private const string BsdiffMagic = "BSDIFF40";
    private const int BsdiffHeaderSize = 32;

    public string FormatName => "bsdiff Binary Patch";

    public IReadOnlyList<string> FileExtensions { get; } = ["bsdiff"];

    private static bool HasSupportedMagic(ReadOnlySpan<byte> header) => header.Length >= 8 &&
        (header[..8].SequenceEqual("BSDIFF40"u8) ||
         (header[..5].SequenceEqual("BSDF2"u8) && header[5] is 1 or 2 && header[6] is 1 or 2 && header[7] is 1 or 2));

    /// <summary>
    /// Recognizes BSDIFF40 and Android BSDF2 patch headers.
    /// <para>识别 BSDIFF40 和 Android BSDF2 补丁头。</para>
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
            return HasSupportedMagic(header);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads bsdiff header metadata without unpacking: patch type, file length, and the 32-byte header's control-block length, diff-block length and reconstructed new-file size.
    /// <para>不解包读取 bsdiff 头部元数据：补丁类型、文件长度，以及 32 字节头部中的控制块长度、差异块长度、 还原出的新文件大小。</para>
    /// </summary>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var header = FormatDetector.ReadHeader(filePath, BsdiffHeaderSize);
                if (header.Length < BsdiffHeaderSize || !HasSupportedMagic(header))
                {
                    return FirmwareOpResult.Fail($"Not a bsdiff (BSDIFF40) patch: '{filePath}'.  不是 bsdiff (BSDIFF40) 补丁。");
                }

                var controlLength = BinaryDeltaPatch.ReadNumber(header.AsSpan(8, 8));
                var diffLength = BinaryDeltaPatch.ReadNumber(header.AsSpan(16, 8));
                var newSize = BinaryDeltaPatch.ReadNumber(header.AsSpan(24, 8));
                if (controlLength < 0 || diffLength < 0 || newSize < 0)
                {
                    return FirmwareOpResult.Fail("Invalid negative length in bsdiff header.  bsdiff 头部存在非法负长度。");
                }

                var fileLength = new FileInfo(filePath).Length;
                if (controlLength > fileLength - BsdiffHeaderSize || diffLength > fileLength - BsdiffHeaderSize - controlLength)
                    return FirmwareOpResult.Fail("Binary patch streams exceed the file length.");
                var metadata = new Dictionary<string, string>
                {
                    ["PatchType"] = header.AsSpan(0, 5).SequenceEqual("BSDF2"u8) ? "bsdiff (BSDF2)" : "bsdiff (BSDIFF40)",
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
    /// Not supported: applying a patch needs a source file; use DeltaEngine.ApplyDeltaAsync.
    /// <para>不支持：应用补丁需要源文件；请使用 DeltaEngine.ApplyDeltaAsync。</para>
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FirmwareOpResult.Fail(
            "Applying a bsdiff patch requires a source file; use DeltaEngine.ApplyDeltaAsync.  应用 bsdiff 补丁需要源文件，请使用 DeltaEngine.ApplyDeltaAsync。"));

    /// <summary>
    /// Not supported: generating a patch needs source and target files; use DeltaEngine.GenerateDeltaAsync.
    /// <para>不支持：生成补丁需要源文件与目标文件；请使用 DeltaEngine.GenerateDeltaAsync。</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => Task.FromResult(FirmwareOpResult.Fail(
            "Generating a bsdiff patch requires source and target files; use DeltaEngine.GenerateDeltaAsync.  生成 bsdiff 补丁需要源文件与目标文件，请使用 DeltaEngine.GenerateDeltaAsync。"));
}