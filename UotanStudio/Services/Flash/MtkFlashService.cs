using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Services.Flash;

/// <summary>Host orchestration for explicitly selected serial XFlash targets, separate from USB device discovery.
/// <para>针对显式选择的串口 XFlash 目标进行编排，与 USB 设备发现相互独立。</para></summary>
public static partial class MtkFlashService
{
    /// <summary>Starts the selected agent, reads GPT and prepares a preview without storage writes or erases.
    /// <para>启动所选代理、读取 GPT 并准备预览，不写入或擦除存储。</para></summary>
    /// <param name="serial">Explicit COM/TTY and line settings. <para>显式的 COM/TTY 与线路设置。</para></param>
    /// <param name="startup">Selected loader/stages/EMI and identity policy.
    /// <para>所选加载器、代理阶段、EMI 与身份策略。</para></param>
    /// <param name="physicalPartition">Explicit XFlash physical region containing GPT.
    /// <para>显式指定的、持有 GPT 的 XFlash 物理区域。</para></param>
    /// <param name="steps">Explicit named partition operations; copied before asynchronous startup.
    /// <para>显式命名的分区操作；在异步启动前完成复制。</para></param>
    /// <param name="cancellationToken">The preparation cancellation token. <para>准备过程的取消令牌。</para></param>
    /// <returns>A caller-owned preparation retaining the session and all source handles until disposed.
    /// <para>持有会话与所有源句柄直至释放的自有预览对象。</para></returns>
    public static Task<MtkFlashPreparation> PrepareAsync(SerialConnectionOptions serial, MtkXFlashBootstrapOptions startup,
        uint physicalPartition, IReadOnlyList<MtkPartitionFlashStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(startup);
        return PrepareAsync(token => MtkXFlashBootstrap.ConnectSerialAsync(serial, startup, token),
            physicalPartition, steps, cancellationToken);
    }

    /// <summary>Boots an identity-matched agent on a session the device lease already owns, then
    /// prepares GPT-named operations against the live table.
    /// <para>在设备租约已持有的会话上启动按身份匹配的代理，随后针对实时表准备按 GPT 名寻址的操作。</para></summary>
    /// <param name="session">A leased session in the created state. <para>处于已创建状态的已租用会话。</para></param>
    /// <param name="protocol">Explicit DA family; no detection or fallback is performed.
    /// <para>显式的 DA 家族；不执行检测或回退。</para></param>
    /// <param name="startup">Parsed loader profile, storage family, identity policy and companion resources.
    /// <para>已解析的加载器配置、存储族、身份策略与伴随资源。</para></param>
    /// <param name="physicalPartition">Explicit physical user region to observe. <para>显式指定的待观测物理用户区域。</para></param>
    /// <param name="steps">Explicit named operations; copied before asynchronous startup.
    /// <para>显式命名的操作；在异步启动前完成复制。</para></param>
    /// <param name="cancellationToken">Cancellation for startup, metadata and source preflight.
    /// <para>用于启动、元数据与源预检的取消令牌。</para></param>
    /// <returns>An owned preview which performs storage writes only on a separate ExecuteAsync call.
    /// <para>仅在单独的 ExecuteAsync 调用中写入存储的自有预览对象。</para></returns>
    public static Task<MtkFlashPreparation> PrepareOnSessionAsync(MtkSession session, MtkDaProtocol protocol,
        MtkAutoStartup startup, uint physicalPartition, IReadOnlyList<MtkPartitionFlashStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(steps);
        return PrepareAsync(token => MtkBootstrap.MatchAsync(session, protocol, startup, token),
            physicalPartition, steps, cancellationToken);
    }

    internal static async Task<MtkFlashPreparation> PrepareAsync(Func<CancellationToken, Task<MtkSession>> connect,
        uint physicalPartition, IReadOnlyList<MtkPartitionFlashStep> steps, CancellationToken cancellationToken)
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
            throw new ArgumentException("A selected flash step is null.", nameof(steps));
        }
        return await PrepareCoreAsync(connect, physicalPartition,
            snapshot => MtkFlashPlan.Prepare(snapshot, selected, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MtkFlashPreparation> PrepareCoreAsync(Func<CancellationToken, Task<MtkSession>> connect,
        uint physicalPartition, Func<MtkGptSnapshot, MtkFlashPlan> prepare, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = await connect(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The connector returned no MTK session.");
        MtkFlashPlan? plan = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await session.ReadXFlashGptAsync(physicalPartition, cancellationToken).ConfigureAwait(false);
            plan = prepare(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            return new MtkFlashPreparation(session, plan, snapshot);
        }
        catch
        {
            plan?.Dispose();
            session.Dispose();
            throw;
        }
    }
}

/// <summary>Owns a previewed one-shot MTK plan and its live session. Abandoning a preview requires disposal.
/// <para>持有已预览的一次性 MTK 计划与其活动会话。放弃预览必须释放。</para></summary>
public sealed class MtkFlashPreparation : IMtkFlashPlanPreview
{
    private readonly MtkSession _session;
    private readonly MtkFlashPlan _plan;

    internal MtkFlashPreparation(MtkSession session, MtkFlashPlan plan, MtkGptSnapshot snapshot)
    {
        _session = session;
        _plan = plan;
        Snapshot = snapshot;
    }

    /// <summary>Gets the exact GPT observation used for resolving names; execution revalidates it.
    /// <para>获取用于解析分区名的确切 GPT 观测结果；执行时会重新校验。</para></summary>
    /// <value>The validated snapshot. <para>已校验的快照。</para></value>
    public MtkGptSnapshot Snapshot { get; }
    /// <summary>Gets the session's unified storage capability snapshot published during discovery;
    /// plan execution rejects it when the device geometry drifted.
    /// <para>获取会话在发现阶段发布的统一存储能力快照；设备几何漂移时计划执行会拒绝它。</para></summary>
    /// <value>The discovery snapshot, or null when the session reports none.
    /// <para>发现阶段的快照；会话未上报时为 null。</para></value>
    public Comm.Protocols.Mtk.Storage.MtkStorageCapabilitySnapshot? StorageCapability =>
        _session.StorageCapability;
    /// <summary>Gets the read-only physical operations to preview, excluding sparse holes.
    /// <para>获取待预览的只读物理操作，不含稀疏空洞。</para></summary>
    /// <value>One entry per extent. <para>每个范围对应一项。</para></value>
    public IReadOnlyList<MtkFlashOperation> Operations => _plan.Operations;
    /// <summary>Gets the number of executable extents in the plan.
    /// <para>获取计划中可执行范围的数量。</para></summary>
    /// <value>The extent count after preparation. <para>准备完成后的范围数量。</para></value>
    public int OperationCount => _plan.Operations.Count;
    /// <summary>Gets actual planned write/erase bytes, excluding optional readback and sparse holes.
    /// <para>获取实际计划的写入/擦除字节数，不含可选回读与稀疏空洞。</para></summary>
    /// <value>The checked total. <para>防溢出累加的总量。</para></value>
    public long TotalBytes => _plan.TotalBytes;
    /// <summary>Gets the communication plan's one-shot lifecycle state.
    /// <para>获取通信计划的单次生命周期状态。</para></summary>
    /// <value>The current state. <para>当前状态。</para></value>
    public MtkFlashPlanState State => _plan.State;

    /// <summary>Executes only after explicit confirmation; no retry or rollback is attempted.
    /// <para>仅在显式确认后执行；不尝试重试或回滚。</para></summary>
    /// <param name="progress">Optional phase-local progress reporter.
    /// <para>可选的阶段内进度报告器。</para></param>
    /// <param name="eraseTimeoutMs">The deadline for each erase range.
    /// <para>每个擦除范围的截止时间。</para></param>
    /// <param name="cancellationToken">The execution cancellation token.
    /// <para>执行取消令牌。</para></param>
    /// <returns>Completion totals. <para>完成总量。</para></returns>
    public Task<MtkFlashPlanResult> ExecuteAsync(IProgress<MtkFlashPlanProgress>? progress = null,
        int eraseTimeoutMs = 120000, CancellationToken cancellationToken = default) =>
        _session.ExecuteXFlashPlanAsync(_plan, progress, eraseTimeoutMs, cancellationToken);

    /// <summary>Releases source handles and the session. Disposal while the plan is running is rejected.
    /// <para>释放源句柄与会话。计划运行期间拒绝释放。</para></summary>
    public void Dispose()
    {
        _plan.Dispose();
        _session.Dispose();
    }
}