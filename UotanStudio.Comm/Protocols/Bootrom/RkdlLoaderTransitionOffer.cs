using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// The evidence an RKDL session publishes around a loader download, so the device layer
/// can wait for the post-download USB re-enumeration and open the replacement endpoint.
/// <para>RKDL 会话在 loader 下载前后发布的证据，便于设备层等待下载后的 USB 重枚举并
/// 打开替代端点。</para>
/// </summary>
public sealed record RkdlLoaderTransitionOffer
{
    /// <summary>
    /// Gets the endpoint the loader-download session was leased on, when the session knows one.
    /// <para>获取 loader 下载会话所租用的端点（若会话可知）。</para>
    /// </summary>
    /// <value>The exact previous endpoint, or <see langword="null"/> when the session knows none.
    /// <para>精确的旧端点；会话不知晓端点时为 <see langword="null"/>。</para></value>
    public required DeviceEndpoint? PreviousEndpoint { get; init; }

    /// <summary>
    /// Gets the best-evidence physical identity of the device behind the previous endpoint, captured
    /// when the session was opened.
    /// <para>获取会话打开时捕获的旧端点背后设备的最优物理身份。</para>
    /// </summary>
    /// <value>The correlated identity, or <see langword="null"/> when no correlatable metadata
    /// is available. <para>可关联的身份；无可关联元数据时为 <see langword="null"/>。</para></value>
    public PhysicalDeviceIdentity? Identity { get; init; }

    /// <summary>
    /// Gets the transport generation the previous endpoint was opened on, when the controller stamped one.
    /// <para>获取旧端点打开所在的传输代际（若控制器盖印过）。</para>
    /// </summary>
    /// <value>The observed generation, or <see langword="null"/>. <para>观测到的代际，或 <see langword="null"/>。</para></value>
    public TransportGeneration? ObservedGeneration { get; init; }

    /// <summary>
    /// Gets the recommended cooperative deadline for waiting for the replacement endpoint.
    /// <para>获取等待替代端点建议采用的协作式截止时间。</para>
    /// </summary>
    /// <value>Defaults to <see cref="RkdlConstants.LoaderRelinkTimeoutMs"/>, matching the controller
    /// bound of 1 millisecond through 24 hours.
    /// <para>默认取 <see cref="RkdlConstants.LoaderRelinkTimeoutMs"/>，与控制器 1 毫秒至 24 小时的约束一致。</para></value>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(RkdlConstants.LoaderRelinkTimeoutMs);
}