using System.Collections.ObjectModel;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Firmware.VendorFirmware.MediaTek;

namespace UotanStudio.Services.Flash;

public static partial class MtkFlashService
{
    /// <summary>Starts an explicitly selected serial agent and prepares selected Scatter operations against live GPT.
    /// <para>启动显式选择的串口代理，并针对实时 GPT 准备所选 Scatter 操作。</para></summary>
    /// <param name="serial">Explicit serial port and line settings.<para>显式的串口与线路设置。</para></param>
    /// <param name="startup">Explicit loader, agent stages and compatibility policy.
    /// <para>显式的加载器、代理阶段与兼容性策略。</para></param>
    /// <param name="physicalPartition">Explicit physical user region to observe.
    /// <para>显式指定的待观测物理用户区域。</para></param>
    /// <param name="layout">Selected Scatter storage layout, not automatic platform authorization.
    /// <para>所选 Scatter 存储布局，而非自动平台授权。</para></param>
    /// <param name="steps">Explicit operations and selected source images.
    /// <para>显式操作与所选源镜像。</para></param>
    /// <param name="cancellationToken">Cancellation for connection, metadata and source preflight.
    /// <para>用于连接、元数据与源预检的取消令牌。</para></param>
    /// <returns>An owned preview which performs storage writes only on a separate ExecuteAsync call.
    /// <para>仅在单独的 ExecuteAsync 调用中写入存储的自有预览对象。</para></returns>
    public static Task<MtkFlashPreparation> PrepareScatterAsync(SerialConnectionOptions serial,
        MtkXFlashBootstrapOptions startup, uint physicalPartition, MtkScatterStorage layout,
        IReadOnlyList<MtkScatterFlashStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(startup);
        return PrepareScatterAsync(token => MtkXFlashBootstrap.ConnectSerialAsync(serial, startup, token),
            physicalPartition, layout, steps, cancellationToken);
    }

    /// <summary>Starts a serial agent whose loader entry is matched from the reported hardware identity, then prepares selected Scatter operations against the live GPT.
    /// <para>启动按上报硬件身份匹配加载器条目的串口代理，随后针对实时 GPT 准备所选 Scatter 操作。</para></summary>
    /// <param name="serial">Explicit serial port and line settings.<para>显式的串口与线路设置。</para></param>
    /// <param name="protocol">Explicit DA family; no detection or fallback is performed.
    /// <para>显式的 DA 家族；不执行检测或回退。</para></param>
    /// <param name="startup">Parsed loader profile, storage family, identity policy and companion resources.
    /// <para>已解析的加载器配置、存储族、身份策略与伴随资源。</para></param>
    /// <param name="physicalPartition">Explicit physical user region to observe.
    /// <para>显式指定的待观测物理用户区域。</para></param>
    /// <param name="layout">Selected Scatter storage layout, not automatic platform authorization.
    /// <para>所选 Scatter 存储布局，而非自动平台授权。</para></param>
    /// <param name="steps">Explicit operations and selected source images.
    /// <para>显式操作与所选源镜像。</para></param>
    /// <param name="cancellationToken">Cancellation for connection, metadata and source preflight.
    /// <para>用于连接、元数据与源预检的取消令牌。</para></param>
    /// <returns>An owned preview which performs storage writes only on a separate ExecuteAsync call.
    /// <para>仅在单独的 ExecuteAsync 调用中写入存储的自有预览对象。</para></returns>
    public static Task<MtkFlashPreparation> PrepareScatterMatchedAsync(SerialConnectionOptions serial,
        MtkDaProtocol protocol, MtkAutoStartup startup, uint physicalPartition, MtkScatterStorage layout,
        IReadOnlyList<MtkScatterFlashStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(startup);
        return PrepareScatterAsync(token => MtkBootstrap.MatchSerialAsync(serial, protocol, startup, token),
            physicalPartition, layout, steps, cancellationToken);
    }

    /// <summary>Boots an identity-matched agent on a session the device lease already owns, then prepares selected Scatter operations against the live GPT.
    /// <para>在设备租约已持有的会话上启动按身份匹配的代理，随后针对实时 GPT 准备所选 Scatter 操作。</para></summary>
    /// <param name="session">A leased session in the created state.<para>处于已创建状态的已租用会话。</para></param>
    /// <param name="protocol">Explicit DA family; no detection or fallback is performed.
    /// <para>显式的 DA 家族；不执行检测或回退。</para></param>
    /// <param name="startup">Parsed loader profile, storage family, identity policy and companion resources.
    /// <para>已解析的加载器配置、存储族、身份策略与伴随资源。</para></param>
    /// <param name="physicalPartition">Explicit physical user region to observe.
    /// <para>显式指定的待观测物理用户区域。</para></param>
    /// <param name="layout">Selected Scatter storage layout, not automatic platform authorization.
    /// <para>所选 Scatter 存储布局，而非自动平台授权。</para></param>
    /// <param name="steps">Explicit operations and selected source images.
    /// <para>显式操作与所选源镜像。</para></param>
    /// <param name="cancellationToken">Cancellation for startup, metadata and source preflight.
    /// <para>用于启动、元数据与源预检的取消令牌。</para></param>
    /// <returns>An owned preview which performs storage writes only on a separate ExecuteAsync call.
    /// <para>仅在单独的 ExecuteAsync 调用中写入存储的自有预览对象。</para></returns>
    public static Task<MtkFlashPreparation> PrepareScatterOnSessionAsync(MtkSession session,
        MtkDaProtocol protocol, MtkAutoStartup startup, uint physicalPartition, MtkScatterStorage layout,
        IReadOnlyList<MtkScatterFlashStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(startup);
        return PrepareScatterAsync(token => MtkBootstrap.MatchAsync(session, protocol, startup, token),
            physicalPartition, layout, steps, cancellationToken);
    }

    internal static async Task<MtkFlashPreparation> PrepareScatterAsync(Func<CancellationToken, Task<MtkSession>> connect,
        uint physicalPartition, MtkScatterStorage layout, IReadOnlyList<MtkScatterFlashStep> steps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(layout.Partitions);
        ArgumentNullException.ThrowIfNull(steps);
        cancellationToken.ThrowIfCancellationRequested();
        if (steps.Count is 0 or > 256 || layout.Partitions.Count is 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "Scatter selections or metadata exceed host limits.");
        }
        var selected = steps.ToArray();
        if (selected.Any(step => step is null))
        {
            throw new ArgumentException("A selected scatter operation is null.", nameof(steps));
        }
        var partitions = new MtkScatterPartition[layout.Partitions.Count];
        for (var index = 0; index < partitions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = layout.Partitions[index] ?? throw new ArgumentException("A scatter entry is null.", nameof(layout));
            partitions[index] = entry with { Fields = SnapshotScatterFields(entry.Fields) };
        }
        var captured = layout with
        {
            Fields = SnapshotScatterFields(layout.Fields),
            Partitions = Array.AsReadOnly(partitions)
        };
        return await PrepareCoreAsync(connect, physicalPartition,
            snapshot => MtkFlashPlan.Prepare(snapshot, captured, selected, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string> SnapshotScatterFields(IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count > 64 || fields.Any(pair => pair.Key.Length > 128 || pair.Value is null || pair.Value.Length > 4096))
        {
            throw new ArgumentException("Scatter metadata fields exceed host limits.", nameof(fields));
        }
        return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(fields, StringComparer.Ordinal));
    }
}