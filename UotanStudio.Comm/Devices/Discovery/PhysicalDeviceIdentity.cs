using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// The best-evidence physical identity of one USB device, stable across mode switches when
/// container or port metadata exists.
/// <para>单台 USB 设备当前可得证据下的最优物理标识；存在容器或端口元数据时跨模式切换保持稳定。</para>
/// Correlation never uses VID/PID, enumeration order, or list position.
/// <para>关联判定绝不使用 VID/PID、枚举顺序或列表位置。</para>
/// </summary>
public sealed record PhysicalDeviceIdentity
{
    /// <summary>
    /// The strongest stable key: the container-id key when available, else the serial-based key.
    /// <para>当前最强稳定键：优先为容器 ID 键，否则为基于序列号的键。</para>
    /// </summary>
    /// <value>The container-id key when a container id exists, otherwise the serial-based key.
    /// <para>存在容器 ID 时为容器 ID 键，否则为基于序列号的键。</para></value>
    public required string Key { get; init; }

    /// <summary>
    /// The OS container id, when the backend reported one.
    /// <para>后端报告的操作系统容器 ID（若有）。</para>
    /// </summary>
    /// <value>The reported OS container id, or <see langword="null"/> when unreported.
    /// <para>后端上报的操作系统容器 ID；未上报时为空。</para></value>
    public Guid? ContainerId { get; init; }

    /// <summary>
    /// The bus-and-port chain the device was observed on, when the backend reported one.
    /// <para>设备被观测到的总线与端口链路径（若后端有报告）。</para>
    /// </summary>
    /// <value>The observed bus-and-port chain, or <see langword="null"/> when unreported.
    /// <para>观测到的总线与端口链路径；未上报时为空。</para></value>
    public string? PhysicalPortPath { get; init; }

    /// <summary>
    /// The OS device instance id, when the backend reported one.
    /// <para>后端报告的操作系统设备实例 ID（若有）。</para>
    /// </summary>
    /// <value>The reported OS device instance id, or <see langword="null"/> when unreported.
    /// <para>后端上报的操作系统设备实例 ID；未上报时为空。</para></value>
    public string? DeviceInstanceId { get; init; }

    /// <summary>
    /// Derives the identity from an enumerated device, or <see langword="null"/> when the device
    /// exposes neither a container id nor a usable serial, so identity is never claimed from
    /// VID/PID alone.
    /// <para>从已枚举设备推导标识；设备既无容器 ID 也无可用序列号时返回 <see langword="null"/>，
    /// 因此绝不会仅凭 VID/PID 声称身份。</para>
    /// </summary>
    /// <param name="info">
    /// The enumerated USB device. <para>已枚举的 USB 设备。</para>
    /// </param>
    /// <returns>The derived identity, or <see langword="null"/>. <para>推导出的标识，或 <see langword="null"/>。</para></returns>
    public static PhysicalDeviceIdentity? From(UsbDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var key = UsbPhysicalIdentity.Of(info, info.SerialNumber);
        if (key is null)
        {
            return null;
        }

        Guid? container = info.ContainerId is { } containerValue && containerValue != Guid.Empty
            ? containerValue
            : null;
        var port = info.PhysicalPortPath is { Length: > 0 } portValue && !portValue.Any(char.IsControl)
            ? portValue
            : null;
        return new PhysicalDeviceIdentity
        {
            Key = key,
            ContainerId = container,
            PhysicalPortPath = port,
            DeviceInstanceId = info.DeviceInstanceId is { Length: > 0 } instanceId ? instanceId : null
        };
    }

    /// <summary>
    /// Correlates this identity with another observation of possibly the same hardware.
    /// Container evidence wins, else usable port paths decide; missing evidence is indeterminate.
    /// <para>将本标识与（可能）同一硬件的另一观测关联。容器证据优先，否则由可用端口路径判定；
    /// 证据缺失时不确定。</para>
    /// </summary>
    /// <param name="other">
    /// The other identity to correlate with; <see langword="null"/> never correlates.
    /// <para>待关联的另一标识；<see langword="null"/> 永不关联。</para>
    /// </param>
    /// <returns>The correlation verdict. <para>关联判定结果。</para></returns>
    public DeviceCorrelationResult CorrelatesWith(PhysicalDeviceIdentity? other)
    {
        if (other is null)
        {
            return DeviceCorrelationResult.Indeterminate;
        }

        if (ContainerId is { } here && other.ContainerId is { } there)
        {
            return here == there ? DeviceCorrelationResult.Same : DeviceCorrelationResult.Different;
        }

        if (PhysicalPortPath is { } herePort && other.PhysicalPortPath is { } therePort)
        {
            return string.Equals(herePort, therePort, StringComparison.Ordinal)
                ? DeviceCorrelationResult.Same
                : DeviceCorrelationResult.Different;
        }

        return DeviceCorrelationResult.Indeterminate;
    }
}

/// <summary>
/// The verdict of correlating two physical-device observations.
/// <para>两次物理设备观测的关联判定结果。</para>
/// </summary>
public enum DeviceCorrelationResult
{
    /// <summary>
    /// The evidence proves the observations describe the same physical hardware.
    /// <para>证据证明两个观测指向同一物理硬件。</para>
    /// </summary>
    Same,

    /// <summary>
    /// The evidence proves the observations describe different physical hardware.
    /// <para>证据证明两个观测指向不同物理硬件。</para>
    /// </summary>
    Different,

    /// <summary>
    /// The evidence is missing or too weak to bind or separate the observations.
    /// <para>证据缺失或过弱，无法绑定也无法分离两个观测。</para>
    /// </summary>
    Indeterminate
}