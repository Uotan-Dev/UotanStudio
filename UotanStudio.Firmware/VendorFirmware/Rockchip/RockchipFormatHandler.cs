using System.Globalization;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.VendorFirmware.Rockchip;

/// <summary>
/// Inspects, extracts and rebuilds Rockchip RKAF and RKFW firmware containers.
/// <para>检查、解包并重新打包瑞芯微 RKAF 与 RKFW 固件容器。</para>
/// </summary>
internal sealed class RockchipFormatHandler : IFormatHandler
{
    /// <summary>Display name of the Rockchip firmware family.
    /// <para>瑞芯微固件族的显示名称。</para></summary>
    /// <value>A fixed label shown in format lists.
    /// <para>格式列表中显示的固定名称。</para></value>
    public string FormatName => "Rockchip RKAF/RKFW Firmware";

    /// <summary>File extensions owned by this handler.
    /// <para>本处理器负责的文件扩展名。</para></summary>
    /// <value>The dot-less extensions <c>rkfw</c> and <c>rkaf</c>.
    /// <para>不带点号的 <c>rkfw</c> 与 <c>rkaf</c>。</para></value>
    public IReadOnlyList<string> FileExtensions => ["rkfw", "rkaf"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">Path probed for a little-endian <c>RKFW</c> or <c>RKAF</c> magic at offset 0.
    /// <para>将在偏移 0 处探测小端序 <c>RKFW</c> 或 <c>RKAF</c> 魔数的路径。</para></param>
    /// <returns><see langword="true"/> when the file exists, is at least the RKFW header size, and starts with a
    /// known magic; <see langword="false"/> for blank paths, missing files and any read error.
    /// <para>文件存在、不小于 RKFW 头部长度且以已知魔数开头时为 <see langword="true"/>；路径为空、文件缺失或
    /// 读取异常时为 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        return HeaderProbe.ProbeMagic(filePath, RkFwHeader.StructSize, 0, RkFwHeader.MagicRkfw)
            || HeaderProbe.ProbeMagic(filePath, RkFwHeader.StructSize, 0, RkFwHeader.MagicRkaf);
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">Container file selected by the 4-byte magic; RKFW headers are previewed field by
    /// field, RKAF files are enumerated through the entry table.
    /// <para>由 4 字节魔数判定类型的容器文件；RKFW 逐字段预览头部，RKAF 按条目表枚举。</para></param>
    /// <param name="cancellationToken">Checked before and after parsing; cancellation is rethrown.
    /// <para>在解析前后分别检查；取消时向上抛出。</para></param>
    /// <returns>An <see cref="FirmwareOpResult"/> whose metadata reports container kind, entry count, per-entry
    /// names and sizes, and for RKFW the decoded version, chip, offsets and a release time only when it falls
    /// inside a valid calendar range; <c>ChecksumStatus</c> is <c>Verified</c> for RKAF and
    /// <c>NotVerified</c> for RKFW. Any read or parse failure produces a failed result carrying the message.
    /// <para>返回的 <see cref="FirmwareOpResult"/> 元数据包含容器种类、条目数量、各条目名称与大小；RKFW 还包含
    /// 解析出的版本、芯片、偏移，且仅当发布时间落在有效日历范围内才报告；RKAF 的 <c>ChecksumStatus</c> 为
    /// <c>Verified</c>，RKFW 为 <c>NotVerified</c>。读取或解析失败时返回携带错误信息的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[4];
            stream.ReadExactly(magic);
            var isRkfw = magic.SequenceEqual("RKFW"u8);
            var (header, entries) = isRkfw
                ? RockchipExtractor.PreviewRkfw(filePath)
                : (null, RockchipExtractor.List(filePath));
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = new Dictionary<string, string>
            {
                ["Container"] = isRkfw ? "RKFW" : "RKAF",
                ["FileCount"] = entries.Count.ToString(CultureInfo.InvariantCulture),
                ["ChecksumStatus"] = isRkfw ? "NotVerified" : "Verified"
            };
            if (header is not null)
            {
                metadata["RkfwVersion"] = $"{header.Version >> 24}.{(header.Version >> 16) & 0xff}.{header.Version & 0xffff}";
                metadata["MergeVersion"] = $"0x{header.MergeVersion:X8}";
                metadata["ChipId"] = $"0x{header.Chip:X8}";
                metadata["LoaderOffset"] = header.BootOffset.ToString(CultureInfo.InvariantCulture);
                metadata["LoaderSize"] = header.BootSize.ToString(CultureInfo.InvariantCulture);
                metadata["ImageOffset"] = header.ImageOffset.ToString(CultureInfo.InvariantCulture);
                metadata["ImageSize"] = header.ImageSize.ToString(CultureInfo.InvariantCulture);
                var time = header.ReleaseTime;
                var year = time[0] | time[1] << 8;
                if (year is >= 1 and <= 9999 && time[2] is >= 1 and <= 12 &&
                    time[3] >= 1 && time[3] <= DateTime.DaysInMonth(year, time[2]) &&
                    time[4] < 24 && time[5] < 60 && time[6] < 60)
                    metadata["ReleaseTime"] = $"{year:D4}-{time[2]:D2}-{time[3]:D2} {time[4]:D2}:{time[5]:D2}:{time[6]:D2}";
            }
            var summary = $"Rockchip {(isRkfw ? "RKFW" : "RKAF")}: {entries.Count} entries";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. entries.Select(static e => e.Name)],
                metadata: metadata,
                components: [.. entries.Select(static e => FirmwareComponent.Of(e.Name, e.Size))]));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    /// <param name="filePath">
    /// RKAF or RKFW container to extract.
    /// <para>待解包的 RKAF 或 RKFW 容器。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Directory receiving the extracted entries and, on success, the repack metadata file.
    /// <para>接收解出条目以及成功后打包元数据文件的目录。</para>
    /// </param>
    /// <param name="options">
    /// Extraction policy; <c>null</c> uses defaults (overwrite enabled, no progress sink). The optional progress
    /// sink receives percentage reports while entries are written.
    /// <para>解包策略；为 <c>null</c> 时使用默认值（允许覆盖、不上报进度）。可选的进度回调在写出条目时接收百分比上报。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the extraction; cancellation is rethrown rather than converted to a failed result.
    /// <para>取消解包；取消会向上抛出，而不是转换为失败结果。</para>
    /// </param>
    /// <returns>
    /// An <see cref="FirmwareOpResult"/> reporting the extracted entry count, or a failed result carrying the
    /// error message when the container is malformed or the output cannot be written.
    /// <para>返回报告解出条目数的 <see cref="FirmwareOpResult"/>；容器格式错误或输出无法写入时返回携带错误信息的失败结果。</para>
    /// </returns>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.Core.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var count = RockchipExtractor.Extract(filePath, outputDirectory, ToProgress(options?.Progress),
                options?.Overwrite ?? true, cancellationToken);
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} entries to {outputDirectory}"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Source directory handed to <see cref="RockchipPacker"/>.
    /// <para>交给 <see cref="RockchipPacker"/> 的源目录。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination container path.
    /// <para>目标容器路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the packing; cancellation is rethrown.
    /// <para>取消打包；取消时向上抛出。</para>
    /// </param>
    /// <returns>
    /// The result of the overload using default <see cref="RockchipPackOptions"/>.
    /// <para>使用默认 <see cref="RockchipPackOptions"/> 重载的打包结果。</para>
    /// </returns>
    public async Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => await PackAsync(inputPath, outputPath, new RockchipPackOptions(), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Packs the input into firmware asynchronously with explicit header options.
    /// <para>按显式头部选项异步将输入打包为固件。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Source directory handed to <see cref="RockchipPacker"/>; it selects RKFW or RKAF packing.
    /// <para>交给 <see cref="RockchipPacker"/> 的源目录；由其决定按 RKFW 还是 RKAF 打包。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination container path, written atomically by the packer.
    /// <para>目标容器路径，由打包器原子写出。</para>
    /// </param>
    /// <param name="options">
    /// Header identity, version, timestamp and template-reuse settings applied during packing.
    /// <para>打包时应用的头部标识、版本、时间戳与模板复用设置。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the packing; cancellation is rethrown rather than converted to a failed result.
    /// <para>取消打包；取消会向上抛出，而不是转换为失败结果。</para>
    /// </param>
    /// <returns>
    /// An <see cref="FirmwareOpResult"/> reporting the entry count of the freshly written container, or a failed
    /// result carrying the packing error message.
    /// <para>返回报告新容器条目数的 <see cref="FirmwareOpResult"/>，或携带打包错误信息的失败结果。</para>
    /// </returns>
    public async Task<FirmwareOpResult> PackAsync(
        string inputPath,
        string outputPath,
        RockchipPackOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await RockchipPacker.PackAsync(inputPath, outputPath, options, cancellationToken).ConfigureAwait(false);
            var entries = RockchipExtractor.List(outputPath);
            return FirmwareOpResult.Ok($"Packed Rockchip firmware with {entries.Count} entries.",
                entries: [.. entries.Select(static e => e.Name)],
                metadata: new Dictionary<string, string> { ["FileCount"] = entries.Count.ToString() });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    private static IProgress<double>? ToProgress(IFirmwareProgress? progress)
        => progress is null ? null : new FirmwareProgressAdapter(progress, "Extracting Rockchip firmware");
}