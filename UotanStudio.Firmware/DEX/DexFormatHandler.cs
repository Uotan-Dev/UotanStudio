using System.Text;
using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Dex;

/// <summary>
/// <para>Dalvik Executable (DEX) 格式处理器：识别 .dex 文件、读取元数据、反汇编解包与重建打包。</para>
/// Dalvik Executable (DEX) format handler: detects .dex files, reads metadata, disassembles on unpack and rebuilds on pack.
/// </summary>
public sealed class DexFormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "Dalvik Executable (DEX)";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions { get; } = ["dex"];

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var header = FormatDetector.ReadHeader(filePath, 8);
            // 校验魔数：dex\n0xx\0
            // Validate the magic: dex\n0xx\0.
            if (header.Length < 8 || header[0] != (byte)'d' || header[1] != (byte)'e' || header[2] != (byte)'x' || header[3] != (byte)'\n')
            {
                return false;
            }
            if (header[4] < (byte)'0' || header[4] > (byte)'9'
                || header[5] < (byte)'0' || header[5] > (byte)'9'
                || header[6] < (byte)'0' || header[6] > (byte)'9'
                || header[7] != 0)
            {
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dex = DexFile.Load(filePath);
            return Task.FromResult(FirmwareOpResult.Ok(dex.Describe()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"DEX parse failed: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return FirmwareOpResult.Fail("Output directory is empty");
            }

            var dex = DexFile.Load(filePath);
            var text = Disassembler.DisassembleAll(dex);
            Directory.CreateDirectory(outputDirectory);

            var name = Path.GetFileNameWithoutExtension(filePath);
            var safeName = string.IsNullOrWhiteSpace(name) ? "dex" : name;
            var outputPath = Path.Combine(outputDirectory, safeName + ".smali");

            var overwrite = options?.Overwrite ?? true;
            if (File.Exists(outputPath) && !overwrite)
            {
                return FirmwareOpResult.Fail($"Output exists and overwrite is disabled: {outputPath}");
            }

            await File.WriteAllTextAsync(outputPath, text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return FirmwareOpResult.Ok($"Disassembled DEX to {outputPath}", [outputPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail($"DEX disassembly failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Output path is empty"));
            }

            var dex = DexFile.Load(inputPath);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            dex.Write(outputPath);
            return Task.FromResult(FirmwareOpResult.Ok($"Rebuilt DEX written to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"DEX rebuild failed: {ex.Message}"));
        }
    }
}