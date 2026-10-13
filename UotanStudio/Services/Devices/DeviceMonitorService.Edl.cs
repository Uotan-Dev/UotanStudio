using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    private readonly EdlConnectionCache _edlConnections;

    /// <summary>
    /// Observes the EDL protocol while preserving the handshake for the next device operation.
    /// <para>观测 EDL 协议状态，同时为后续设备操作保留握手信息。</para>
    /// </summary>
    /// <param name="device">The EDL endpoint to probe. <para>要探测的 EDL 端点。</para></param>
    /// <param name="cancellationToken">Token observing the probe. <para>观测探测过程的取消令牌。</para></param>
    /// <returns>The observed protocol state. <para>观测到的协议状态。</para></returns>
    public Task<EdlProtocolState> ProbeEdlProtocolAsync(DetectedDevice device,
        CancellationToken cancellationToken = default)
    {
        EnsureEdlLivenessProbe();
        return _edlConnections.ProbeAsync(device, cancellationToken);
    }

    /// <summary>
    /// Returns the read-only detail snapshot collected alongside the EDL endpoint's latest probe;
    /// null when the probe has not run yet or collection failed.
    /// <para>返回该 EDL 端点最近一次探测附加采集到的只读详情快照；探测尚未运行或采集失败时为 null。</para>
    /// </summary>
    /// <param name="device">The EDL endpoint to query. <para>要查询的 EDL 端点。</para></param>
    /// <returns>The latest detail snapshot, or <see langword="null"/>.
    /// <para>最近的详情快照；无则 <see langword="null"/>。</para></returns>
    public EdlDetailsSnapshot? GetEdlDetails(DetectedDevice device) => _edlConnections.GetDetails(device.Key);

    /// <summary>
    /// Installs the lease-generation liveness probe on first use, so a stored lease is reused only
    /// while its transport generation still matches the current one.
    /// <para>首次使用时安装租约代际判活探针，使已存放的租约仅在其传输代际与当前代际一致时复用。</para>
    /// </summary>
    private void EnsureEdlLivenessProbe() => _edlConnections.LeaseCurrentProbe ??= IsEdlLeaseGenerationCurrent;

    private bool IsEdlLeaseGenerationCurrent(DeviceLease lease) =>
        lease.Generation is not { } generation
        || lease.Endpoint.UsbInfo is not { } info
        || PhysicalDeviceIdentity.From(info) is not { } identity
        || !_controller.TryPeekTransportGeneration(identity, out var current)
        || current == generation;
}