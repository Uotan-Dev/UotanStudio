using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>刷写服务接口：统一 Fastboot / EDL / RKDL / ADB 四条刷写路径。
/// 通过 DI 注入可替换为测试 mock。</para>
/// Flash service interface: unifies the Fastboot / EDL / RKDL / ADB flash paths.
/// Injectable via DI and mockable for testing.
/// </summary>
public interface IFlashService
{
    /// <summary>
    /// <para>把指定镜像刷写到设备分区；异常被捕获并转为失败结果。</para>
    /// Flashes the specified image to a device partition; exceptions are captured and
    /// converted into a failure result.
    /// </summary>
    Task<FlashResult> FlashImageAsync(
        DeviceInfo device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct);

    /// <summary>
    /// <para>擦除单个分区（仅 fastboot 协议）。</para>
    /// Erases a single partition (fastboot protocol only).
    /// </summary>
    Task<FlashResult> ErasePartitionAsync(
        DeviceInfo device, string partition, IProgress<FlashProgress>? progress, CancellationToken ct);

    /// <summary>
    /// <para>擦除设备上的全部分区（仅 fastboot 协议）。</para>
    /// Erases every partition on the device (fastboot protocol only).
    /// </summary>
    Task<FlashResult> EraseAllAsync(
        DeviceInfo device, IProgress<FlashProgress>? progress, CancellationToken ct);

    /// <summary>
    /// <para>通过 ADB 把 OTA 包推送到 recovery sideload 模式。</para>
    /// Pushes an OTA package to the device in recovery sideload mode via ADB.
    /// </summary>
    Task<FlashResult> FlashSideloadAsync(
        DeviceInfo device, string otaPackagePath, IProgress<FlashProgress>? progress, CancellationToken ct);

    /// <summary>
    /// <para>查询设备属性（fastboot getvar / ADB getprop）。</para>
    /// Queries a device property (fastboot getvar / ADB getprop).
    /// </summary>
    Task<FlashResult> GetDevicePropertyAsync(
        DeviceInfo device, string propertyName, CancellationToken ct);
}
