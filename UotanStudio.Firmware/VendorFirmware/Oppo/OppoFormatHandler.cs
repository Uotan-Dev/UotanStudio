using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo;

/// <summary>
/// Format handler exposing Oppo OFP/OPS/OZIP archives through the shared unpack pipeline.
/// <para>通过共享解包流程对外提供 Oppo OFP/OPS/OZIP 归档的格式处理器。</para>
/// </summary>
public sealed class OppoFormatHandler : IReadonlyFormatHandler
{
    private readonly OppReader _reader = new();

    /// <summary>Display name shown for the Oppo firmware family. <para>Oppo 固件系列显示的标签名称。</para></summary>
    /// <value>A constant label; never empty. <para>固定标签；绝不为空。</para></value>
    public string FormatName => "Oppo Firmware (OFP/OPS/OZIP)";

    /// <summary>Lower-case file extensions this handler claims. <para>本处理器所声称的小写文件扩展名。</para></summary>
    /// <value>The read-only list <c>ofp</c>, <c>ops</c>, <c>ozip</c>. <para>只读列表 <c>ofp</c>、<c>ops</c>、<c>ozip</c>。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["ofp", "ops", "ozip"];

    /// <summary>
    /// Determines whether this handler can process the given file.
    /// <para>判断该处理器能否处理指定文件。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the candidate firmware file.
    /// <para>候选固件文件的路径。</para>
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="filePath"/> exists, carries a <c>.ofp</c>, <c>.ops</c> or <c>.ozip</c> extension, is not an ELF or ARM-signed image, and Oppo format detection resolves a known format or the strong extension alone is accepted after a detection error; otherwise <see langword="false"/>.
    /// <para>当 <paramref name="filePath"/> 存在、带有 <c>.ofp</c>、<c>.ops</c> 或 <c>.ozip</c> 扩展名、不是 ELF 或 ARM 签名镜像，且 Oppo 格式检测解析出已知格式，或检测出错后仅凭强扩展名即被接受时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        var strongExtension = extension.Equals(".ofp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ops", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ozip", StringComparison.OrdinalIgnoreCase);
        if (!strongExtension)
        {
            return false;
        }


        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> head = stackalloc byte[0x3C];
            var read = fs.Read(head);
            if (read >= 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
            {
                return false;
            }
            if (read >= 0x3C
                && head[0x38] == (byte)'A' && head[0x39] == (byte)'R'
                && head[0x3A] == (byte)'M' && head[0x3B] == (byte)'d')
            {
                return false;
            }
        }
        catch
        {

        }

        try
        {
            using var fs = File.OpenRead(filePath);
            var format = Parsers.FormatDetector.DetectFormatFromStream(fs);
            if (format != OppFormat.Unknown)
            {
                return true;
            }
        }
        catch
        {
            return strongExtension;
        }


        return strongExtension;
    }

    /// <summary>
    /// Reads format metadata without unpacking.
    /// <para>读取格式元数据（不解包）。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the Oppo firmware file to inspect.
    /// <para>要检查的 Oppo 固件文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed before and during parsing; cancelling it aborts the operation.
    /// <para>在解析前后观察的令牌；取消它会中止操作。</para>
    /// </param>
    /// <returns>A task whose result carries the format fields, per-entry component list and a status message; parsing errors are folded into a failed result.
    /// <para>任务结果携带格式字段、逐条目组件列表与状态消息；解析错误被折叠为失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被发出取消信号。</para>
    /// </exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {


                var archive = await _reader.ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
                var metadata = archive.Metadata;
                cancellationToken.ThrowIfCancellationRequested();

                var meta = new Dictionary<string, string>
                {
                    ["Format"] = metadata.FormatName,
                    ["FormatCode"] = metadata.Format.ToString(),
                    ["FileSize"] = metadata.FileSize.ToString(),
                    ["EntryCount"] = metadata.EntryCount.ToString(),
                };

                if (metadata.ProjectName is not null)
                {
                    meta["ProjectName"] = metadata.ProjectName;
                }

                if (metadata.CpuModel is not null)
                {
                    meta["CpuModel"] = metadata.CpuModel;
                }

                if (metadata.FlashType is not null)
                {
                    meta["FlashType"] = metadata.FlashType;
                }

                if (metadata.FirmwareName is not null)
                {
                    meta["FirmwareName"] = metadata.FirmwareName;
                }

                if (metadata.ProjectId is not null)
                {
                    meta["ProjectId"] = metadata.ProjectId;
                }

                if (metadata.KeyId is not null)
                {
                    meta["KeyId"] = metadata.KeyId;
                }


                var components = archive.Entries
                    .Where(e => IsFirmwareComponent(e.Name))
                    .Select(e => FirmwareComponent.Of(e.Name, e.Size))
                    .ToList();

                return FirmwareOpResult.Ok(
                    $"Read {metadata.EntryCount} entries from '{filePath}' as {metadata.FormatName}.",
                    components: components,
                    metadata: meta);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read Oppo firmware metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Unpacks the firmware file to the output directory.
    /// <para>将固件文件解包到输出目录。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the Oppo firmware file to unpack.
    /// <para>要解包的 Oppo 固件文件路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory; created when it does not exist.
    /// <para>目标目录；不存在时会创建。</para>
    /// </param>
    /// <param name="options">
    /// Unpack options carrying the overwrite policy and progress reporter, or <see langword="null"/> for the defaults.
    /// <para>包含覆盖策略与进度报告的解包选项；为 <see langword="null"/> 时使用默认值。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed before and during extraction; cancelling it aborts the operation.
    /// <para>在解包前后观察的令牌；取消它会中止操作。</para>
    /// </param>
    /// <returns>A task whose result reports the extracted entry count and names, or a failed result when extraction errors occur.
    /// <para>任务结果报告解包的条目数与名称；解包出错时返回失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被发出取消信号。</para>
    /// </exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.Core.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                options?.Progress?.Report(5, "Reading Oppo firmware metadata...");
                var archive = await _reader.ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
                var metadata = archive.Metadata;
                var names = archive.Entries.Select(e => e.Name).ToList();

                options?.Progress?.Report(10, "Extracting entries...");
                var extractOptions = new OppExtractOptions
                {
                    Overwrite = options?.Overwrite ?? true,
                };

                var result = await _reader.ExtractAsync(
                    filePath,
                    outputDirectory,
                    extractOptions,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                options?.Progress?.Report(100, "Extraction complete.");

                var meta = new Dictionary<string, string>
                {
                    ["Format"] = metadata.FormatName,
                    ["EntryCount"] = metadata.EntryCount.ToString(),
                };

                if (metadata.KeyId is not null)
                {
                    meta["KeyId"] = metadata.KeyId;
                }

                return FirmwareOpResult.Ok(
                    $"Extracted {result.ExtractedFileCount} entries from '{filePath}' to '{outputDirectory}'.",
                    names,
                    meta);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack Oppo firmware '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Determines whether an entry is a real firmware component (excludes OTA config, signing metadata, and other non-firmware directories).
    /// <para>判断条目是否为真正的固件组件（排除 OTA 配置、签名等非固件目录）。</para>
    /// </summary>
    private static bool IsFirmwareComponent(string name)
    {

        if (name.StartsWith("firmware-update/", StringComparison.Ordinal))
            return true;


        if (name.EndsWith(".dat.br", StringComparison.Ordinal)
            || name.EndsWith(".new.dat.br", StringComparison.Ordinal)
            || name.EndsWith(".transfer.list", StringComparison.Ordinal)
            || name.EndsWith(".patch.dat", StringComparison.Ordinal))
            return true;


        if (name.StartsWith("compatibility.zip", StringComparison.Ordinal)
            || name.StartsWith("oppo_update_script", StringComparison.Ordinal)
            || name.StartsWith("oppo_operate_data", StringComparison.Ordinal))
            return true;


        if (name.StartsWith("data_update_patch/", StringComparison.Ordinal))
            return false;
        if (name.StartsWith("META-INF/", StringComparison.Ordinal))
            return false;
        if (name.Equals("oppo_metadata", StringComparison.Ordinal))
            return false;


        return !name.Contains('/');
    }

    /// <summary>
    /// Pack operation.
    /// <para>打包操作。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Source directory or file to pack; unused because packing is unsupported.
    /// <para>要打包的源目录或文件；由于不支持打包而未使用。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination archive path; unused because packing is unsupported.
    /// <para>目标归档路径；由于不支持打包而未使用。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token checked before the unsupported result is produced; cancelling it aborts the operation.
    /// <para>在生成不支持结果前检查的令牌；取消它会中止操作。</para>
    /// </param>
    /// <returns>A task whose result is always a failed <see cref="FirmwareOpResult"/>; this handler only reads and unpacks Oppo firmware.
    /// <para>任务结果始终为失败的 <see cref="FirmwareOpResult"/>；本处理器仅读取与解包 Oppo 固件。</para></returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被发出取消信号。</para>
    /// </exception>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            return FirmwareOpResult.Fail(
                "Packing to Oppo firmware formats (OFP/OPS/OZIP) is not supported by this handler. " +
                "Only reading and unpacking are available.");
        }, cancellationToken);
    }
}