using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>
/// The device target handed to device-facing application services: the verified protocol endpoints, the
/// selected endpoint and the primitive identity the operation needs.
/// <para>交给面向设备的应用服务的设备目标：已核实的协议端点、当前选中的端点，以及操作需要的基本身份信息。</para>
/// </summary>
public sealed record DeviceTarget
{
    /// <summary>
    /// Creates a target from one endpoint.
    /// <para>用单个端点创建目标。</para>
    /// </summary>
    /// <param name="endpoint">The endpoint the operation runs against.<para>操作使用的端点。</para></param>
    public DeviceTarget(DetectedDevice endpoint) : this([endpoint], endpoint)
    {
    }

    /// <summary>
    /// Creates a target from every verified endpoint of one physical device.
    /// <para>用一台物理设备的全部已核实端点创建目标。</para>
    /// </summary>
    /// <param name="endpoints">Verified endpoints; at least one is required.<para>已核实的端点；至少需要一个。</para></param>
    /// <param name="endpoint">The selected endpoint; falls back to the first one when omitted or not a member.
    /// <para>选中的端点；省略或不属于端点集合时回退到第一个。</para></param>
    public DeviceTarget(IReadOnlyList<DetectedDevice> endpoints, DetectedDevice? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("A device target requires an endpoint.", nameof(endpoints));
        }

        var snapshot = new DetectedDevice[endpoints.Count];
        for (var index = 0; index < endpoints.Count; index++)
        {
            snapshot[index] = endpoints[index];
        }

        Endpoints = snapshot;
        Endpoint = endpoint is not null && Array.IndexOf(snapshot, endpoint) >= 0 ? endpoint : snapshot[0];
    }

    /// <summary>Verified protocol endpoints of the physical device. <para>该物理设备的全部已核实协议端点。</para></summary>
    public IReadOnlyList<DetectedDevice> Endpoints { get; }

    /// <summary>The endpoint device operations run against. <para>设备操作使用的端点。</para></summary>
    public DetectedDevice Endpoint { get; }

    /// <summary>Protocol of the selected endpoint. <para>选中端点的协议。</para></summary>
    public DeviceProtocolKind Protocol => Endpoint.Protocol;

    /// <summary>Serial number of the selected endpoint. <para>选中端点的序列号。</para></summary>
    public string Serial => Endpoint.Serial;

    /// <summary>Display name of the selected endpoint. <para>选中端点的显示名称。</para></summary>
    public string DisplayName => Endpoint.DisplayName;

    /// <summary>State text of the selected endpoint. <para>选中端点的状态文本。</para></summary>
    public string State => Endpoint.State;

    /// <summary>Protocol and route label of the selected endpoint. <para>选中端点的协议与路由标签。</para></summary>
    public string Mode => Endpoint.ProtocolLabel;

    /// <summary>The EDL wire protocol observed for the endpoint. <para>为该端点观察到的 EDL 协议。</para></summary>
    public EdlProtocolState EdlProtocol => Endpoint.EdlProtocol;

    /// <summary>Whether the physical device exposes more than one protocol endpoint. <para>物理设备是否暴露多个协议端点。</para></summary>
    public bool HasMultipleEndpoints => Endpoints.Count > 1;
}