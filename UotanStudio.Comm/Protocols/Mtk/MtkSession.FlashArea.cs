using UotanStudio.Comm.Protocols.Mtk.Storage;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Reads a region-agnostic absolute range of XFlash storage, crossing reported region boundaries as needed, reusing the same chunked read exchange the partition and GPT paths use.
    /// <para>读取 XFlash 存储中与区域无关的绝对区间，按需跨越上报的区域边界，复用分区与 GPT 路径同样的分块读交互。</para>
    /// </summary>
    /// <remarks>
    /// <para>The destination may hold partial bytes when a chunk fails; publishers must stage to a temporary
    /// file. Regions the agent reserved for authenticated access are absent from the map, so they are never
    /// read here. Progress counts only bytes the agent already delivered into <paramref name="destination"/>.</para>
    /// <para>某个分块失败时目标可能已包含部分字节；发布方必须写入临时文件。代理为认证访问保留的区域不会出现在
    /// 映射中，因此这里绝不会读取它们。进度只统计代理已经写入 <paramref name="destination"/> 的字节。</para>
    /// </remarks>
    /// <param name="area">An address space built from this session's current XFlash geometry.
    /// <para>基于本会话当前 XFlash 几何信息构建的地址空间。</para></param>
    /// <param name="offset">The nonnegative, transfer-aligned absolute offset.
    /// <para>非负且按传输对齐的绝对偏移。</para></param>
    /// <param name="length">The positive, transfer-aligned absolute byte count.
    /// <para>正数且按传输对齐的绝对字节数。</para></param>
    /// <param name="destination">A writable stream which remains open.
    /// <para>保持打开的可写流。</para></param>
    /// <param name="progress">Optional cumulative bytes delivered across every chunk of the walk.
    /// <para>可选的、整个遍历中已交付字节的累计值。</para></param>
    /// <param name="cancellationToken">Cancellation honoured before the first command and between chunks.
    /// <para>在首个命令之前以及各分块之间都会响应的取消令牌。</para></param>
    /// <returns>A task that completes after every planned byte was written to the destination.
    /// <para>在全部计划字节写入目标之后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="area"/> or <paramref name="destination"/> is null.
    /// <para><paramref name="area"/> 或 <paramref name="destination"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.
    /// <para><paramref name="destination"/> 不可写。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested range is unaligned or leaves the reported
    /// address space. <para>请求的范围未对齐，或超出上报的地址空间。</para></exception>
    /// <exception cref="NotSupportedException"><paramref name="area"/> was not built by the XFlash discovery
    /// path. <para><paramref name="area"/> 不是由 XFlash 发现路径构建的。</para></exception>
    /// <exception cref="InvalidOperationException">Storage is not ready, or the map belongs to a different
    /// storage observation. <para>存储未就绪，或该映射属于另一份存储观测。</para></exception>
    /// <exception cref="MtkProtocolException">A chunk faulted the exchange; the session faults.
    /// <para>某个分块使交换失败；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    public async Task ReadXFlashAreaAsync(MtkFlashAreaMap area, long offset, long length, Stream destination,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(destination);
        RequireWritableAreaDestination(destination);
        var segments = area.Plan(offset, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAreaMapSource(area, MtkStorageDiscoverySource.XFlash);
            RequireState(MtkSessionState.XFlashStorageReady);
            if (!ReferenceEquals(area.XFlashGeometry, XFlashStorage))
            {
                throw new InvalidOperationException(
                    "The XFlash area map was built from a different storage observation.");
            }

            RequireAreaGeometry(area);
            cancellationToken.ThrowIfCancellationRequested();
            var delivered = 0L;
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partitionId = RequireAreaIdentifier(segment, area.Source);
                for (var consumed = 0L; consumed < segment.Length;)
                {
                    var chunk = Math.Min(area.ChunkBytes, segment.Length - consumed);
                    await ReadXFlashStorageOwnedAsync(partitionId, checked(segment.RegionOffset + consumed), chunk,
                        destination, null, cancellationToken).ConfigureAwait(false);
                    consumed = checked(consumed + chunk);
                    delivered = checked(delivered + chunk);
                    progress?.Report(delivered);
                }
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads a region-agnostic absolute range of Legacy eMMC storage across the regions the agent initialized, matching the reference handler's whole-flash and raw-sector reads (Library/DA/mtk_da_handler.py:285-312) on the same chunked exchange the partition paths use.
    /// <para>在与区域无关的绝对范围上读取 Legacy eMMC 存储，跨越代理已初始化的各区域，对应参考处理程序的 整片与原始扇区读取（Library/DA/mtk_da_handler.py:285-312），并复用分区路径使用的同一套分块交换。</para></summary>
    /// <param name="area">An address space built from this session's current Legacy inventory.
    /// <para>基于本会话当前 Legacy 清单构建的地址空间。</para></param>
    /// <param name="offset">The nonnegative, 512-aligned absolute offset.
    /// <para>非负且按 512 对齐的绝对偏移。</para></param>
    /// <param name="length">The positive, 512-aligned absolute byte count.
    /// <para>正数且按 512 对齐的绝对字节数。</para></param>
    /// <param name="destination">A writable stream which remains open.<para>保持打开的可写流。</para></param>
    /// <param name="timeoutMs">The cooperative deadline for each chunk, from 1 ms through 24 hours.
    /// <para>每个分块的协作式截止时间，范围 1 毫秒至 24 小时。</para></param>
    /// <param name="progress">Optional cumulative bytes delivered across every chunk of the walk.
    /// <para>可选的、整个遍历中已交付字节的累计值。</para></param>
    /// <param name="cancellationToken">Cancellation honoured before the first command and between chunks.
    /// <para>在首个命令之前以及各分块之间都会响应的取消令牌。</para></param>
    /// <returns>A task that completes after every planned byte was written to the destination.
    /// <para>在全部计划字节写入目标之后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="area"/> or <paramref name="destination"/> is null.
    /// <para><paramref name="area"/> 或 <paramref name="destination"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.
    /// <para><paramref name="destination"/> 不可写。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is unaligned, exceeds the reported address space, or <paramref name="timeoutMs"/> is out of bounds.
    /// <para>范围未对齐、超出上报的地址空间，或 <paramref name="timeoutMs"/> 越界。</para></exception>
    /// <exception cref="NotSupportedException"><paramref name="area"/> was not built by the Legacy discovery path.
    /// <para><paramref name="area"/> 不是由 Legacy 发现路径构建的。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready, or the map belongs to a different storage inventory.
    /// <para>Legacy 存储未就绪，或该映射属于另一份存储清单。</para></exception>
    /// <exception cref="MtkProtocolException">A chunk faulted the exchange; the Legacy family faults.
    /// <para>某个分块使交换失败；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">A chunk deadline expired; the session cannot be replayed.
    /// <para>某个分块的截止时间到期；会话无法重放。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    public async Task ReadLegacyAreaAsync(MtkFlashAreaMap area, long offset, long length, Stream destination,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(destination);
        RequireWritableAreaDestination(destination);
        ValidateXmlSetupTimeout(timeoutMs);
        var segments = area.Plan(offset, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAreaMapSource(area, MtkStorageDiscoverySource.Legacy);
            RequireState(MtkSessionState.LegacyStorageReady);
            if (!ReferenceEquals(area.LegacyGeometry, LegacyStorage))
            {
                throw new InvalidOperationException(
                    "The Legacy area map was built from a different storage inventory.");
            }

            RequireAreaGeometry(area);
            cancellationToken.ThrowIfCancellationRequested();
            var delivered = 0L;
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partitionId = RequireLegacyAreaIdentifier(segment, area);
                for (var consumed = 0L; consumed < segment.Length;)
                {
                    var chunk = Math.Min(area.ChunkBytes, segment.Length - consumed);
                    await TransferLegacyStorageOwnedAsync(false, partitionId,
                        checked(segment.RegionOffset + consumed), chunk, destination, timeoutMs, null,
                        cancellationToken, nor: false).ConfigureAwait(false);
                    consumed = checked(consumed + chunk);
                    delivered = checked(delivered + chunk);
                    progress?.Report(delivered);
                }
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads a region-agnostic absolute range of XML storage across the named regions the agent reported, matching the reference handler's whole-flash and raw-sector reads (Library/DA/mtk_da_handler.py:285-312) on the same chunked exchange the partition paths use.
    /// <para>在与区域无关的绝对范围上读取 XML 存储，跨越代理上报的各命名区域，对应参考处理程序的整片与原始 扇区读取（Library/DA/mtk_da_handler.py:285-312），并复用分区路径使用的同一套分块交换。</para></summary>
    /// <param name="area">An address space built from this session's current XML geometry.
    /// <para>基于本会话当前 XML 几何信息构建的地址空间。</para></param>
    /// <param name="offset">The nonnegative, block-aligned absolute offset.
    /// <para>非负且按块对齐的绝对偏移。</para></param>
    /// <param name="length">The positive, block-aligned absolute byte count.
    /// <para>正数且按块对齐的绝对字节数。</para></param>
    /// <param name="destination">A writable stream which remains open.<para>保持打开的可写流。</para></param>
    /// <param name="timeoutMs">The cooperative deadline for each chunk, from 1 ms through 24 hours.
    /// <para>每个分块的协作式截止时间，范围 1 毫秒至 24 小时。</para></param>
    /// <param name="progress">Optional cumulative bytes delivered across every chunk of the walk.
    /// <para>可选的、整个遍历中已交付字节的累计值。</para></param>
    /// <param name="cancellationToken">Cancellation honoured before the first command and between chunks.
    /// <para>在首个命令之前以及各分块之间都会响应的取消令牌。</para></param>
    /// <returns>A task that completes after every planned byte was written to the destination.
    /// <para>在全部计划字节写入目标之后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="area"/> or <paramref name="destination"/> is null.
    /// <para><paramref name="area"/> 或 <paramref name="destination"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.
    /// <para><paramref name="destination"/> 不可写。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is unaligned, exceeds the reported address space, or <paramref name="timeoutMs"/> is out of bounds.
    /// <para>范围未对齐、超出上报的地址空间，或 <paramref name="timeoutMs"/> 越界。</para></exception>
    /// <exception cref="NotSupportedException"><paramref name="area"/> was not built by the XML discovery path, or a region it names is reserved for authenticated access.
    /// <para><paramref name="area"/> 不是由 XML 发现路径构建的，或其命名的区域为认证访问保留。</para></exception>
    /// <exception cref="InvalidOperationException">XML storage geometry is unknown, no XML channel is attached, or the map belongs to a different geometry observation.
    /// <para>尚未发现 XML 存储几何信息、未附加 XML 通道，或该映射属于另一份几何观测。</para></exception>
    /// <exception cref="MtkProtocolException">A chunk faulted the exchange; the XML family faults.
    /// <para>某个分块使交换失败；XML 家族进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">A chunk deadline expired; reconnect before continuing.
    /// <para>某个分块的截止时间到期；续操作前必须重连。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    public async Task ReadXmlAreaAsync(MtkFlashAreaMap area, long offset, long length, Stream destination,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(destination);
        RequireWritableAreaDestination(destination);
        ValidateXmlSetupTimeout(timeoutMs);
        var segments = area.Plan(offset, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAreaMapSource(area, MtkStorageDiscoverySource.Xml);
            RequireState(MtkSessionState.XmlStorageGeometryKnown);
            if (!ReferenceEquals(area.XmlGeometry, XmlStorage))
            {
                throw new InvalidOperationException(
                    "The XML area map was built from a different storage geometry.");
            }

            RequireAreaGeometry(area);
            cancellationToken.ThrowIfCancellationRequested();
            var delivered = 0L;
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var consumed = 0L; consumed < segment.Length;)
                {
                    var chunk = Math.Min(area.ChunkBytes, segment.Length - consumed);
                    await ReadXmlStorageOwnedAsync(segment.RegionName, checked(segment.RegionOffset + consumed),
                        chunk, destination, timeoutMs, null, cancellationToken).ConfigureAwait(false);
                    consumed = checked(consumed + chunk);
                    delivered = checked(delivered + chunk);
                    progress?.Report(delivered);
                }
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private static void RequireWritableAreaDestination(Stream destination)
    {
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The area destination is not writable.", nameof(destination));
        }
    }

    private static void RequireAreaMapSource(MtkFlashAreaMap area, MtkStorageDiscoverySource expected)
    {
        if (area.Source != expected)
        {
            throw new NotSupportedException(
                $"The {expected} download agent cannot walk an area map built by the {area.Source} discovery " +
                "path; the reference handler addresses each family with its own reported geometry " +
                "(Library/DA/mtk_da_handler.py:285-312) and no fallback between families is performed.");
        }
    }

    private void RequireAreaGeometry(MtkFlashAreaMap area)
    {
        if (StorageCapability is { } published && !published.GeometryEquals(area.Capability))
        {
            throw new InvalidOperationException(
                "The published storage capability no longer matches the area map's reported geometry; the walk " +
                "was not started.");
        }
    }

    private static uint RequireAreaIdentifier(MtkFlashAreaSegment segment, MtkStorageDiscoverySource source) =>
        segment.PhysicalIdentifier ?? throw new NotSupportedException(
            $"The {source} area segment for region {segment.RegionName} carries no physical identifier, so it " +
            "cannot be addressed by the XFlash storage command.");

    private static byte RequireLegacyAreaIdentifier(MtkFlashAreaSegment segment, MtkFlashAreaMap area)
    {
        var identifier = segment.PhysicalIdentifier ?? throw new NotSupportedException(
            $"The {area.Source} area segment for region {segment.RegionName} carries no physical identifier.");
        if (identifier > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(segment),
                "The Legacy region identifier reported for an area segment exceeds its one-byte wire field.");
        }

        return checked((byte)identifier);
    }
}