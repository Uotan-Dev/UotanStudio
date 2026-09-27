using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// The best-evidence physical identity of one USB device, stable across mode switches and
/// interface renumbering where the backend supplies container or physical-port metadata.
/// <para>单台 USB 设备当前可得证据下的最优物理标识；在后端提供容器或物理端口元数据时，
/// 跨模式切换与接口重编号保持稳定。</para>
/// Correlation never uses VID/PID, enumeration order, or list position; absent or conflicting
/// evidence yields <see cref="DeviceCorrelationResult.Indeterminate"/> instead of a guess.
/// <para>关联判定绝不使用 VID/PID、枚举顺序或列表位置；证据缺失或冲突时返回
/// <see cref="DeviceCorrelationResult.Indeterminate"/>，绝不猜测。</para>
/// </summary>
public sealed record PhysicalDeviceIdentity
{
    /// <summary>
    /// Gets the strongest stable key: the container-id key when available, else the serial-based key.
    /// <para>获取当前最强稳定键：优先为容器 ID 键，否则为基于序列号的键。</para>
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Gets the OS container id, when the backend reported one.
    /// <para>获取后端报告的操作系统容器 ID（若有）。</para>
    /// </summary>
    public Guid? ContainerId { get; init; }

    /// <summary>
    /// Gets the bus-and-port chain the device was observed on, when the backend reported one.
    /// <para>获取设备被观测到的总线与端口链路径（若后端有报告）。</para>
    /// </summary>
    public string? PhysicalPortPath { get; init; }

    /// <summary>
    /// Gets the OS device instance id, when the backend reported one.
    /// <para>获取操作系统设备实例 ID（若后端有报告）。</para>
    /// </summary>
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
        string? port = info.PhysicalPortPath is { Length: > 0 } portValue && !portValue.Any(char.IsControl)
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
    /// Correlates this identity with another observation of (possibly) the same hardware.
    /// A container verdict wins over the port verdict; without a container verdict, equal usable
    /// port paths correlate as <see cref="DeviceCorrelationResult.Same"/> and different ones as
    /// <see cref="DeviceCorrelationResult.Different"/>; everything else is indeterminate.
    /// <para>将本标识与（可能）同一硬件的另一观测关联。容器判定优先于端口判定；
    /// 无法按容器判定时，可用端口路径相同为 <see cref="DeviceCorrelationResult.Same"/>、
    /// 不同为 <see cref="DeviceCorrelationResult.Different"/>；其余情况一律不确定。</para>
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