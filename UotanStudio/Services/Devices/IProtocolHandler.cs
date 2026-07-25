using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Devices;

namespace UotanStudio.Services.Devices
{
    /// <summary>
    /// <para>协议处理器接口。每种通讯协议（ADB、Fastboot、EDL、MTK、Rockchip）实现此接口。</para>
    /// Protocol handler interface. Each communication protocol (ADB, Fastboot, EDL, MTK, Rockchip) implements this.
    /// </summary>
    public interface IProtocolHandler
    {
        /// <summary>
        /// <para>处理器支持的协议。</para>
        /// Protocol supported by the handler.
        /// </summary>
        DeviceProtocol Protocol { get; }

        /// <summary>
        /// <para>枚举当前可见设备序列号列表。</para>
        /// Enumerates the list of currently visible device serials.
        /// </summary>
        Task<IReadOnlyList<string>> EnumerateAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// <para>检查指定设备是否仍在线。</para>
        /// Checks whether the specified device is still present.
        /// </summary>
        Task<bool> IsPresentAsync(string serial, CancellationToken cancellationToken = default);

        /// <summary>
        /// <para>连接设备。</para>
        /// Connects to the device.
        /// </summary>
        Task ConnectAsync(string serial, CancellationToken cancellationToken = default);

        /// <summary>
        /// <para>执行命令。</para>
        /// Executes a command.
        /// </summary>
        Task<DeviceCommandResult> ExecuteAsync(string serial, string command, CancellationToken cancellationToken = default);

        /// <summary>
        /// <para>刷写分区。</para>
        /// Flashes a partition.
        /// </summary>
        Task<DeviceCommandResult> FlashPartitionAsync(string serial, string partition, string imagePath, CancellationToken cancellationToken = default);

        /// <summary>
        /// <para>擦除分区。</para>
        /// Erases a partition.
        /// </summary>
        Task<DeviceCommandResult> ErasePartitionAsync(string serial, string partition, CancellationToken cancellationToken = default);

        /// <summary>
        /// <para>重启设备到指定模式。</para>
        /// Reboots the device to the specified mode.
        /// </summary>
        Task<DeviceCommandResult> RebootAsync(string serial, RebootMode mode, CancellationToken cancellationToken = default);

        /// <summary>
        /// <para>填充设备能力描述。</para>
        /// Populates the device capability descriptor.
        /// </summary>
        void PopulateCapabilities(DeviceInfo info);
    }
}
