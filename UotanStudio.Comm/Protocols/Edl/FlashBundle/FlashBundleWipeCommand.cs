namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// The caller-supplied phrase authorising the destructive wipe phase of a flash bundle. A wipe overwrites data with zeros and cannot be undone, so the phrase must be exactly <c>WIPE</c>; anything else is refused before a command document exists, with no device write at all.
/// <para>调用方提供、用于授权 flash bundle 破坏性擦除阶段的短语。擦除以零覆盖数据且不可撤销，因此短语必须严格等于 <c>WIPE</c>；其他任何值在生成命令文档之前即被拒绝，完全不进行设备写入。</para>
/// </summary>
/// <param name="Phrase">The caller-supplied authorization phrase.
/// <para>调用方提供的授权短语。</para></param>
public sealed record WipeConfirmation(string Phrase)
{
    /// <summary>
    /// The only phrase that authorises a wipe, compared with ordinal case sensitivity; the single source of truth
    /// for both the caller that builds the confirmation and the bundle that checks it.
    /// <para>唯一可授权擦写的短语，按区分大小写的序数方式比较；构造确认的调用方与校验它的刷写包共用此一定义。</para>
    /// </summary>
    /// <value>The literal <c>WIPE</c>.</value>
    public const string RequiredPhrase = "WIPE";
}

/// <summary>
/// What a flash bundle would wipe, or why it refused to. The preview is the offline and pre-flight report of a
/// <c>&lt;wipe&gt;</c> manifest request: <see cref="CommandXml"/> is the exact command document that would go on
/// the wire, and a refusal leaves it <see langword="null"/> and records the reason instead.
/// <para>刷写包将要擦除的内容，或其拒绝理由。预览是对 <c>&lt;wipe&gt;</c> 清单请求的离线与预检报告：
/// <see cref="CommandXml"/> 就是即将上线的精确命令文档；被拒绝时它保持 <see langword="null"/>，
/// 并改为记录拒绝理由。</para>
/// </summary>
public sealed record FlashWipePreview
{
    /// <summary>
    /// Gets the display name of the wipe request, taken from its manifest label.
    /// <para>获取擦写请求的显示名称，取自清单标签。</para>
    /// </summary>
    /// <value>The label, or the zeros payload filename when the element carries no label.</value>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the physical partition or LUN the request addresses.
    /// <para>获取本请求针对的物理分区或 LUN。</para>
    /// </summary>
    /// <value>The literal physical_partition_number value.</value>
    public required int PhysicalPartitionNumber { get; init; }

    /// <summary>
    /// Gets the resolved absolute starting sector of the region that would be overwritten.
    /// <para>获取即将被覆盖区域的绝对起始扇区。</para>
    /// </summary>
    /// <value>The resolved start, or <see langword="null"/> when the region could not be resolved.</value>
    public long? StartSector { get; init; }

    /// <summary>
    /// Gets the number of sectors that would be overwritten.
    /// <para>获取即将被覆盖的扇区数。</para>
    /// </summary>
    /// <value>The resolved count, or <see langword="null"/> when the region could not be resolved.</value>
    public long? SectorCount { get; init; }

    /// <summary>
    /// Gets the number of storage bytes that would be overwritten.
    /// <para>获取即将被覆盖的存储字节数。</para>
    /// </summary>
    /// <value>The checked byte total, or <see langword="null"/> when the region could not be resolved.</value>
    public long? BytesToOverwrite { get; init; }

    /// <summary>
    /// Gets the exact command document the bundle would send.
    /// <para>获取本包即将发送的精确命令文档。</para>
    /// </summary>
    /// <value>The program-with-zeros document, or <see langword="null"/> for a refused request.</value>
    public string? CommandXml { get; init; }

    /// <summary>
    /// Gets the reason the request was refused.
    /// <para>获取本请求被拒绝的理由。</para>
    /// </summary>
    /// <value>The refusal text, or <see langword="null"/> when the request passed every gate.</value>
    public string? RefusalReason { get; init; }

    /// <summary>
    /// Gets whether the request passed every gate and therefore has a command document.
    /// <para>获取本请求是否已通过全部门控并因此拥有命令文档。</para>
    /// </summary>
    /// <value><see langword="true"/> when <see cref="CommandXml"/> is present.</value>
    public bool IsAuthorized => CommandXml is not null;
}

/// <summary>
/// A wipe request whose confirmation, geometry and region gates have all been satisfied, together with the
/// command document the builder produced for it. An instance is built in one pass and never mutated, so a
/// refused request cannot leave a partially built command behind for a later attempt to reuse.
/// <para>确认、几何与区域门控均已满足的擦写请求，并附带构建器为其生成的命令文档。实例一次性构造且
/// 永不修改，因此被拒绝的请求不会留下构造到一半的命令供后续尝试复用。</para>
/// </summary>
/// <param name="StartSector">The resolved absolute starting sector.
/// <para>解析后的绝对起始扇区。</para></param>
/// <param name="SectorCount">The number of sectors to overwrite. <para>待覆盖的扇区数。</para></param>
/// <param name="PhysicalPartitionNumber">The physical partition or LUN. <para>物理分区或 LUN。</para></param>
/// <param name="SectorSize">The sector size the region was resolved with.
/// <para>解析本区域所用的扇区大小。</para></param>
/// <param name="Slot">The slot binding, when the request carries one.
/// <para>请求携带槽位时的槽位绑定。</para></param>
/// <param name="ZeroFilename">The zeros payload filename identifier.
/// <para>零字节负载文件名标识。</para></param>
/// <param name="ByteCount">The checked number of storage bytes overwritten.
/// <para>经校验的覆盖存储字节数。</para></param>
/// <param name="CommandXml">The program-with-zeros document for the region.
/// <para>针对本区域的写零编程命令文档。</para></param>
internal sealed record FlashBundleWipeCommand(long StartSector, long SectorCount, int PhysicalPartitionNumber,
    long SectorSize, int? Slot, string ZeroFilename, long ByteCount, string CommandXml)
{
    /// <summary>
    /// The only phrase that authorises a wipe, compared with ordinal case sensitivity.
    /// <para>唯一可授权擦写的短语，按区分大小写的序数方式比较。</para>
    /// </summary>
    /// <value>The literal <c>WIPE</c>.</value>
    internal const string RequiredPhrase = WipeConfirmation.RequiredPhrase;

    /// <summary>
    /// The refusal recorded when no confirmation, or a different phrase, was supplied.
    /// <para>未提供确认或所提供短语不同时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text ending in the no-write guarantee.</value>
    internal const string MissingConfirmationReason =
        "A wipe requires the caller-supplied WIPE confirmation; nothing was written.";

    /// <summary>
    /// The refusal recorded for an entire-storage wipe without the second explicit flag.
    /// <para>整段存储擦写未附带第二项显式开关时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming <c>AllowEntireStorageWipe</c>.</value>
    internal const string AmbiguousEntireStorageReason =
        "An entire-storage wipe also requires AllowEntireStorageWipe; nothing was written.";

    /// <summary>
    /// The refusal recorded when the target never reported geometry for the partition.
    /// <para>目标从未为该分区上报几何信息时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming the missing geometry.</value>
    internal const string MissingGeometryReason =
        "The wipe target cannot be resolved without reported storage geometry; nothing was written.";

    /// <summary>
    /// The refusal recorded when the requested region does not resolve against the reported geometry.
    /// <para>所请求区域无法依据上报几何解析时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming the unresolvable region.</value>
    internal const string UnresolvableRegionReason =
        "The wipe target region is outside the reported storage geometry; nothing was written.";

    /// <summary>
    /// The refusal recorded when the manifest sector size contradicts the reported one.
    /// <para>清单扇区大小与上报扇区大小矛盾时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming the sector-size contradiction.</value>
    internal const string SectorSizeConflictReason =
        "The wipe element declares a sector size the target does not report; nothing was written.";

    /// <summary>
    /// The refusal recorded when range validation rejected the command document.
    /// <para>范围校验拒绝该命令文档时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming the rejected range.</value>
    internal const string CommandBuildReason =
        "The wipe command was rejected by range validation; nothing was written.";

    /// <summary>
    /// Turns a parsed wipe request into a gated command, reporting a refusal instead of throwing. Every gate
    /// runs before <see cref="FirehoseXmlBuilder.BuildWipe"/> is reached, so a refusal produces no command
    /// document and the caller performs no device write.
    /// <para>把已解析的擦写请求转为受门控的命令，并报告拒绝而不抛出异常。每道门控都在到达
    /// <see cref="FirehoseXmlBuilder.BuildWipe"/> 之前执行，因此拒绝不会产生命令文档，调用方也不会
    /// 写入设备。</para>
    /// </summary>
    /// <param name="entry">The parsed wipe request. <para>已解析的擦写请求。</para></param>
    /// <param name="options">The bundle policy snapshot supplying the confirmation gates.
    /// <para>提供确认门控的本包策略快照。</para></param>
    /// <param name="geometry">The geometry the target reported for this partition, or <see langword="null"/>
    /// when none was ever reported. Its sector size is the one the region resolves against.
    /// <para>目标为该分区上报的几何信息；从未上报时为 <see langword="null"/>。其扇区大小就是解析本区域
    /// 所依据的扇区大小。</para></param>
    /// <param name="command">The gated command when every gate passed, otherwise <see langword="null"/>.
    /// <para>全部门控通过时给出受门控的命令，否则为 <see langword="null"/>。</para></param>
    /// <param name="refusal">The refusal reason when a gate failed, otherwise <see langword="null"/>.
    /// <para>门控失败时给出拒绝理由，否则为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the command was built.
    /// <para>命令构造成功时返回 <see langword="true"/>。</para></returns>
    internal static bool TryPrepare(WipeManifestEntry entry, FlashBundleOptions options,
        FirehoseStorageGeometry? geometry, out FlashBundleWipeCommand? command, out string? refusal)
    {
        command = null;
        refusal = null;
        if (options.WipeConfirmation is not { } confirmation
            || !string.Equals(confirmation.Phrase, RequiredPhrase, StringComparison.Ordinal))
        {
            refusal = MissingConfirmationReason;
            return false;
        }

        if (entry.EntireStorage && !options.AllowEntireStorageWipe)
        {
            refusal = AmbiguousEntireStorageReason;
            return false;
        }

        if (geometry is null)
        {
            refusal = MissingGeometryReason;
            return false;
        }

        if (entry.SectorSizeInBytes != 0 && entry.SectorSizeInBytes != geometry.SectorSize)
        {
            refusal = SectorSizeConflictReason;
            return false;
        }

        if (!TryResolveRegion(entry, geometry, out var start, out var count, out var rangeRefusal))
        {
            refusal = rangeRefusal;
            return false;
        }

        try
        {
            var bytes = FirehoseXmlBuilder.ValidateProgramRange(geometry.SectorSize, count,
                entry.PhysicalPartitionNumber, start, entry.Slot);
            command = new FlashBundleWipeCommand(start, count, entry.PhysicalPartitionNumber,
                geometry.SectorSize, entry.Slot, entry.ZeroFilename, bytes,
                FirehoseXmlBuilder.BuildWipe(geometry.SectorSize, count, entry.PhysicalPartitionNumber, start,
                    entry.ZeroFilename, entry.Slot));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            refusal = CommandBuildReason;
            return false;
        }
        catch (OverflowException)
        {
            refusal = CommandBuildReason;
            return false;
        }
    }

    /// <summary>
    /// Builds the dry-run or pre-flight report for one wipe request through the same gate pass as
    /// <see cref="TryPrepare"/>, so a preview and the command it previews cannot disagree.
    /// <para>经由与 <see cref="TryPrepare"/> 相同的门控判定，为单条擦写请求构造 dry-run 或预检报告，
    /// 使预览与其所预览的命令不可能分歧。</para>
    /// </summary>
    /// <param name="entry">The parsed wipe request. <para>已解析的擦写请求。</para></param>
    /// <param name="options">The bundle policy snapshot supplying the confirmation gates.
    /// <para>提供确认门控的本包策略快照。</para></param>
    /// <param name="geometry">The reported geometry for this partition, or <see langword="null"/> offline.
    /// Its sector size is the one the region resolves against.
    /// <para>该分区的上报几何信息；离线时为 <see langword="null"/>。其扇区大小就是解析本区域所依据的
    /// 扇区大小。</para></param>
    /// <returns>The preview naming the region that would be wiped, or the refusal.
    /// <para>指明将被擦除区域的预览，或拒绝结果。</para></returns>
    internal static FlashWipePreview Preview(WipeManifestEntry entry, FlashBundleOptions options,
        FirehoseStorageGeometry? geometry)
    {
        TryPrepare(entry, options, geometry, out var command, out var refusal);
        return new FlashWipePreview
        {
            Name = string.IsNullOrEmpty(entry.Label) ? entry.ZeroFilename : entry.Label,
            PhysicalPartitionNumber = entry.PhysicalPartitionNumber,
            StartSector = command?.StartSector,
            SectorCount = command?.SectorCount,
            BytesToOverwrite = command?.ByteCount,
            CommandXml = command?.CommandXml,
            RefusalReason = refusal
        };
    }

    private static bool TryResolveRegion(WipeManifestEntry entry, FirehoseStorageGeometry geometry,
        out long start, out long count, out string? refusal)
    {
        start = 0;
        count = 0;
        refusal = null;
        if (entry.EntireStorage)
        {
            count = geometry.SectorCount;
            return true;
        }

        try
        {
            if (entry.ToPlanRange(geometry.SectorSize) is not { } range)
            {
                refusal = UnresolvableRegionReason;
                return false;
            }

            (start, count) = range.Resolve(geometry);
            return true;
        }
        catch (ArgumentException)
        {
            refusal = UnresolvableRegionReason;
            return false;
        }
        catch (InvalidDataException)
        {
            refusal = SectorSizeConflictReason;
            return false;
        }
        catch (OverflowException)
        {
            refusal = UnresolvableRegionReason;
            return false;
        }
    }
}