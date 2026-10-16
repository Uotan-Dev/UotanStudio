using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.AndroidMeta.Dex;

/// <summary>
/// Format handler for Dalvik Executable images: magic sniffing on the 8-byte header, metadata
/// summaries, whole-file smali disassembly and model rebuild back to <c>.dex</c> bytes. All parsing
/// treats the file as untrusted input and every failure is reported through
/// <see cref="FirmwareOpResult"/> rather than an exception, except cancellation.
/// <para>Dalvik 可执行镜像的格式处理器：基于 8 字节头部的魔数探测、元数据摘要、整文件 smali 反汇编，
/// 以及把模型重建回 <c>.dex</c> 字节。所有解析都将文件视为不可信输入，除取消外的一切失败都通过
/// <see cref="FirmwareOpResult"/> 报告，而不抛出异常。</para>
/// </summary>
public sealed class DexFormatHandler : IFormatHandler
{
    /// <summary>Display name of the handled format. <para>所处理格式的显示名称。</para></summary>
    /// <value>The fixed label "Dalvik Executable (DEX)". <para>固定标签 "Dalvik Executable (DEX)"。</para></value>
    public string FormatName => "Dalvik Executable (DEX)";

    /// <summary>Lowercase file extensions claimed by this handler. <para>本处理器声明的小写文件扩展名。</para></summary>
    /// <value>A single-entry list containing <c>"dex"</c>. <para>仅含 <c>"dex"</c> 的单元素列表。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["dex"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">Path to probe; only its first 8 bytes are read. <para>待探测的路径；仅读取前 8 个字节。</para></param>
    /// <returns><see langword="true"/> when the file exists and its magic is <c>dex\n</c> followed by
    /// three ASCII digits and a NUL byte; <see langword="false"/> for blank paths, missing files and
    /// any read error.
    /// <para>当文件存在且魔数为 <c>dex\n</c> 加三个 ASCII 数字与一个 NUL 字节时返回 <see langword="true"/>；
    /// 路径为空、文件缺失或任何读取错误均返回 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var header = FormatDetector.ReadHeader(filePath, 8);


            if (header.Length < 8 || !header.AsSpan(0, 4).SequenceEqual("dex\n"u8))
            {
                return false;
            }
            return header[4] is >= (byte)'0' and <= (byte)'9'
                && header[5] is >= (byte)'0' and <= (byte)'9'
                && header[6] is >= (byte)'0' and <= (byte)'9'
                && header[7] == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">DEX file to parse in full. <para>需要完整解析的 DEX 文件。</para></param>
    /// <param name="cancellationToken">Observed before parsing begins. <para>在解析开始前生效。</para></param>
    /// <returns>A successful result carrying the <see cref="DexFile.Describe"/> summary, or a failure
    /// result whose message starts with "DEX parse failed:" for any parse or I/O error.
    /// <para>成功结果携带 <see cref="DexFile.Describe"/> 摘要；任何解析或 I/O 错误返回以 "DEX parse failed:" 开头的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled. <para><paramref name="cancellationToken"/> 已触发。</para></exception>
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

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    /// <param name="filePath">DEX file to disassemble in full. <para>需要整体反汇编的 DEX 文件。</para></param>
    /// <param name="outputDirectory">Directory created on demand and written with one UTF-8 <c>&lt;name&gt;.smali</c> file plus the repack metadata document. <para>按需创建的目录；写入单个 UTF-8 <c>&lt;name&gt;.smali</c> 文件与重打包元数据文档。</para></param>
    /// <param name="options">Extraction options; <c>null</c> behaves as overwrite-enabled. <para>解包选项；<c>null</c> 等同于允许覆盖。</para></param>
    /// <param name="cancellationToken">Observed before and during file writes; a cancelled run throws without a failure result. <para>在写入前后生效；取消时直接抛出而不返回失败结果。</para></param>
    /// <returns>A successful result listing the emitted smali file, or a failure result (messages
    /// prefixed "Output directory is empty", "Output exists and overwrite is disabled" or "DEX
    /// disassembly failed"). The repack metadata step never overwrites a non-metadata file of the
    /// same name.
    /// <para>成功结果列出生成的 smali 文件；失败结果前缀为 "Output directory is empty"、
    /// "Output exists and overwrite is disabled" 或 "DEX disassembly failed"。重打包元数据步骤绝不覆盖
    /// 同名的非元数据文件。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled. <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Core.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private async Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    /// <param name="inputPath">Existing DEX file parsed into the model before rebuilding. <para>重建前先解析为模型的既有 DEX 文件。</para></param>
    /// <param name="outputPath">Destination file; its parent directory is created on demand and an
    /// existing file is replaced only through the atomic temporary-file swap of <see cref="DexFile.Write"/>.
    /// <para>目标文件；父目录按需创建，既有文件仅通过 <see cref="DexFile.Write"/> 的临时文件原子替换被覆盖。</para></param>
    /// <param name="cancellationToken">Observed before the rebuild; a cancelled run throws without a failure result. <para>在重建开始前生效；取消时直接抛出而不返回失败结果。</para></param>
    /// <returns>A successful result listing the rebuilt DEX file, or a failure result (messages
    /// prefixed "Output path is empty" or "DEX rebuild failed") for any parse or I/O error.
    /// <para>成功结果列出重建后的 DEX 文件；任何解析或 I/O 错误返回以 "Output path is empty" 或
    /// "DEX rebuild failed" 开头的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled. <para><paramref name="cancellationToken"/> 已触发。</para></exception>
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