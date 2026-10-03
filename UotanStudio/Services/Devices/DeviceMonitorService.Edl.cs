using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    private readonly EdlConnectionCache _edlConnections;

    /// <summary>Observes the EDL protocol while preserving the handshake for the next device operation.</summary>
    public Task<EdlProtocolState> ProbeEdlProtocolAsync(DetectedDevice device,
        CancellationToken cancellationToken = default)
    {
        EnsureEdlLivenessProbe();
        return _edlConnections.ProbeAsync(device, cancellationToken);
    }

    /// <summary>
    /// <para>返回该 EDL 端点最近一次探测附加采集到的只读详情快照；探测尚未运行或采集失败时为 null。</para>
    /// Returns the read-only detail snapshot collected alongside the EDL endpoint's latest probe;
    /// null when the probe has not run yet or collection failed.
    /// </summary>
    /// <param name="device">The EDL endpoint to query. <para>要查询的 EDL 端点。</para></param>
    /// <returns>The latest detail snapshot, or <see langword="null"/>.
    /// <para>最近的详情快照；无则 <see langword="null"/>。</para></returns>
    public EdlDetailsSnapshot? GetEdlDetails(DetectedDevice device) => _edlConnections.GetDetails(device.Key);

    /// <summary>
    /// <para>惰性接线 EDL 租约代际判活探针：parked 租约只有在携带类型化 EdlSession、会话连接
    /// 仍存活，且其传输代际与控制器当前代际不矛盾时才可复用；代际不同即作废重开。探针缺失
    /// 身份信息或从未观测到代际时保留租约，交由会话自身的故障路径退休。</para>
    /// Lazily wires the EDL lease-generation liveness probe: a parked lease is reusable only when it
    /// carries a typed <c>EdlSession</c> whose connection is live and its transport generation does
    /// not contradict the controller's current generation; a different generation retires the lease
    /// and reopens. When identity evidence or an observed generation is absent the lease is kept and
    /// the session's own fault paths retire it.
    /// </summary>
    private void EnsureEdlLivenessProbe() => _edlConnections.LeaseCurrentProbe ??= IsEdlLeaseGenerationCurrent;

    private bool IsEdlLeaseGenerationCurrent(DeviceLease lease) =>
        lease.Generation is not { } generation
        || lease.Endpoint.UsbInfo is not { } info
        || PhysicalDeviceIdentity.From(info) is not { } identity
        || !_controller.TryPeekTransportGeneration(identity, out var current)
        || current == generation;
}