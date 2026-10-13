using UotanStudio.Comm.Protocols.Mtk;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Re-programs the primary and backup metadata extents of the GPT the device itself just reported,
/// previewing the whole plan before any byte is written.
/// <para>把设备自身刚刚上报的分区表写回主、备两份元数据范围，并在写入任何字节前预览完整计划。</para>
/// </summary>
internal static class MtkGptRepairService
{
    /// <summary>
    /// Reads the family's partition table on an already ready session and previews the primary and backup extents
    /// that a write-back would program, without writing any byte.
    /// <para>在已就绪的会话上读取该家族的分区表，并预览将要写回的主、备范围；不写入任何字节。</para>
    /// </summary>
    /// <param name="session">A ready session whose geometry the named family already discovered. <para>已由该家族发现几何信息的就绪会话。</para></param>
    /// <param name="family">The explicitly selected agent family. <para>显式选定的代理家族，不回退到其他家族。</para></param>
    /// <param name="kind">The storage family the session reported, used to resolve the region holding the table. <para>该会话上报的存储家族，用于解析持有分区表的区域。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A write-back preview ready to confirm and execute. <para>可确认并执行的写回预览。</para></returns>
    internal static async Task<MtkGptWritePreparation> PrepareAsync(MtkSession session, MtkDaProtocol family,
        MtkXFlashStorageKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        if (family == MtkDaProtocol.XFlash)
        {
            var snapshot = await session.ReadXFlashGptAsync(MtkScatterPackage.GetPhysicalPartition(kind), ct)
                .ConfigureAwait(false);
            return new MtkGptWritePreparation(session, family, snapshot, MtkGptWritePlanner.Plan(snapshot),
                snapshot.Table.MetadataSha256);
        }

        if (family == MtkDaProtocol.Legacy)
        {
            var snapshot = await session.ReadLegacyGptAsync(MtkScatterPackage.GetLegacyPhysicalPartition(kind), ct)
                .ConfigureAwait(false);
            return new MtkGptWritePreparation(session, family, snapshot, MtkGptWritePlanner.Plan(snapshot),
                snapshot.Table.MetadataSha256);
        }

        if (family == MtkDaProtocol.Xml)
        {
            var snapshot = await session.ReadXmlGptAsync(MtkScatterPackage.GetXmlRegionName(kind),
                cancellationToken: ct).ConfigureAwait(false);
            return new MtkGptWritePreparation(session, family, snapshot, MtkGptWritePlanner.Plan(snapshot),
                snapshot.Table.MetadataSha256);
        }

        throw new ArgumentOutOfRangeException(nameof(family),
            "The partition-table write-back supports the XFlash, XML and Legacy download agent families.");
    }
}

/// <summary>
/// A prepared partition-table write-back: the extents it can preview, a one-shot lifecycle, and
/// execution once the preview is confirmed.
/// <para>一次分区表写回的准备结果：可预览范围、一次性生命周期，以及确认后的执行。</para>
/// </summary>
public sealed class MtkGptWritePreparation : IMtkFlashPlanPreview
{
    private readonly MtkSession _session;
    private readonly MtkDaProtocol _family;
    private readonly object _snapshot;
    private readonly Lock _gate = new();
    private MtkFlashPlanState _state = MtkFlashPlanState.Prepared;
    private bool _disposed;

    internal MtkGptWritePreparation(MtkSession session, MtkDaProtocol family, object snapshot,
        IReadOnlyList<MtkGptWriteExtent> extents, string tableMetadataSha256)
    {
        _session = session;
        _family = family;
        _snapshot = snapshot;
        Extents = extents;
        TotalBytes = extents.Aggregate(0L, (total, extent) => checked(total + extent.Length));
        TableMetadataSha256 = tableMetadataSha256;
    }

    /// <summary>Gets the extents the write-back would program, primary first.
    /// <para>获取写回将要编程的范围，主范围在前。</para></summary>
    /// <value>The planned extents in programming order. <para>按编程顺序排列的计划范围。</para></value>
    public IReadOnlyList<MtkGptWriteExtent> Extents { get; }

    /// <summary>Gets the number of extents the write-back programs.
    /// <para>获取写回所编程的范围数量。</para></summary>
    /// <value>The count of planned operations. <para>计划操作的数量。</para></value>
    public int OperationCount => Extents.Count;

    /// <summary>Gets the total byte count across the planned extents.
    /// <para>获取计划范围跨越的字节总数。</para></summary>
    /// <value>The sum of the planned extent lengths. <para>各计划范围长度之和。</para></value>
    public long TotalBytes { get; }

    /// <summary>Gets the lifecycle state of this one-shot preview.
    /// <para>获取本次一次性预览的生命周期状态。</para></summary>
    /// <value>The current state of the preview lifecycle. <para>预览生命周期当前状态。</para></value>
    public MtkFlashPlanState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>Gets the fingerprint of the table that would be re-programmed.
    /// <para>获取将被重新编程的分区表摘要。</para></summary>
    /// <value>The metadata digest of the observed table. <para>所观测分区表的元数据摘要。</para></value>
    public string TableMetadataSha256 { get; }

    /// <summary>Gets the equivalence evidence recorded by the last successful execution.
    /// <para>获取最近一次成功执行记录的等价性证据。</para></summary>
    /// <value>The evidence of the last run, or null before one succeeds. <para>最近一次执行的证据，成功之前为 null。</para></value>
    public MtkGptWriteResult? Evidence { get; private set; }

    /// <summary>
    /// Executes the write-back once the preview is confirmed, performing no erase, no retry and no
    /// rollback.
    /// <para>预览确认后执行写回，不进行擦除、不重试也不回滚。</para>
    /// </summary>
    /// <param name="progress">Optional phase-local progress. <para>可选的阶段内进度。</para></param>
    /// <param name="eraseTimeoutMs">Retained for the shared preview surface, which erases nothing here. <para>为不擦除的共用预览接口保留。</para></param>
    /// <param name="cancellationToken">Execution cancellation token. <para>执行取消令牌。</para></param>
    /// <returns>Completion totals and the verified readback bytes. <para>完成总量与回读校验字节数。</para></returns>
    public async Task<MtkFlashPlanResult> ExecuteAsync(IProgress<MtkFlashPlanProgress>? progress = null,
        int eraseTimeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != MtkFlashPlanState.Prepared)
            {
                throw new InvalidOperationException(
                    "A partition-table write-back can be executed only once.");
            }

            _state = MtkFlashPlanState.Running;
        }

        var succeeded = false;
        try
        {
            var result = _family switch
            {
                MtkDaProtocol.XFlash => await _session
                    .WriteXFlashGptAsync((MtkGptSnapshot)_snapshot, progress, cancellationToken)
                    .ConfigureAwait(false),
                MtkDaProtocol.Legacy => await _session
                    .WriteLegacyGptAsync((MtkLegacyGptSnapshot)_snapshot, progress, cancellationToken)
                    .ConfigureAwait(false),
                MtkDaProtocol.Xml => await _session
                    .WriteXmlGptAsync((MtkXmlGptSnapshot)_snapshot, progress, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(_family),
                    "The partition-table write-back supports the XFlash, XML and Legacy download agent families.")
            };

            Evidence = result;
            succeeded = true;
            return new MtkFlashPlanResult(result.ExtentsProgrammed, result.BytesProgrammed, 0,
                result.BytesCompared);
        }
        finally
        {
            lock (_gate)
            {
                _state = succeeded ? MtkFlashPlanState.Completed : MtkFlashPlanState.Failed;
            }
        }
    }

    /// <summary>
    /// Ends the preview's one-shot lifecycle; never disposes the borrowed session.
    /// <para>结束该预览的一次性生命周期；不会释放借用来的会话。</para>
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_state == MtkFlashPlanState.Running)
            {
                throw new InvalidOperationException("An executing write-back cannot be disposed.");
            }

            _disposed = true;
            _state = MtkFlashPlanState.Disposed;
        }
    }
}