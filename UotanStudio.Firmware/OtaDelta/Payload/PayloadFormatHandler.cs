using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Models;

namespace UotanStudio.Firmware.OtaDelta.Payload;

/// <summary>
/// Format handler for Android OTA <c>payload.bin</c> images, covering metadata reading, partition unpacking and directory repacking.
/// <para>Android OTA <c>payload.bin</c> 镜像的格式处理器，涵盖元数据读取、分区解包与目录重新打包。</para>
/// </summary>
public sealed class PayloadFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = true;
        metadata.PackingNotes = " The directory packer creates an unsigned full payload.";
    }

    /// <summary>
    /// Display name of the handled format.
    /// <para>所处理格式的显示名称。</para>
    /// </summary>
    /// <value>
    /// Always <c>"Android OTA Payload (payload.bin)"</c>; published with repack metadata records.
    /// <para>恒为 <c>"Android OTA Payload (payload.bin)"</c>；随重打包元数据记录一起发布。</para>
    /// </value>
    public string FormatName => "Android OTA Payload (payload.bin)";

    /// <summary>
    /// Conventional file-name extensions associated with this format.
    /// <para>与本格式关联的惯用文件扩展名。</para>
    /// </summary>
    /// <value>
    /// <c>bin</c> and <c>payload</c>, without a leading dot; for listing and filtering only, since
    /// <see cref="CanHandle"/> detects the format by magic bytes instead.
    /// <para><c>bin</c> 与 <c>payload</c>，不含前导点；仅用于列表与过滤，<see cref="CanHandle"/> 实际按魔数识别格式。</para>
    /// </value>
    public IReadOnlyList<string> FileExtensions { get; } = ["bin", "payload"];

    /// <summary>
    /// Determines whether this handler can process the given file.
    /// <para>判断该处理器能否处理指定文件。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path; blank or non-existent paths yield <see langword="false"/>.
    /// <para>候选文件路径；空白或不存在的路径返回 <see langword="false"/>。</para></param>
    /// <returns><see langword="true"/> when the first four bytes equal the <c>CrAU</c> payload magic;
    /// <see langword="false"/> for blank or missing paths and for any read error, which is swallowed.
    /// <para>前四字节等于 <c>CrAU</c> 载荷魔数时返回 <see langword="true"/>；路径空白、缺失或读取异常
    /// （异常被吞掉）时返回 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        return HeaderProbe.ProbeMagic(filePath, 4, 0, "CrAU"u8);
    }

    /// <summary>
    /// Reads format metadata without unpacking.
    /// <para>读取格式元数据（不解包）。</para>
    /// </summary>
    /// <param name="filePath">Local payload file, either a raw <c>payload.bin</c> or a ZIP archive wrapping one.
    /// <para>本地载荷文件，为原始 <c>payload.bin</c> 或包裹它的 ZIP 归档。</para></param>
    /// <param name="cancellationToken">Cancels the metadata read before or during I/O. <para>在 I/O 前后或期间取消元数据读取。</para></param>
    /// <returns>A task whose result succeeds with the partition name list, a display metadata dictionary (format,
    /// partition count, block size, minor version, manifest and signature geometry, max timestamp, partial-update
    /// flag) and one component per partition; parse failures are captured as a failure result.
    /// <para>任务结果：成功时携带分区名列表、展示元数据字典（格式、分区数、块大小、minor 版本、清单与签名几何、
    /// 最大时间戳、增量标记）以及每分区一个组件；解析失败以失败结果承载。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled; cancellation is never converted into a failure result.
    /// <para>令牌被取消；取消不会被转换为失败结果。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var metadata = await PayloadParser.GetPayloadMetadataAsync(filePath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var meta = new Dictionary<string, string>
                {
                    ["Format"] = FormatName,
                    ["PartitionCount"] = metadata.PartitionNames.Count.ToString(),
                    ["BlockSize"] = metadata.BlockSize.ToString(),
                    ["MinorVersion"] = metadata.Manifest.MinorVersion.ToString(),
                    ["ManifestLength"] = MetadataDisplay.Number(metadata.ManifestLength),
                    ["MetadataSignatureLength"] = MetadataDisplay.Number(metadata.MetadataSignatureLength),
                    ["BaseOffset"] = MetadataDisplay.Number(metadata.BaseOffset),
                    ["MaxTimestamp"] = MetadataDisplay.Number(metadata.MaxTimestamp),
                    ["SignaturesOffset"] = MetadataDisplay.Number(metadata.SignaturesOffset),
                    ["SignaturesSize"] = MetadataDisplay.Number(metadata.SignaturesSize),
                    ["PartialUpdate"] = metadata.Manifest.PartialUpdate.ToString(),
                };

                var components = PartitionInfoHelper.ComputePartitionInfo(metadata.Manifest)
                    .Select(p => FirmwareComponent.Of(p.Name, (long)p.SizeBytes))
                    .ToList();

                return FirmwareOpResult.Ok(
                    $"Read {metadata.PartitionNames.Count} partitions from '{filePath}'.",
                    metadata.PartitionNames,
                    meta,
                    components);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read payload metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Unpacks all partitions of the payload to the output directory.
    /// <para>将载荷的所有分区解包到输出目录。</para>
    /// </summary>
    /// <param name="filePath">Existing raw or ZIP-wrapped payload file. <para>已存在的原始或 ZIP 包裹的载荷文件。</para></param>
    /// <param name="outputDirectory">Directory receiving the extracted <c>*.img</c> files and the unified repack
    /// metadata record written after a successful extraction.
    /// <para>接收解包出的 <c>*.img</c> 文件，并在解包成功后写入统一重打包元数据记录的目录。</para></param>
    /// <param name="options">Optional progress reporting, per-partition output byte cap and overwrite policy;
    /// when overwrite is disabled a pre-existing metadata file fails the operation before extraction, otherwise a
    /// pre-existing file must already be recognized metadata.
    /// <para>可选的进度上报、每分区输出字节上限与覆盖策略；关闭覆盖时，已存在的元数据文件会在解包前使操作失败，
    /// 否则已存在文件必须本身是可识别的元数据。</para></param>
    /// <param name="cancellationToken">Cancels the extraction before or during I/O. <para>在 I/O 前或期间取消解包。</para></param>
    /// <returns>A task whose result succeeds with the extracted partition names, or carries the error message when
    /// the payload cannot be parsed or written.
    /// <para>任务结果：成功时携带解包出的分区名；载荷无法解析或写入时携带错误消息。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
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
                var metadata = await PayloadParser.GetPayloadMetadataAsync(filePath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                options?.Progress?.Report(5, "Reading payload manifest...");
                var byteProgressAdapter = options?.Progress is null
                    ? null
                    : new Progress<(long Processed, long Total)>(tuple =>
                    {
                        var (processed, total) = tuple;
                        if (total <= 0)
                        {
                            return;
                        }

                        var percent = 5.0 + (processed * 95.0) / total;
                        options.Progress.Report(Math.Clamp(percent, 5.0, 100.0), "Extracting payload partitions...");
                    });
                await PayloadParser.ExtractAsync(filePath, null, outputDirectory, cancellationToken,
                    byteProgress: byteProgressAdapter,
                    maximumPartitionBytes: options?.MaximumOutputBytes ?? PayloadExtractor.DefaultMaximumPartitionBytes).ConfigureAwait(false);
                options?.Progress?.Report(100, "Extraction complete.");

                return FirmwareOpResult.Ok(
                    $"Extracted {metadata.PartitionNames.Count} partitions from '{filePath}' to '{outputDirectory}'.",
                    metadata.PartitionNames);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Packs a directory of partition images into a payload.bin file.
    /// <para>将分区镜像目录打包为 payload.bin 文件。</para>
    /// </summary>
    /// <param name="inputPath">Directory whose top-level <c>*.img</c> files become partitions, ordered by file name
    /// (ordinal); each file name without its extension is the partition name.
    /// <para>其顶层 <c>*.img</c> 文件将按文件名（序数序）成为分区；去掉扩展名的文件名即分区名。</para></param>
    /// <param name="outputPath">Destination payload file; the result is an unsigned full payload.
    /// <para>目标载荷文件；产物为未签名的完整载荷。</para></param>
    /// <param name="cancellationToken">Cancels before or between partition additions. <para>在开始前或各分区添加之间取消。</para></param>
    /// <returns>A task whose result succeeds with the packed partition names, or fails when the input is not a
    /// directory, contains no <c>*.img</c> files, or the build raises an error.
    /// <para>任务结果：成功时携带已打包的分区名；输入不是目录、不含 <c>*.img</c> 文件或构建出错时返回失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
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

                var images = Directory.EnumerateFiles(inputPath, "*.img", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                    .ToList();
                if (images.Count == 0)
                {
                    return FirmwareOpResult.Fail($"No *.img partition images found in '{inputPath}'.");
                }

                var names = new List<string>();
                using (var builder = new PayloadBuilder())
                {
                    foreach (var image in images)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = Path.GetFileNameWithoutExtension(image);
                        builder.AddPartition(name, image);
                        names.Add(name);
                    }

                    builder.Build(outputPath);
                }

                return FirmwareOpResult.Ok(
                    $"Packed {names.Count} partitions from '{inputPath}' into '{outputPath}'.",
                    names);
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