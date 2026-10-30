using System.Globalization;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.VendorFirmware.Rockchip;

/// <summary>
/// Detection, metadata reading and entry extraction for Rockchip RKBOOT/RKLOADER images, the <c>MiniLoaderAll.bin</c>
/// and <c>loader_v*.bin</c> files that <c>rkdeveloptool db</c> and RKDevTool upload to a boot ROM.
/// <para>瑞芯微 RKBOOT/RKLOADER 镜像（<c>rkdeveloptool db</c> 与 RKDevTool 上传到 boot ROM 的
/// <c>MiniLoaderAll.bin</c> 与 <c>loader_v*.bin</c>）的识别、元数据读取与条目解包。</para>
/// </summary>
/// <remarks>
/// The handler is read-only: it reports the header and the staged entry tables and extracts the declared entry payloads,
/// and never rewrites the image. Detection is content-only because loader files carry the generic <c>.bin</c> suffix.
/// <para>本处理器只读：上报头部与分阶段条目表，并解出声明的条目负载，绝不改写镜像。由于 loader 文件使用通用的
/// <c>.bin</c> 后缀，识别完全依赖内容。</para>
/// </remarks>
internal sealed class RockchipLoaderFormatHandler : IReadonlyFormatHandler, IHeaderFormatHandler
{
    /// <summary>Display name of the Rockchip loader image format.
    /// <para>瑞芯微 loader 镜像格式的显示名称。</para>
    /// </summary>
    /// <value>The constant <c>Rockchip Loader Image</c>, also the <see cref="FormatDetector"/> registration key.
    /// <para>常量 <c>Rockchip Loader Image</c>，同时作为 <see cref="FormatDetector"/> 的注册键。</para></value>
    public string FormatName => "Rockchip Loader Image";

    /// <summary>Extensions this handler claims before content inspection.
    /// <para>在内容检查之前本处理器声明认领的扩展名。</para>
    /// </summary>
    /// <value>An empty list: <c>.bin</c> is shared with many unrelated payloads, so only the <c>BOOT</c>/<c>LDR </c>
    /// header decides acceptance. <para>空列表：<c>.bin</c> 与大量无关负载共用，因此只由 <c>BOOT</c>/<c>LDR </c>
    /// 头部决定是否接受。</para></value>
    public IReadOnlyList<string> FileExtensions => [];

    /// <summary>Determines whether a file is an RKBOOT/RKLOADER image by reading its header.
    /// <para>通过读取头部判断文件是否为 RKBOOT/RKLOADER 镜像。</para>
    /// </summary>
    /// <param name="filePath">Candidate file; missing, blank or unreadable paths never match.
    /// <para>候选文件；缺失、空白或不可读的路径绝不匹配。</para></param>
    /// <returns><see langword="true"/> when the file is long enough and starts with a self-consistent loader header.
    /// <para>文件足够长且以自洽的 loader 头部开头时为 <see langword="true"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            if (stream.Length < RockchipLoaderImage.HeaderSize + RockchipLoaderImage.ChecksumSize)
            {
                return false;
            }

            var header = new byte[RockchipLoaderImage.HeaderSize];
            stream.ReadExactly(header);
            return RockchipLoaderImage.HasHeader(header);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Determines whether an already-read header prefix is a loader header, so detection needs no second open.
    /// <para>依据已读取的头部前缀判断是否为 loader 头部，使识别无需再次打开文件。</para>
    /// </summary>
    /// <param name="header">Leading image bytes. <para>镜像起始字节。</para></param>
    /// <param name="filePath">Path the header came from; unused because the decision is content-only.
    /// <para>头部来源路径；判定只依赖内容，因此不使用。</para></param>
    /// <returns><see langword="true"/> when <see cref="RockchipLoaderImage.HasHeader"/> accepts the prefix.
    /// <para><see cref="RockchipLoaderImage.HasHeader"/> 接受该前缀时为 <see langword="true"/>。</para></returns>
    public bool CanHandle(ReadOnlySpan<byte> header, string filePath) => RockchipLoaderImage.HasHeader(header);

    /// <summary>Reads the loader header, the three entry tables and the trailing checksum without extracting anything.
    /// <para>读取 loader 头部、三张条目表与末尾校验和，不提取任何内容。</para>
    /// </summary>
    /// <param name="filePath">Loader image to inspect; the file is opened read-only.
    /// <para>待检查的 loader 镜像；文件以只读方式打开。</para></param>
    /// <param name="cancellationToken">Cancels between entry tables and between checksum chunks.
    /// <para>在各条目表之间与各校验和数据块之间取消。</para></param>
    /// <returns>Outcome carrying the tag, loader and merge version, release time, chip field, sign and RC4 flags,
    /// per-table entry counts, the stored checksum with its verification status, and one component per entry named by
    /// <see cref="RockchipLoaderEntry.FileName"/>. A checksum mismatch is reported as
    /// <c>ChecksumStatus</c>=<c>Mismatch</c> and still returns a successful outcome, so the panel can display the
    /// declared structure of a damaged image; structural violations return a failed outcome instead.
    /// <para>结果对象携带标签、loader 与合并版本、发布时间、芯片字段、签名与 RC4 标志、各表条目数量、
    /// 存储校验和及其校验状态，以及按 <see cref="RockchipLoaderEntry.FileName"/> 命名的每条目组件。校验和不一致会以
    /// <c>ChecksumStatus</c>=<c>Mismatch</c> 上报但仍返回成功结果，使面板可以展示损坏镜像声明的结构；结构违规则返回失败结果。</para></returns>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = OpenImage(filePath);
            var image = RockchipLoaderImage.Read(stream, cancellationToken);
            var metadata = new Dictionary<string, string>
            {
                ["LoaderTag"] = image.Tag.TrimEnd(),
                ["LoaderVersion"] = image.VersionText,
                ["MergeVersion"] = image.MergeVersionText,
                ["SupportChip"] = image.Chip,
                ["Signed"] = image.Signed ? "True" : "False",
                ["Rc4Disabled"] = image.Rc4Disabled ? "True" : "False",
                ["HeaderSize"] = image.DeclaredHeaderSize.ToString(CultureInfo.InvariantCulture),
                ["EntryCount471"] = image.EntryCount471.ToString(CultureInfo.InvariantCulture),
                ["EntryCount472"] = image.EntryCount472.ToString(CultureInfo.InvariantCulture),
                ["EntryCountLoader"] = image.EntryCountLoader.ToString(CultureInfo.InvariantCulture),
                ["LoaderChecksum"] = "0x" + image.StoredChecksum.ToString("X8", CultureInfo.InvariantCulture),
                ["ChecksumStatus"] = image.ChecksumMatches ? "Verified" : "Mismatch",
            };
            if (image.ReleaseTime.Length > 0)
            {
                metadata["ReleaseTime"] = image.ReleaseTime;
            }

            var summary =
                $"Rockchip {image.Tag.TrimEnd()} loader {image.VersionText}: {image.Entries.Count} entries";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                metadata: metadata,
                components: [.. image.Entries.Select(static entry => FirmwareComponent.Of(entry.FileName, entry.DataSize))]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return Task.FromResult(FirmwareOpResult.Fail(exception.Message));
        }
    }

    /// <summary>Extracts the declared payload of every entry into the output directory.
    /// <para>把每个条目的声明负载解出到输出目录。</para>
    /// </summary>
    /// <param name="filePath">Loader image to unpack; never modified.
    /// <para>待解包的 loader 镜像；不会被修改。</para></param>
    /// <param name="outputDirectory">Destination directory, created when absent.
    /// <para>目标目录，不存在时创建。</para></param>
    /// <param name="options">Entry selection, per-entry size ceiling, overwrite policy and progress sink;
    /// <see langword="null"/> extracts every entry with defaults. Selection compares the entry file names reported by
    /// <see cref="ReadMetadataAsync"/> case-insensitively.
    /// <para>条目选择、单条目大小上限、覆盖策略与进度回调；传 <see langword="null"/> 时按默认值解出全部条目。
    /// 选择按 <see cref="ReadMetadataAsync"/> 上报的条目文件名忽略大小写比较。</para></param>
    /// <param name="cancellationToken">Cancels between entries and inside a single copy; a cancelled extraction
    /// surfaces as an <see cref="OperationCanceledException"/> and leaves no partially written payload behind.
    /// <para>在条目之间与单次拷贝内部取消；取消会以 <see cref="OperationCanceledException"/> 呈现，且不会残留写了一半的负载。</para></param>
    /// <returns>Outcome listing the written file names and their sizes, or a failed outcome when the image is structurally
    /// invalid, its checksum does not match, an entry exceeds the size ceiling, or an output file exists with
    /// overwriting disabled. <para>结果对象列出已写入的文件名及其大小；镜像结构无效、校验和不一致、条目超过大小上限，
    /// 或禁用覆盖时输出文件已存在，均返回失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new UnpackOptions();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return FirmwareOpResult.Fail("Output directory is empty");
        }

        try
        {
            using var stream = OpenImage(filePath);
            var image = RockchipLoaderImage.Read(stream, cancellationToken);
            if (!image.ChecksumMatches)
            {
                return FirmwareOpResult.Fail(
                    $"Rockchip loader checksum mismatch: stored 0x{image.StoredChecksum:X8}, computed 0x{image.ComputedChecksum:X8}.");
            }

            var selected = options.CreateSelectedEntrySet();
            var writes = image.Entries
                .Where(entry => entry.DataSize > 0 && (selected is null || selected.Contains(entry.FileName)))
                .ToList();
            Directory.CreateDirectory(outputDirectory);

            List<string> written = [];
            List<FirmwareComponent> components = [];
            var total = Math.Max(1, writes.Count);
            foreach (var entry in writes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.DataSize > options.MaximumOutputBytes)
                {
                    throw new InvalidDataException(
                        $"Loader entry '{entry.FileName}' declares {entry.DataSize} bytes, above the {options.MaximumOutputBytes}-byte limit.");
                }

                var destination = FileExtraction.ResolvePath(outputDirectory, entry.FileName);
                await FileExtraction.WriteAtomicAsync(destination, async output =>
                {
                    stream.Position = entry.DataOffset;
                    await StreamCopy.CopyExactlyAsync(stream, output, entry.DataSize, cancellationToken).ConfigureAwait(false);
                }, options.Overwrite, cancellationToken).ConfigureAwait(false);

                written.Add(entry.FileName);
                components.Add(FirmwareComponent.Of(entry.FileName, entry.DataSize));
                options.Progress?.Report(written.Count * 100.0 / total, entry.FileName);
            }

            return FirmwareOpResult.Ok(
                $"Extracted {written.Count} loader entries to {outputDirectory}", entries: written, components: components);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return FirmwareOpResult.Fail(exception.Message);
        }
    }

    private static FileStream OpenImage(string filePath)
        => new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
}