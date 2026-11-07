using System.Text;
using UotanStudio.Compress;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// A resolved partition content for card image synthesis: a factory that opens the content stream
/// on demand (once for measuring, once for writing) and an optional declared length in bytes.
/// <para>卡镜像合成用的一份分区内容：按需打开内容流的工厂（测量与写入各一次）以及可选的声明字节长度。</para>
/// </summary>
/// <param name="Open">Opens a fresh readable stream positioned at the content start; invoked multiple times and each returned stream is disposed by the writer.
/// <para>打开一个定位在内容起点的新可读流；会被多次调用，每个返回的流都由写入器释放。</para></param>
/// <param name="Length">Declared content length in bytes, or <see langword="null"/> when unknown (for example a gzip source), in which case the writer measures the stream itself.
/// <para>声明的内容字节长度；未知（例如 gzip 源）时为 <see langword="null"/>，此时由写入器自行测量。</para></param>
public sealed record SpacemiTCardContent(Func<Stream> Open, long? Length);

/// <summary>
/// Progress event of a card image synthesis run.
/// <para>卡镜像合成运行的进度事件。</para>
/// </summary>
/// <param name="Phase">Stage identifier: <c>write-partition</c> while copying partition data, <c>done</c> after the image is complete.
/// <para>阶段标识：复制分区数据时为 <c>write-partition</c>，镜像完成后为 <c>done</c>。</para></param>
/// <param name="Partition">Partition the event belongs to; <see langword="null"/> for run-level events.
/// <para>事件所属分区；运行级事件为 <see langword="null"/>。</para></param>
/// <param name="BytesCompleted">Bytes processed so far, when byte accounting applies.
/// <para>目前已处理的字节数；无字节计量时为 <see langword="null"/>。</para></param>
/// <param name="BytesTotal">Total bytes to process, when known in advance.
/// <para>待处理的总字节数；无法预知时为 <see langword="null"/>。</para></param>
/// <param name="Message">Human-readable detail of the event.
/// <para>事件的人类可读细节。</para></param>
public sealed record SpacemiTCardImageProgress(string Phase, string? Partition, long? BytesCompleted, long? BytesTotal, string Message);

/// <summary>
/// One partition laid out into a synthesized card image, as recorded by
/// <see cref="SpacemiTCardImageWriter.Write"/>.
/// <para><see cref="SpacemiTCardImageWriter.Write"/> 记录的、已排入合成卡镜像的一个分区。</para>
/// </summary>
/// <param name="Name">Partition name from the source table. <para>来自源表的分区名。</para></param>
/// <param name="OffsetBytes">Resolved byte offset of the partition inside the image.
/// <para>分区在镜像内解析出的字节偏移。</para></param>
/// <param name="LengthBytes">Reserved extent in bytes; the declared size, or the content length when the size is open-ended.
/// <para>预留区间的字节长度；为声明大小，size 开放时为内容长度。</para></param>
/// <param name="InGpt">Whether the partition occupies a GPT entry; hidden or empty partitions are written without one.
/// <para>分区是否占用 GPT 条目；隐藏或空分区只写数据不建条目。</para></param>
public sealed record SpacemiTCardWrittenPartition(string Name, long OffsetBytes, long LengthBytes, bool InGpt);

/// <summary>
/// Outcome of a card image synthesis run: the final image size and the per-partition layout.
/// <para>卡镜像合成运行的结果：最终镜像大小与逐分区布局。</para>
/// </summary>
/// <param name="ImageSizeBytes">Total image length in bytes. <para>镜像总字节长度。</para></param>
/// <param name="Partitions">Written partitions in source-table order.
/// <para>按源表顺序排列的已写分区。</para></param>
public sealed record SpacemiTCardImageResult(long ImageSizeBytes, IReadOnlyList<SpacemiTCardWrittenPartition> Partitions);

/// <summary>
/// Synthesizes a SpacemiT K1/K3 bootable card image (a standard GPT disk image) from a parsed
/// <c>partition_*.json</c> table and resolved partition contents. The layout replicates the device
/// firmware's <c>_parse_flash_config</c>: run-length entries continue at the running cursor, hidden
/// partitions (K1 <c>bootinfo</c> at LBA 0, K3 <c>bootinfo</c> at 1 MiB) carry data without a GPT
/// entry, and a trailing reserve of 34 blocks keeps the backup GPT clear of partition data. Content
/// that starts with a gzip header is decompressed before it lands in the image, so the card always
/// stores raw bytes regardless of the table's <c>gzip-N</c> transfer hint.
/// <para>从解析后的 <c>partition_*.json</c> 分区表与已解析的分区内容合成 SpacemiT K1/K3 可启动卡镜像
/// （标准 GPT 磁盘镜像）。布局复刻设备固件的 <c>_parse_flash_config</c>：缺省偏移的条目接续运行游标，
/// hidden 分区（K1 的 LBA 0 bootinfo、K3 的 1 MiB bootinfo）只写数据不建 GPT 条目，尾部预留 34 块
/// 使备份 GPT 不与分区数据重叠。以 gzip 头开头的内容会先解压再写入，因此无论表中的 <c>gzip-N</c>
/// 传输提示如何，卡内始终存放原始字节。</para>
/// </summary>
public static class SpacemiTCardImageWriter
{
    private const int CopyBufferBytes = 1024 * 1024;

    private const int MeasureBufferBytes = 64 * 1024;

    private const int ImageAlignBytes = 4096;

    /// <summary>Upper bound for a single partition's measured content, so a hostile gzip stream cannot
    /// expand into an unbounded host measurement. <para>单个分区测量内容的上限，防止恶意 gzip 流在宿主
    /// 测量阶段无限膨胀。</para></summary>
    private const long MaximumContentBytes = 512L << 30;

    /// <summary>Upper bound for the synthesized image, catching absurd table offsets that would otherwise
    /// ask the file system for an exabyte-scale file. <para>合成镜像的上限，拦截会向文件系统申请 EB 级
    /// 文件的荒谬表偏移。</para></summary>
    private const long MaximumImageBytes = 16L << 40;

    private const string UniqueIdDomainPrefix = "spacemit-card-image:";

    /// <summary>
    /// The standard EFI System Partition type GUID, which the vendor card layout assigns to its system-boot
    /// partition (named "EFI System" there, "ESP"/"esp" in the flash tables).
    /// <para>标准 EFI 系统分区类型 GUID，厂商卡布局把它赋给其 system-boot 分区（那里名为 "EFI System"，
    /// 刷机表中为 "ESP"/"esp"）。</para></summary>
    private static readonly Guid EfiSystemPartitionGuid = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");

    /// <summary>
    /// The Linux filesystem data type GUID, which the vendor card layout assigns to its data partitions (CIDATA,
    /// root) and which every partition outside the vendor table falls back to.
    /// <para>Linux 文件系统数据类型 GUID，厂商卡布局把它赋给其数据分区（CIDATA、root），厂商表之外的分区
    /// 也回退到它。</para></summary>
    private static readonly Guid LinuxFilesystemGuid = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    /// <summary>
    /// The partition type GUIDs copied from the vendor card layout definition
    /// (<c>gadget.yaml</c>, the official K3 Ubuntu card image): each firmware slot carries a
    /// vendor-chosen type GUID keyed by its partition name, and the system-boot partition carries
    /// the standard EFI type. A name the vendor layout does not list falls back to the Linux
    /// filesystem data type the same layout uses for its data partitions.
    /// <para>仿照厂商卡布局定义（<c>gadget.yaml</c>，官方 K3 Ubuntu 卡镜像）复制的分区类型 GUID：
    /// 每个固件槽位按分区名携带厂商选定的类型 GUID，system-boot 分区携带标准 EFI 类型。厂商布局未
    /// 列出的名称回退到同一布局中数据分区使用的 Linux 文件系统数据类型。</para>
    /// </summary>
    private static readonly Dictionary<string, Guid> VendorTypeGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["env"] = new("64C85018-D1B1-48BF-8D09-76A005AB67CC"),
        ["bootinfo"] = new("1794F3BC-344B-47DA-B2F5-C3C755D62CF8"),
        ["fsbl"] = new("A5EA2F6F-462B-4DC8-9D75-0A479AD90E90"),
        ["esos"] = new("93389EB0-D147-44B7-BBA2-0F37C4239B91"),
        ["opensbi"] = new("1B6AC811-C308-4920-8F90-8E86596DFD02"),
        ["uboot"] = new("A5923B95-7159-4FE1-B51F-7693EC44F96D"),
        ["esp"] = EfiSystemPartitionGuid,
        ["efi system"] = EfiSystemPartitionGuid,
    };

    /// <summary>
    /// Synthesizes the card image at <paramref name="destinationPath"/>: the GPT metadata is published
    /// first (zero-filled data area), then every partition's resolved content is streamed into its byte
    /// offset. A table in MTD format is rejected because NOR/NAND layouts cannot be expressed as a GPT
    /// disk image; overlapping partition ranges are rejected because they would corrupt each other on
    /// the card.
    /// <para>在 <paramref name="destinationPath"/> 合成卡镜像：先发布 GPT 元数据（数据区补零），再将每
    /// 个分区解析出的内容流式写入其字节偏移。MTD 格式的表会被拒绝，因为 NOR/NAND 布局无法表达为 GPT
    /// 磁盘镜像；互相重叠的分区区间也会被拒绝，因为它们在卡上会互相破坏。</para>
    /// </summary>
    /// <param name="table">Parsed partition table; only <c>format: gpt</c> tables are accepted.
    /// <para>解析后的分区表；只接受 <c>format: gpt</c> 的表。</para></param>
    /// <param name="resolve">Resolves a table image reference to its content; a <see langword="null"/>
    /// result leaves the partition as reserved zero-filled space. Exceptions thrown here pass through
    /// unchanged. <para>把表中的镜像引用解析为内容；返回 <see langword="null"/> 时该分区保留为补零
    /// 占位区。此处抛出的异常原样透传。</para></param>
    /// <param name="destinationPath">Destination image path, replaced by the synthesis result.
    /// <para>目标镜像路径，将被合成结果替换。</para></param>
    /// <param name="progress">Optional progress sink with phases <c>write-partition</c> and <c>done</c>.
    /// <para>可选的进度接收器，阶段为 <c>write-partition</c> 与 <c>done</c>。</para></param>
    /// <param name="cancellationToken">Cancels between and during writes; a cancelled run may leave a
    /// partially filled image at the destination. <para>在写入之间与写入期间取消；取消后目标处可能残留
    /// 未填满的镜像。</para></param>
    /// <returns>The final image size and the resolved per-partition layout.
    /// <para>最终镜像大小与解析出的逐分区布局。</para></returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.
    /// <para>参数为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is empty, or the table
    /// declares more than one image for a partition. <para><paramref name="destinationPath"/> 为空，或表中
    /// 某分区声明了多于一个镜像。</para></exception>
    /// <exception cref="InvalidDataException">The table is not a GPT table, a visible partition offset is
    /// unaligned or inside the GPT metadata area, or two partition ranges overlap.
    /// <para>表不是 GPT 表、可见分区偏移未对齐或落在 GPT 元数据区内，或两个分区区间重叠。</para></exception>
    /// <exception cref="EndOfStreamException">A content stream with a declared or measured length ended
    /// before that length was written. <para>声明或测量过长度的内容流在写满该长度前结束。</para></exception>
    /// <exception cref="IOException">The destination image cannot be written.
    /// <para>无法写入目标镜像。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌已被取消。</para></exception>
    public static SpacemiTCardImageResult Write(SpacemiTPartitionTable table,
        Func<string, SpacemiTCardContent?> resolve, string destinationPath,
        IProgress<SpacemiTCardImageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!table.Format.Equals(SpacemiTConstants.PartitionFormatGpt, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SpacemiT card image synthesis requires a '{SpacemiTConstants.PartitionFormatGpt}' partition table, not '{table.Format}'.");
        }

        var plan = PlanLayout(table, resolve, cancellationToken);
        GptImageComposer.WriteNewImage(destinationPath, GptLayout.SectorSize, plan.BlockCount,
            plan.GptPartitions, diskId: null, cancellationToken);
        using var image = new FileStream(destinationPath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, CopyBufferBytes);
        foreach (var partition in plan.Partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (partition.Content is null || partition.BytesToWrite == 0)
            {
                continue;
            }

            WritePartitionContent(image, partition, progress, cancellationToken);
        }

        progress?.Report(new SpacemiTCardImageProgress("done", null, plan.ImageSizeBytes, plan.ImageSizeBytes,
            $"card image written, {plan.ImageSizeBytes} bytes"));
        return new SpacemiTCardImageResult(plan.ImageSizeBytes, plan.ResultPartitions);
    }

    private sealed record PlannedPartition(
        SpacemiTPartitionEntry Entry,
        long Offset,
        long Extent,
        SpacemiTCardContent? Content,
        long BytesToWrite,
        bool InGpt);

    private sealed record LayoutPlan(
        ulong BlockCount,
        long ImageSizeBytes,
        List<GptImagePartition> GptPartitions,
        List<PlannedPartition> Partitions,
        List<SpacemiTCardWrittenPartition> ResultPartitions);

    /// <summary>
    /// Derives the full image layout the way the device firmware's <c>_parse_flash_config</c> does:
    /// entries without an offset continue at the running cursor, the cursor advances to every entry's
    /// end, and any two non-empty ranges (hidden included) must stay disjoint.
    /// <para>按设备固件 <c>_parse_flash_config</c> 的方式推导完整镜像布局：缺省偏移的条目接续运行游标，
    /// 游标推进到每个条目的终点，且任意两个非空区间（含 hidden）必须互不重叠。</para>
    /// </summary>
    private static LayoutPlan PlanLayout(SpacemiTPartitionTable table,
        Func<string, SpacemiTCardContent?> resolve, CancellationToken cancellationToken)
    {
        var partitions = new List<PlannedPartition>(table.Partitions.Count);
        var gptPartitions = new List<GptImagePartition>();
        var resultPartitions = new List<SpacemiTCardWrittenPartition>(table.Partitions.Count);
        var ranges = new List<(long Start, long End)>();
        var cursor = 0L;
        foreach (var entry in table.Partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = ResolveContent(entry, resolve);
            var contentLength = 0L;
            if (content is not null)
            {
                contentLength = content.Length
                    ?? (entry.Size > 0 ? -1 : MeasureContent(content, entry.Name, cancellationToken));
            }

            var offset = entry.Offset < 0 ? cursor : entry.Offset;
            var extent = entry.Size > 0 ? entry.Size : Math.Max(contentLength, 0);
            var end = checked(offset + extent);
            if (extent > 0)
            {
                foreach (var (start, stop) in ranges)
                {
                    if (offset < stop && start < end)
                    {
                        throw new InvalidDataException(
                            $"Partition '{entry.Name}' range [{offset}, {end}) overlaps another partition on the card.");
                    }
                }

                ranges.Add((offset, end));
            }

            var inGpt = false;
            if (!entry.Hidden && extent > 0)
            {
                if (offset % GptLayout.SectorSize != 0)
                {
                    throw new InvalidDataException(
                        $"Partition '{entry.Name}' byte offset {offset} is not {GptLayout.SectorSize}-aligned, so it cannot carry a GPT entry.");
                }

                if (offset < (long)GptLayout.FirstUsableLba * GptLayout.SectorSize)
                {
                    throw new InvalidDataException(
                        $"Partition '{entry.Name}' byte offset {offset} falls inside the mandatory GPT metadata area.");
                }

                var entryBlocks = (extent + GptLayout.SectorSize - 1) / GptLayout.SectorSize;
                var firstLba = (ulong)(offset / GptLayout.SectorSize);
                var lastLba = checked(firstLba + (ulong)entryBlocks - 1UL);
                // The type GUID follows the vendor card layout's own name-to-type table, so a synthesized card
                // reads back with the same partition types the official image carries.
                // 类型 GUID 遵循厂商卡布局自身的名称→类型表，使合成卡回读出的分区类型与官方镜像一致。
                gptPartitions.Add(new GptImagePartition(entry.Name, SelectTypeId(entry.Name),
                    DeriveUniqueId(entry.Name), firstLba, lastLba));
                inGpt = true;
            }

            var bytesToWrite = content is null
                ? 0
                : entry.Size > 0
                    ? (contentLength >= 0 ? Math.Min(contentLength, entry.Size) : -1)
                    : contentLength;
            cursor = Math.Max(cursor, end);
            partitions.Add(new PlannedPartition(entry, offset, extent, content, bytesToWrite, inGpt));
            resultPartitions.Add(new SpacemiTCardWrittenPartition(entry.Name, offset, extent, inGpt));
        }

        var imageSize = checked(cursor + (long)GptLayout.FirstUsableLba * GptLayout.SectorSize);
        if (imageSize > MaximumImageBytes)
        {
            throw new InvalidDataException(
                $"The partition table implies a {imageSize}-byte card image, above the {MaximumImageBytes}-byte limit.");
        }

        imageSize = (imageSize + ImageAlignBytes - 1) / ImageAlignBytes * ImageAlignBytes;
        return new LayoutPlan((ulong)(imageSize / GptLayout.SectorSize), imageSize,
            gptPartitions, partitions, resultPartitions);
    }

    private static SpacemiTCardContent? ResolveContent(SpacemiTPartitionEntry entry,
        Func<string, SpacemiTCardContent?> resolve)
    {
        if (entry.Images.Count == 0)
        {
            return null;
        }

        if (entry.Images.Count > 1)
        {
            throw new ArgumentException(
                $"Partition '{entry.Name}' declares {entry.Images.Count} images; card image synthesis supports exactly one image reference per partition.");
        }

        return resolve(entry.Images[0]);
    }

    /// <summary>
    /// Streams the content once to measure its decompressed length, rejecting streams that expand past
    /// the bounded limit so a decompression bomb cannot drive the layout.
    /// <para>对流做一次测量以得出解压后长度，超过有界上限即拒绝，使解压炸弹无法主导布局。</para>
    /// </summary>
    private static long MeasureContent(SpacemiTCardContent content, string partition,
        CancellationToken cancellationToken)
    {
        using var source = content.Open();
        using var data = OpenContentStream(source);
        var buffer = new byte[MeasureBufferBytes];
        long total = 0;
        int read;
        while ((read = data.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += read;
            if (total > MaximumContentBytes)
            {
                throw new InvalidDataException(
                    $"Content for partition '{partition}' measures beyond the {MaximumContentBytes}-byte limit.");
            }
        }

        return total;
    }

    private static void WritePartitionContent(FileStream image, PlannedPartition partition,
        IProgress<SpacemiTCardImageProgress>? progress, CancellationToken cancellationToken)
    {
        var entry = partition.Entry;
        var maximumBytes = partition.BytesToWrite >= 0 ? partition.BytesToWrite : partition.Extent;
        if (checked(partition.Offset + maximumBytes) > image.Length)
        {
            throw new InvalidDataException(
                $"Partition '{entry.Name}' write range exceeds the synthesized image length.");
        }

        using var source = partition.Content!.Open();
        using var data = OpenContentStream(source);
        image.Position = partition.Offset;
        var buffer = new byte[CopyBufferBytes];
        long written = 0;
        var total = partition.BytesToWrite >= 0 ? partition.BytesToWrite : (long?)null;
        while (written < maximumBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buffer.Length, maximumBytes - written);
            var read = data.Read(buffer, 0, want);
            if (read <= 0)
            {
                break;
            }

            image.Write(buffer, 0, read);
            written += read;
            progress?.Report(new SpacemiTCardImageProgress("write-partition", entry.Name, written, total,
                $"writing '{entry.Name}', {written} bytes"));
        }

        if (partition.BytesToWrite >= 0 && written < partition.BytesToWrite)
        {
            throw new EndOfStreamException(
                $"Content for partition '{entry.Name}' ended after {written} of {partition.BytesToWrite} bytes.");
        }
    }

    /// <summary>
    /// Wraps a content stream for reading: a seekable stream whose first bytes carry the gzip magic
    /// (<c>1f 8b 08</c> with no reserved flag bits, mirroring the device's own check) is transparently
    /// decompressed; anything else is read as raw bytes.
    /// <para>包装内容流以供读取：可寻址流的首字节携带 gzip 魔数（<c>1f 8b 08</c> 且无保留标志位，与设备
    /// 端判定一致）时透明解压；其余按原始字节读取。</para>
    /// </summary>
    private static Stream OpenContentStream(Stream source)
    {
        if (!source.CanSeek)
        {
            return source;
        }

        var position = source.Position;
        Span<byte> header = stackalloc byte[4];
        int read = 0;
        while (read < header.Length)
        {
            var filled = source.Read(header[read..]);
            if (filled <= 0)
            {
                break;
            }

            read += filled;
        }

        source.Position = position;
        if (read == header.Length && header[0] == 0x1f && header[1] == 0x8b
            && header[2] == 0x08 && (header[3] & 0x20) == 0)
        {
            return CompressionService.CreateDecompressionStream(source, CompressionFormat.Gzip);
        }

        return source;
    }

    /// <summary>
    /// Resolves the partition type GUID from the vendor card layout's own name-to-type table, falling
    /// back to the Linux filesystem data type for names the layout does not list.
    /// <para>从厂商卡布局自身的名称→类型表解析分区类型 GUID；布局未列出的名称回退到 Linux 文件系统
    /// 数据类型。</para>
    /// </summary>
    /// <param name="name">Partition name from the table. <para>分区表中的分区名。</para></param>
    /// <returns>The vendor type GUID, or the Linux filesystem data type fallback.
    /// <para>厂商类型 GUID；未命中时为 Linux 文件系统数据类型回退。</para></returns>
    private static Guid SelectTypeId(string name) =>
        VendorTypeGuids.TryGetValue(name, out var typeId) ? typeId : LinuxFilesystemGuid;

    /// <summary>
    /// Derives a deterministic unique partition GUID from the partition name, so repeated synthesis of
    /// the same table yields byte-identical GPT entries. This is an integrity label, not a security claim.
    /// <para>由分区名推导确定性的唯一分区 GUID，使同一张表的重复合成产出逐字节一致的 GPT 条目。该标识是
    /// 完整性标记，不构成安全声明。</para>
    /// </summary>
    private static Guid DeriveUniqueId(string name)
    {
        var digest = Md5.ComputeHash(Encoding.UTF8.GetBytes(UniqueIdDomainPrefix + name));
        digest[7] = (byte)((digest[7] & 0x0F) | 0x50);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest);
    }
}