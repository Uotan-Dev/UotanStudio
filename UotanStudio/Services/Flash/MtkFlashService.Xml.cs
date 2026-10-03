using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Services.Flash;

/// <summary>Host orchestration for the MediaTek XML download agent family.
/// <para>面向联发科 XML 下载代理家族的宿主编排。</para></summary>
/// <remarks>
/// <para>The XML agent transfers raw, block-aligned ranges of a named physical region only. Preparation boots
/// the agent, reads that region's GPT and reconciles the requested partition names with the live table before
/// any source is opened; nothing here erases, pads or writes until execution is confirmed separately.</para>
/// <para>XML 代理只传输命名区域中按块对齐的原始范围。准备阶段启动代理、读取该区域的 GPT，
/// 并在打开任何源之前把请求的分区名与该实时表核对；在单独确认执行之前，本流程不会擦除、填充或写入。</para>
/// </remarks>
public static partial class MtkFlashService
{
    /// <summary>Boots an XML agent on a leased session and previews GPT-named operations.
    /// <para>在已租用的会话上启动 XML 代理并预览按 GPT 名寻址的操作。</para></summary>
    /// <param name="session">A leased session in the created state.
    /// <para>处于已创建状态的已租用会话。</para></param>
    /// <param name="startup">Parsed loader profile, XML settings, storage family, identity policy and companion
    /// resources. <para>已解析的加载器配置、XML 设置、存储族、身份策略与伴随资源。</para></param>
    /// <param name="regionName">Explicit XML region token holding the GPT, such as <c>EMMC-USER</c>.
    /// <para>显式指定的、持有 GPT 的 XML 区域标记，例如 <c>EMMC-USER</c>。</para></param>
    /// <param name="steps">Explicit named operations; copied before asynchronous startup.
    /// <para>显式命名的操作；在异步启动前完成复制。</para></param>
    /// <param name="cancellationToken">Cancellation for startup, metadata and source preflight.
    /// <para>用于启动、元数据与源预检的取消令牌。</para></param>
    /// <returns>A preview owning the session and every source handle until disposed.
    /// <para>持有会话与所有源句柄直至释放的预览对象。</para></returns>
    public static Task<MtkXmlFlashPreparation> PrepareXmlOnSessionAsync(MtkSession session,
        MtkAutoStartup startup, string regionName, IReadOnlyList<MtkXmlPartitionFlashStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(startup);
        return PrepareXmlAsync(
            token => MtkBootstrap.MatchAsync(session, MtkDaProtocol.Xml, startup, token),
            regionName, steps, cancellationToken);
    }

    /// <summary>Boots an XML agent over an explicit serial port and previews GPT-named operations.
    /// <para>经显式串口启动 XML 代理并预览按 GPT 名寻址的操作。</para></summary>
    /// <param name="serial">Explicit COM/TTY and line settings.
    /// <para>显式的 COM/TTY 与线路设置。</para></param>
    /// <param name="startup">Identity-matched loader selection, XML settings and companion resources.
    /// <para>按身份匹配的加载器选择、XML 设置与伴随资源。</para></param>
    /// <param name="regionName">Explicit XML region token holding the GPT, such as <c>EMMC-USER</c>.
    /// <para>显式指定的、持有 GPT 的 XML 区域标记，例如 <c>EMMC-USER</c>。</para></param>
    /// <param name="steps">Explicit named operations; copied before asynchronous startup.
    /// <para>显式命名的操作；在异步启动前完成复制。</para></param>
    /// <param name="cancellationToken">Cancellation for startup, metadata and source preflight.
    /// <para>用于启动、元数据与源预检的取消令牌。</para></param>
    /// <returns>A preview owning the session and every source handle until disposed.
    /// <para>持有会话与所有源句柄直至释放的预览对象。</para></returns>
    public static Task<MtkXmlFlashPreparation> PrepareXmlSerialAsync(SerialConnectionOptions serial,
        MtkAutoStartup startup, string regionName, IReadOnlyList<MtkXmlPartitionFlashStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(startup);
        return PrepareXmlAsync(
            token => MtkBootstrap.MatchSerialAsync(serial, MtkDaProtocol.Xml, startup, token),
            regionName, steps, cancellationToken);
    }

    /// <summary>Previews GPT-named XML operations through a caller-supplied session connector.
    /// <para>经调用方提供的会话连接器预览按 GPT 名寻址的 XML 操作。</para></summary>
    /// <param name="connect">Connector returning a storage-ready XML session; ownership transfers to the
    /// returned preview. <para>返回存储就绪 XML 会话的连接器；所有权转移给返回的预览对象。</para></param>
    /// <param name="regionName">Explicit XML region token holding the GPT.
    /// <para>显式指定的、持有 GPT 的 XML 区域标记。</para></param>
    /// <param name="steps">Explicit named operations; copied before asynchronous startup.
    /// <para>显式命名的操作；在异步启动前完成复制。</para></param>
    /// <param name="cancellationToken">Cancellation for startup, metadata and source preflight.
    /// <para>用于启动、元数据与源预检的取消令牌。</para></param>
    /// <returns>A preview owning the session and every source handle until disposed.
    /// <para>持有会话与所有源句柄直至释放的预览对象。</para></returns>
    public static Task<MtkXmlFlashPreparation> PrepareXmlAsync(
        Func<CancellationToken, Task<MtkSession>> connect, string regionName,
        IReadOnlyList<MtkXmlPartitionFlashStep> steps, CancellationToken cancellationToken = default) =>
        PrepareXmlCoreAsync(connect, regionName, steps, cancellationToken);

    private static async Task<MtkXmlFlashPreparation> PrepareXmlCoreAsync(
        Func<CancellationToken, Task<MtkSession>> connect, string regionName,
        IReadOnlyList<MtkXmlPartitionFlashStep> steps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        if (steps.Count is 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(steps));
        }

        var selected = steps.ToArray();
        if (selected.Any(step => step is null))
        {
            throw new ArgumentException("A selected XML flash step is null.", nameof(steps));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var session = await connect(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The connector returned no MTK session.");
        MtkXmlFlashPlan? plan = null;
        try
        {
            var snapshot = await session.ReadXmlGptAsync(regionName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            plan = MtkXmlFlashPlan.Prepare(snapshot, selected, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new MtkXmlFlashPreparation(session, plan, snapshot);
        }
        catch
        {
            plan?.Dispose();
            session.Dispose();
            throw;
        }
    }
}

/// <summary>Owns a previewed XML plan and its live session.
/// <para>持有已预览的 XML 计划与其活动会话。</para></summary>
/// <remarks>
/// <para>Abandoning a preview requires disposal. Disposal while execution is running is rejected so an active
/// transfer keeps its source handles alive.</para>
/// <para>放弃预览必须释放。执行期间拒绝释放，使进行中的传输仍能持有源句柄。</para>
/// </remarks>
public sealed class MtkXmlFlashPreparation : IMtkFlashPlanPreview
{
    private readonly MtkSession _session;
    private readonly MtkXmlFlashPlan _plan;

    internal MtkXmlFlashPreparation(MtkSession session, MtkXmlFlashPlan plan, MtkXmlGptSnapshot snapshot)
    {
        _session = session;
        _plan = plan;
        Snapshot = snapshot;
    }

    /// <summary>Gets the GPT observation used to resolve names; execution revalidates it.
    /// <para>获取用于解析分区名的 GPT 观测结果；执行时会重新校验。</para></summary>
    /// <value>The validated snapshot. <para>已校验的快照。</para></value>
    public MtkXmlGptSnapshot Snapshot { get; }

    /// <summary>Gets the read-only physical operations to preview.
    /// <para>获取待预览的只读物理操作。</para></summary>
    /// <value>One entry per extent. <para>每个范围对应一项。</para></value>
    public IReadOnlyList<MtkXmlFlashOperation> Operations => _plan.Operations;

    /// <inheritdoc />
    public int OperationCount => _plan.Operations.Count;

    /// <inheritdoc />
    public long TotalBytes => _plan.TotalBytes;

    /// <inheritdoc />
    public MtkFlashPlanState State => _plan.State;

    /// <inheritdoc />
    public Task<MtkFlashPlanResult> ExecuteAsync(IProgress<MtkFlashPlanProgress>? progress = null,
        int eraseTimeoutMs = 120000, CancellationToken cancellationToken = default) =>
        _session.ExecuteXmlPlanAsync(_plan, progress, eraseTimeoutMs, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _plan.Dispose();
        _session.Dispose();
    }
}