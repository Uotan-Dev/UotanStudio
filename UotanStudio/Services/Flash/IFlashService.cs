using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Flash service interface: unifies the Fastboot / EDL / RKDL / ADB flash paths.
/// Injectable via DI and mockable for testing.
/// <para>刷写服务接口：统一 Fastboot / EDL / RKDL / ADB 四条刷写路径。
/// 通过 DI 注入可替换为测试 mock。</para>
/// </summary>
public interface IFlashService
{
    /// <summary>
    /// Flashes the specified image to a device partition; exceptions are captured and
    /// converted into a failure result.
    /// <para>把指定镜像刷写到设备分区；异常被捕获并转为失败结果。</para>
    /// </summary>
    /// <param name="device">The target device whose protocol selects the flash path. <para>决定刷写路径的目标设备。</para></param>
    /// <param name="partition">The target partition name. <para>目标分区名。</para></param>
    /// <param name="imagePath">Full path of the image file to flash. <para>待刷写镜像文件的完整路径。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash result describing success or failure. <para>描述成功或失败的刷写结果。</para></returns>
    Task<FlashResult> FlashImageAsync(
        DeviceTarget device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Erases a single partition (fastboot protocol only).
    /// <para>擦除单个分区（仅 fastboot 协议）。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="partition">The partition to erase. <para>要擦除的分区名。</para></param>
    /// <param name="progress">Optional erase progress reporter. <para>可选的擦除进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The erase result. <para>擦除结果。</para></returns>
    Task<FlashResult> ErasePartitionAsync(
        DeviceTarget device, string partition, IProgress<FlashProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Erases every partition on the device (fastboot protocol only).
    /// <para>擦除设备上的全部分区（仅 fastboot 协议）。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="progress">Optional erase progress reporter. <para>可选的擦除进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The erase result. <para>擦除结果。</para></returns>
    Task<FlashResult> EraseAllAsync(
        DeviceTarget device, IProgress<FlashProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Pushes an OTA package to the device in recovery sideload mode via ADB.
    /// <para>通过 ADB 把 OTA 包推送到 recovery sideload 模式。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="otaPackagePath">Full path of the OTA (zip) package. <para>OTA 镜像（zip）完整路径。</para></param>
    /// <param name="progress">Optional push progress reporter. <para>可选的推送进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The sideload result. <para>侧载结果。</para></returns>
    Task<FlashResult> FlashSideloadAsync(
        DeviceTarget device, string otaPackagePath, IProgress<FlashProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Queries a device property (fastboot getvar / ADB getprop).
    /// <para>查询设备属性（fastboot getvar / ADB getprop）。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="propertyName">The property or variable name. <para>属性或变量名。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The query result whose summary holds the name and value. <para>摘要中给出名称与值的查询结果。</para></returns>
    Task<FlashResult> GetDevicePropertyAsync(
        DeviceTarget device, string propertyName, CancellationToken ct);
}