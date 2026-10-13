using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const int GptWriteTimeoutMs = 120000;

    /// <summary>
    /// Programs the device's own primary and backup GPT copies back from one validated XFlash snapshot.
    /// <para>依据一份已校验的 XFlash 快照，把设备自身的主、备 GPT 副本写回。</para>
    /// </summary>
    /// <remarks>
    /// <para>The table travels on the storage-write verb the family already speaks: <c>WRITE_DATA</c>
    /// followed by its 56-byte parameter block, which packs the storage family, the physical partition,
    /// the byte offset and the byte length as one little-endian group. That is the identical exchange an
    /// ordinary partition image travels on, so a GPT program carries no frame the flash path does not
    /// already define.</para>
    /// <para>分区表走的是该家族已有的存储写入命令：<c>WRITE_DATA</c> 及其 56 字节参数块，以一个小端序组
    /// 打包存储家族、物理分区、字节偏移与字节长度。这与普通分区镜像使用的交换完全相同，因此写回分区表
    /// 不会引入刷写路径之外的帧。</para>
    /// <para>Scope limit by design: only the table this session just read is re-programmed. No foreign GPT is
    /// accepted, no geometry is rewritten, no partition payload is touched and no resize is attempted. The live
    /// table is re-read under the same operation lease before the first byte is submitted, exactly like the plan
    /// executors, and every programmed extent is read back and compared afterwards, followed by a fresh table
    /// parse; a mismatch throws and the remaining extents are not retried or rolled back.</para>
    /// <para>刻意的范围限制：只重新编程本会话刚刚读到的分区表。不接受外部 GPT、不改写几何信息、不触碰任何分区
    /// 数据，也不尝试调整容量。与计划执行器完全一致，在提交首个字节之前会在同一操作租约下重新读取实时表；每个
    /// 已编程范围随后都会回读比对，并再重新解析一次分区表。比对不符即抛出，剩余范围不重试也不回滚。</para>
    /// </remarks>
    /// <param name="snapshot">A validated XFlash GPT observation from this session's current geometry.
    /// <para>来自本会话当前几何信息、已校验的 XFlash GPT 观测结果。</para></param>
    /// <param name="progress">Optional phase-local progress per programmed extent.
    /// <para>可选的、每个已编程范围的阶段内进度。</para></param>
    /// <param name="cancellationToken">Cancellation honoured before the first command and between extents.
    /// <para>在首个命令之前以及各范围之间都会响应的取消令牌。</para></param>
    /// <returns>Programmed totals and the separately observed byte, structure and capacity equivalences.
    /// <para>已编程总量，以及分别观察到的字节、结构与容量等价性。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.
    /// <para><paramref name="snapshot"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">Storage is not ready, or the snapshot belongs to a different
    /// storage observation. <para>存储未就绪，或该快照属于另一份存储观测。</para></exception>
    /// <exception cref="InvalidDataException">The live table drifted before the write, an extent did not read
    /// back identically, or the re-parsed table differs from what was programmed.
    /// <para>实时表在写入前发生变化、某个范围回读不一致，或重新解析的分区表与所编程内容不同。</para></exception>
    /// <exception cref="NotSupportedException">The snapshot addresses NAND or an authenticated region.
    /// <para>该快照指向 NAND 或需认证的区域。</para></exception>
    /// <exception cref="MtkProtocolException">A write or read-back exchange failed; the session faults.
    /// <para>某次写入或回读交换失败；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    public async Task<MtkGptWriteResult> WriteXFlashGptAsync(MtkGptSnapshot snapshot,
        IProgress<MtkFlashPlanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStorageReady);
            if (!ReferenceEquals(snapshot.Storage, XFlashStorage))
            {
                throw new InvalidOperationException(
                    "The GPT snapshot belongs to a different storage observation.");
            }

            await RevalidateXFlashGptOwnedAsync(snapshot, cancellationToken).ConfigureAwait(false);
            var extents = MtkGptWritePlanner.Plan(snapshot);
            var storage = XFlashStorage!;
            foreach (var extent in extents)
            {
                storage.ValidateRange(extent.PhysicalIdentifier ?? throw new InvalidOperationException(
                    "The XFlash GPT extent carries no physical region identifier."), extent.Offset, extent.Length);
            }

            var (programmed, compared) = await ProgramGptExtentsAsync(extents, progress, cancellationToken,
                (extent, source, reporter, token) => WriteXFlashStorageOwnedAsync(
                    extent.PhysicalIdentifier!.Value, extent.Offset, extent.Length, source, reporter, token),
                (extent, output, token) => ReadXFlashStorageOwnedAsync(
                    extent.PhysicalIdentifier!.Value, extent.Offset, extent.Length, output, null, token))
                .ConfigureAwait(false);
            var current = await ReadXFlashGptOwnedAsync(snapshot.PartitionId, cancellationToken)
                .ConfigureAwait(false);
            return SummarizeGptWrite(extents, programmed, compared, snapshot.Table, current.Table,
                storage.Regions.Single(region => region.PartitionId == snapshot.PartitionId).CapacityBytes);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Programs the device's own primary and backup GPT copies from one validated Legacy snapshot using the <c>sdmmc_write_data</c> exchange; the partition identity travels inside the command header, so a write needs no preceding region switch.
    /// <para>依据一份已校验的 Legacy 快照写回设备自身的主、备 GPT 副本，使用 <c>sdmmc_write_data</c> 交互；分区标识随命令头携带，因此写入前无需切换区域。</para>
    /// </summary>
    /// <remarks>
    /// <para>Same scope limit as the XFlash path: only the table this session just read is re-programmed, with no
    /// foreign GPT, no geometry rewrite, no partition payload and no resize. The live table is re-read under this
    /// lease before the first byte, every extent is compared after the write and a fresh parse follows.</para>
    /// <para>与 XFlash 路径相同的范围限制：只重新编程本会话刚刚读到的分区表，不接受外部 GPT、不改写几何信息、
    /// 不触碰分区数据、不调整容量。在提交首个字节之前于本租约下重新读取实时表，写入后逐范围比对并重新解析。</para>
    /// </remarks>
    /// <param name="snapshot">A validated Legacy GPT observation from this session's current inventory.
    /// <para>来自本会话当前清单、已校验的 Legacy GPT 观测结果。</para></param>
    /// <param name="progress">Optional phase-local progress per programmed extent.
    /// <para>可选的、每个已编程范围的阶段内进度。</para></param>
    /// <param name="cancellationToken">Cancellation honoured before the first command and between extents.
    /// <para>在首个命令之前以及各范围之间都会响应的取消令牌。</para></param>
    /// <returns>Programmed totals and the separately observed byte, structure and capacity equivalences.
    /// <para>已编程总量，以及分别观察到的字节、结构与容量等价性。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.
    /// <para><paramref name="snapshot"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready, or the snapshot belongs to a
    /// different storage inventory. <para>Legacy 存储未就绪，或该快照属于另一份存储清单。</para></exception>
    /// <exception cref="InvalidDataException">The live table drifted before the write, an extent did not read
    /// back identically, or the re-parsed table differs from what was programmed.
    /// <para>实时表在写入前发生变化、某个范围回读不一致，或重新解析的分区表与所编程内容不同。</para></exception>
    /// <exception cref="NotSupportedException">The Legacy agent reports storage without 512-byte GPT geometry.
    /// <para>Legacy 代理上报的存储不具备 512 字节的 GPT 几何。</para></exception>
    /// <exception cref="MtkProtocolException">A write or read-back exchange failed; the Legacy family faults.
    /// <para>某次写入或回读交换失败；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">An exchange deadline expired; the session cannot be replayed.
    /// <para>某次交换的截止时间到期；会话无法重放。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    public async Task<MtkGptWriteResult> WriteLegacyGptAsync(MtkLegacyGptSnapshot snapshot,
        IProgress<MtkFlashPlanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            if (!ReferenceEquals(snapshot.Storage, LegacyStorage))
            {
                throw new InvalidOperationException(
                    "The Legacy GPT snapshot belongs to a different storage inventory.");
            }

            await RevalidateLegacyGptOwnedAsync(snapshot, cancellationToken).ConfigureAwait(false);
            var extents = MtkGptWritePlanner.Plan(snapshot);
            var storage = LegacyStorage!;
            foreach (var extent in extents)
            {
                storage.ValidateRange(RequireLegacyRegion(extent), extent.Offset, extent.Length);
            }

            var (programmed, compared) = await ProgramGptExtentsAsync(extents, progress, cancellationToken,
                (extent, source, reporter, token) => TransferLegacyStorageOwnedAsync(true,
                    RequireLegacyRegion(extent), extent.Offset, extent.Length, source, GptWriteTimeoutMs, reporter,
                    token, nor: false),
                (extent, output, token) => TransferLegacyStorageOwnedAsync(false, RequireLegacyRegion(extent),
                    extent.Offset, extent.Length, output, GptWriteTimeoutMs, null, token, nor: false))
                .ConfigureAwait(false);
            var current = await ReadLegacyGptOwnedAsync(snapshot.PartitionId, cancellationToken)
                .ConfigureAwait(false);
            return SummarizeGptWrite(extents, programmed, compared, snapshot.Table, current.Table,
                storage.Regions.Single(region => region.PartitionId == snapshot.PartitionId).CapacityBytes);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Programs the device's own primary and backup GPT copies from one validated XML snapshot through <c>CMD:WRITE-FLASH</c>, then reads them back with <c>CMD:READ-FLASH</c> using the same region token, offset and length.
    /// <para>依据一份已校验的 XML 快照，经 <c>CMD:WRITE-FLASH</c> 写回设备自身的主、备 GPT 副本，随后用 <c>CMD:READ-FLASH</c> 以相同区域令牌、偏移与长度回读。</para>
    /// </summary>
    /// <remarks>
    /// <para>Same scope limit as the other families: only the table this session just read is re-programmed, with
    /// no foreign GPT, no geometry rewrite, no partition payload and no resize. The live table is re-read under
    /// this lease before the first byte, every extent is compared after the write and a fresh parse follows.</para>
    /// <para>与其他家族相同的范围限制：只重新编程本会话刚刚读到的分区表，不接受外部 GPT、不改写几何信息、不触碰
    /// 分区数据、不调整容量。在提交首个字节之前于本租约下重新读取实时表，写入后逐范围比对并重新解析。</para>
    /// </remarks>
    /// <param name="snapshot">A validated XML GPT observation from this session's current geometry.
    /// <para>来自本会话当前几何信息、已校验的 XML GPT 观测结果。</para></param>
    /// <param name="progress">Optional phase-local progress per programmed extent.
    /// <para>可选的、每个已编程范围的阶段内进度。</para></param>
    /// <param name="cancellationToken">Cancellation honoured before the first command and between extents.
    /// <para>在首个命令之前以及各范围之间都会响应的取消令牌。</para></param>
    /// <returns>Programmed totals and the separately observed byte, structure and capacity equivalences.
    /// <para>已编程总量，以及分别观察到的字节、结构与容量等价性。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.
    /// <para><paramref name="snapshot"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">XML storage geometry is unknown, no XML channel is attached,
    /// or the snapshot belongs to a different geometry observation.
    /// <para>尚未发现 XML 存储几何信息、未附加 XML 通道，或该快照属于另一份几何观测。</para></exception>
    /// <exception cref="InvalidDataException">The live table drifted before the write, an extent did not read
    /// back identically, or the re-parsed table differs from what was programmed.
    /// <para>实时表在写入前发生变化、某个范围回读不一致，或重新解析的分区表与所编程内容不同。</para></exception>
    /// <exception cref="NotSupportedException">The snapshot names an authenticated region.
    /// <para>该快照命名了需认证的区域。</para></exception>
    /// <exception cref="MtkProtocolException">A write or read-back exchange failed; the XML family faults.
    /// <para>某次写入或回读交换失败；XML 家族进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">An exchange deadline expired; storage may have changed.
    /// <para>某次交换的截止时间到期；存储可能已被改写。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    public async Task<MtkGptWriteResult> WriteXmlGptAsync(MtkXmlGptSnapshot snapshot,
        IProgress<MtkFlashPlanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStorageGeometryKnown);
            if (!ReferenceEquals(snapshot.Storage, XmlStorage))
            {
                throw new InvalidOperationException(
                    "The XML GPT snapshot belongs to a different storage geometry.");
            }

            await RevalidateXmlGptOwnedAsync(snapshot, GptWriteTimeoutMs, cancellationToken).ConfigureAwait(false);
            var extents = MtkGptWritePlanner.Plan(snapshot);
            var storage = XmlStorage!;
            foreach (var extent in extents)
            {
                storage.ValidateRange(extent.RegionName, extent.Offset, extent.Length);
            }

            var (programmed, compared) = await ProgramGptExtentsAsync(extents, progress, cancellationToken,
                (extent, source, reporter, token) => WriteXmlStorageOwnedAsync(extent.RegionName, extent.Offset,
                    extent.Length, source, GptWriteTimeoutMs, reporter, token),
                (extent, output, token) => ReadXmlStorageOwnedAsync(extent.RegionName, extent.Offset, extent.Length,
                    output, GptWriteTimeoutMs, null, token))
                .ConfigureAwait(false);
            var current = await ReadXmlGptOwnedAsync(snapshot.RegionName, GptWriteTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            return SummarizeGptWrite(extents, programmed, compared, snapshot.Table, current.Table,
                storage.Regions.Single(region => region.Name == snapshot.RegionName).CapacityBytes);
        }
        finally
        {
            _operation.Release();
        }
    }

    private static byte RequireLegacyRegion(MtkGptWriteExtent extent)
    {
        var identifier = extent.PhysicalIdentifier ?? throw new InvalidOperationException(
            "The Legacy GPT extent carries no physical region identifier.");
        if (identifier > byte.MaxValue)
        {
            throw new InvalidOperationException(
                "The Legacy GPT extent names a region identifier beyond its one-byte wire field.");
        }

        return checked((byte)identifier);
    }

    private static async Task<(long Programmed, long Compared)> ProgramGptExtentsAsync(
        IReadOnlyList<MtkGptWriteExtent> extents, IProgress<MtkFlashPlanProgress>? progress,
        CancellationToken cancellationToken,
        Func<MtkGptWriteExtent, Stream, IProgress<long>, CancellationToken, Task> write,
        Func<MtkGptWriteExtent, Stream, CancellationToken, Task> read)
    {
        var programmed = 0L;
        for (var index = 0; index < extents.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extent = extents[index];
            var reporter = new GptWriteByteProgress(progress, index, extents.Count, extent, MtkFlashPhase.Writing);
            reporter.Report(0);
            using (var source = new MemoryStream(extent.Content, writable: false))
            {
                await write(extent, source, reporter, cancellationToken).ConfigureAwait(false);
            }

            programmed = checked(programmed + extent.Length);
            progress?.Report(new MtkFlashPlanProgress(index, extents.Count, index,
                MtkFlashPhase.OperationCompleted, extent.Length, extent.Length));
        }

        var compared = 0L;
        for (var index = 0; index < extents.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extent = extents[index];
            var reporter = new GptWriteByteProgress(progress, index, extents.Count, extent, MtkFlashPhase.Verifying);
            reporter.Report(0);
            using var expected = new MemoryStream(extent.Content, writable: false);
            using var output = new StorageComparisonStream(expected);
            await read(extent, output, cancellationToken).ConfigureAwait(false);
            if (output.FirstMismatchOffset is { } mismatch)
            {
                throw new InvalidDataException(
                    $"The {extent.Copy} GPT extent at region byte {extent.Offset} differs at byte " +
                    $"{checked(extent.Offset + mismatch)} after being programmed.");
            }

            if (output.ComparedBytes != extent.Length)
            {
                throw new InvalidDataException(
                    $"The {extent.Copy} GPT extent at region byte {extent.Offset} delivered " +
                    $"{output.ComparedBytes} bytes, short of the {extent.Length} bytes it was programmed with.");
            }

            compared = checked(compared + extent.Length);
        }

        return (programmed, compared);
    }

    private static MtkGptWriteResult SummarizeGptWrite(IReadOnlyList<MtkGptWriteExtent> extents, long programmed,
        long compared, GptTable written, GptTable current, long capacityBytes)
    {
        var structureEquivalent = string.Equals(current.MetadataSha256, written.MetadataSha256,
                StringComparison.Ordinal)
            && current.BlockSize == written.BlockSize && current.BlockCount == written.BlockCount
            && current.PartitionEntryCount == written.PartitionEntryCount;
        if (!structureEquivalent)
        {
            throw new InvalidDataException(
                "The partition table read back after programming does not match the table that was written; " +
                "the extents were not retried and no rollback was attempted.");
        }

        return new MtkGptWriteResult(extents.Count, programmed, compared, programmed == compared, true,
            capacityBytes / written.BlockSize == (long)written.BlockCount, written.MetadataSha256,
            current.MetadataSha256);
    }

    private sealed class GptWriteByteProgress(IProgress<MtkFlashPlanProgress>? progress, int index, int count,
        MtkGptWriteExtent extent, MtkFlashPhase phase) : IProgress<long>
    {
        public void Report(long value) => progress?.Report(new MtkFlashPlanProgress(index, count, index,
            phase, value, extent.Length));
    }
}