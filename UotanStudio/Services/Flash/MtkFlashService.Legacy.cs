using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Services.Flash;

/// <summary>A previewed, one-shot MediaTek download-agent plan awaiting explicit confirmation before execution.
/// <para>已预览的一次性联发科下载代理计划，需显式确认后方可执行。</para></summary>
public interface IMtkFlashPlanPreview : IDisposable
{
    /// <summary>Gets the number of executable extents in the plan.
    /// <para>获取计划中可执行范围的数量。</para></summary>
    /// <value>The extent count after preparation. <para>准备完成后的范围数量。</para></value>
    int OperationCount { get; }

    /// <summary>Gets the planned write and erase bytes, excluding readback.
    /// <para>获取计划的写入与擦除字节数，不含回读。</para></summary>
    /// <value>The checked total. <para>防溢出累加的总量。</para></value>
    long TotalBytes { get; }

    /// <summary>Gets the plan's one-shot lifecycle state.
    /// <para>获取计划的单次生命周期状态。</para></summary>
    /// <value>The current state. <para>当前状态。</para></value>
    MtkFlashPlanState State { get; }

    /// <summary>Executes the plan only after explicit confirmation.
    /// <para>仅在显式确认后执行计划。</para></summary>
    /// <param name="progress">Optional phase-local progress reporter.
    /// <para>可选的阶段内进度报告器。</para></param>
    /// <param name="eraseTimeoutMs">The deadline for each erase range.
    /// <para>每个擦除范围的截止时间。</para></param>
    /// <param name="cancellationToken">The execution cancellation token.
    /// <para>执行取消令牌。</para></param>
    /// <returns>Completion totals. <para>完成总量。</para></returns>
    Task<MtkFlashPlanResult> ExecuteAsync(IProgress<MtkFlashPlanProgress>? progress = null,
        int eraseTimeoutMs = 120000, CancellationToken cancellationToken = default);
}

/// <summary>Operations for the MediaTek Legacy download agent family.
/// <para>面向联发科 Legacy 下载代理家族的操作。</para></summary>
public static partial class MtkFlashService
{
    /// <summary>Boots a Legacy agent on a leased session and previews GPT-named operations.
    /// <para>在已租用的会话上启动 Legacy 代理并预览按 GPT 名寻址的操作。</para></summary>
    /// <param name="session">A leased session in the created state.
    /// <para>处于已创建状态的已租用会话。</para></param>
    /// <param name="startup">Parsed loader profile, storage family, identity policy and companion resources.
    /// <para>已解析的加载器配置、存储族、身份策略与伴随资源。</para></param>
    /// <param name="physicalPartition">Explicit discovered Legacy region holding the GPT, 1 through 8.
    /// <para>显式指定的、持有 GPT 的 Legacy 区域，取值 1 至 8。</para></param>
    /// <param name="steps">Explicit named operations; copied before asynchronous startup.
    /// <para>显式命名的操作；在异步启动前完成复制。</para></param>
    /// <param name="cancellationToken">Cancellation for startup, metadata and source preflight.
    /// <para>用于启动、元数据与源预检的取消令牌。</para></param>
    /// <returns>A preview owning the session and every source handle until disposed.
    /// <para>持有会话与所有源句柄直至释放的预览对象。</para></returns>
    public static Task<MtkLegacyFlashPreparation> PrepareLegacyOnSessionAsync(MtkSession session,
        MtkAutoStartup startup, byte physicalPartition, IReadOnlyList<MtkLegacyPartitionFlashStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(startup);
        return PrepareLegacyAsync(
            token => MtkBootstrap.MatchAsync(session, MtkDaProtocol.Legacy, startup, token),
            physicalPartition, steps, cancellationToken);
    }

    /// <summary>Boots a Legacy agent over an explicit serial port and previews GPT-named operations.
    /// <para>经显式串口启动 Legacy 代理并预览按 GPT 名寻址的操作。</para></summary>
    /// <param name="serial">Explicit COM/TTY and line settings.
    /// <para>显式的 COM/TTY 与线路设置。</para></param>
    /// <param name="startup">Identity-matched loader selection and companion resources.
    /// <para>按身份匹配的加载器选择与伴随资源。</para></param>
    /// <param name="physicalPartition">Explicit discovered Legacy region holding the GPT, 1 through 8.
    /// <para>显式指定的、持有 GPT 的 Legacy 区域，取值 1 至 8。</para></param>
    /// <param name="steps">Explicit named operations; copied before asynchronous startup.
    /// <para>显式命名的操作；在异步启动前完成复制。</para></param>
    /// <param name="cancellationToken">Cancellation for startup, metadata and source preflight.
    /// <para>用于启动、元数据与源预检的取消令牌。</para></param>
    /// <returns>A preview owning the session and every source handle until disposed.
    /// <para>持有会话与所有源句柄直至释放的预览对象。</para></returns>
    public static Task<MtkLegacyFlashPreparation> PrepareLegacySerialAsync(SerialConnectionOptions serial,
        MtkAutoStartup startup, byte physicalPartition, IReadOnlyList<MtkLegacyPartitionFlashStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(startup);
        return PrepareLegacyAsync(
            token => MtkBootstrap.MatchSerialAsync(serial, MtkDaProtocol.Legacy, startup, token),
            physicalPartition, steps, cancellationToken);
    }

    /// <summary>Previews GPT-named Legacy operations through a supplied session connector.
    /// <para>经传入的会话连接器预览按 GPT 名寻址的 Legacy 操作。</para></summary>
    /// <param name="connect">Connector returning a storage-ready Legacy session; ownership transfers to the
    /// returned preview. <para>返回存储就绪 Legacy 会话的连接器；所有权转移给返回的预览对象。</para></param>
    /// <param name="physicalPartition">Explicit discovered Legacy region holding the GPT, 1 through 8.
    /// <para>显式指定的、持有 GPT 的 Legacy 区域，取值 1 至 8。</para></param>
    /// <param name="steps">Explicit named operations; copied before asynchronous startup.
    /// <para>显式命名的操作；在异步启动前完成复制。</para></param>
    /// <param name="cancellationToken">Cancellation for startup, metadata and source preflight.
    /// <para>用于启动、元数据与源预检的取消令牌。</para></param>
    /// <returns>A preview owning the session and every source handle until disposed.
    /// <para>持有会话与所有源句柄直至释放的预览对象。</para></returns>
    public static Task<MtkLegacyFlashPreparation> PrepareLegacyAsync(
        Func<CancellationToken, Task<MtkSession>> connect, byte physicalPartition,
        IReadOnlyList<MtkLegacyPartitionFlashStep> steps, CancellationToken cancellationToken = default) =>
        PrepareLegacyCoreAsync(connect, physicalPartition, steps, cancellationToken);

    private static async Task<MtkLegacyFlashPreparation> PrepareLegacyCoreAsync(
        Func<CancellationToken, Task<MtkSession>> connect, byte physicalPartition,
        IReadOnlyList<MtkLegacyPartitionFlashStep> steps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count is 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(steps));
        }

        var selected = steps.ToArray();
        if (selected.Any(step => step is null))
        {
            throw new ArgumentException("A selected Legacy flash step is null.", nameof(steps));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var session = await connect(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The connector returned no MTK session.");
        MtkLegacyFlashPlan? plan = null;
        try
        {
            var snapshot = await session.ReadLegacyGptAsync(physicalPartition, cancellationToken)
                .ConfigureAwait(false);
            plan = MtkLegacyFlashPlan.Prepare(snapshot, selected, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new MtkLegacyFlashPreparation(session, plan, snapshot);
        }
        catch
        {
            plan?.Dispose();
            session.Dispose();
            throw;
        }
    }
}

/// <summary>Owns a previewed Legacy plan and its live session.
/// <para>持有已预览的 Legacy 计划与其活动会话。</para></summary>
public sealed class MtkLegacyFlashPreparation : IMtkFlashPlanPreview
{
    private readonly MtkSession _session;
    private readonly MtkLegacyFlashPlan _plan;

    internal MtkLegacyFlashPreparation(MtkSession session, MtkLegacyFlashPlan plan, MtkLegacyGptSnapshot snapshot)
    {
        _session = session;
        _plan = plan;
        Snapshot = snapshot;
    }

    /// <summary>Gets the GPT observation used to resolve names; execution revalidates it.
    /// <para>获取用于解析分区名的 GPT 观测结果；执行时会重新校验。</para></summary>
    /// <value>The validated snapshot. <para>已校验的快照。</para></value>
    public MtkLegacyGptSnapshot Snapshot { get; }

    /// <summary>Gets the read-only physical operations to preview.
    /// <para>获取待预览的只读物理操作。</para></summary>
    /// <value>One entry per extent. <para>每个范围对应一项。</para></value>
    public IReadOnlyList<MtkLegacyFlashOperation> Operations => _plan.Operations;

    /// <summary>Gets the number of executable extents in the plan.
    /// <para>获取计划中可执行范围的数量。</para></summary>
    /// <value>The extent count after preparation. <para>准备完成后的范围数量。</para></value>
    public int OperationCount => _plan.Operations.Count;

    /// <summary>Gets the planned write and erase bytes, excluding readback.
    /// <para>获取计划的写入与擦除字节数，不含回读。</para></summary>
    /// <value>The checked total. <para>防溢出累加的总量。</para></value>
    public long TotalBytes => _plan.TotalBytes;

    /// <summary>Gets the plan's one-shot lifecycle state.
    /// <para>获取计划的单次生命周期状态。</para></summary>
    /// <value>The current state. <para>当前状态。</para></value>
    public MtkFlashPlanState State => _plan.State;

    /// <summary>Executes the plan only after explicit confirmation.
    /// <para>仅在显式确认后执行计划。</para></summary>
    /// <param name="progress">Optional phase-local progress reporter.
    /// <para>可选的阶段内进度报告器。</para></param>
    /// <param name="eraseTimeoutMs">The deadline for each erase range.
    /// <para>每个擦除范围的截止时间。</para></param>
    /// <param name="cancellationToken">The execution cancellation token.
    /// <para>执行取消令牌。</para></param>
    /// <returns>Completion totals. <para>完成总量。</para></returns>
    public Task<MtkFlashPlanResult> ExecuteAsync(IProgress<MtkFlashPlanProgress>? progress = null,
        int eraseTimeoutMs = 120000, CancellationToken cancellationToken = default) =>
        _session.ExecuteLegacyPlanAsync(_plan, progress, eraseTimeoutMs, cancellationToken);

    /// <summary>Releases the plan and the session.
    /// <para>释放计划与会话。</para></summary>
    public void Dispose()
    {
        _plan.Dispose();
        _session.Dispose();
    }
}