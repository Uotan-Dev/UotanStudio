using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Devices;
using UotanStudio.Abstractions.Logging;

namespace UotanStudio.Services.Devices
{
    /// <summary>
    /// <para>MediaTek Brom / Preloader 协议处理器（MTK COM）。通过 USB 串口与下载模式通讯。</para>
    /// MediaTek Brom / Preloader protocol handler (MTK COM). Communicates with download mode over a USB serial port.
    /// </summary>
    /// <remarks>
    /// <para>AOT 友好骨架：实际串口读写由 <c>SerialProtocolHandler</c> 抽象，此处仅编排高层命令。</para>
    /// AOT-friendly skeleton: actual serial read/write is abstracted by <c>SerialProtocolHandler</c>; this class only orchestrates high-level commands.
    /// </remarks>
    public sealed class MtkProtocolHandler : IProtocolHandler
    {
        private readonly IUnifiedLog _log;
        private readonly string _toolPath;

        /// <summary>
        /// <para>创建 MTK 协议处理器。</para>
        /// Creates the MTK protocol handler.
        /// </summary>
        public MtkProtocolHandler(IUnifiedLog log, string toolPath = "mtk")
        {
            _log = log;
            _toolPath = toolPath;
        }

        /// <inheritdoc />
        public DeviceProtocol Protocol => DeviceProtocol.MtkCom;

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            // 真实场景需枚举 USB VID:PID 0x0E8D:0x2000 (Brom) 或 0x0E8D:0x0003 (Preloader)
            await Task.CompletedTask.ConfigureAwait(false);
            return Array.Empty<string>();
        }

        /// <inheritdoc />
        public async Task<bool> IsPresentAsync(string serial, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return true;
        }

        /// <inheritdoc />
        public async Task ConnectAsync(string serial, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Info, "MTK", $"Opening brom/preloader channel to {serial}");
            // 真实实现：发送 DA (Download Agent) 并握手
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> ExecuteAsync(string serial, string command, CancellationToken cancellationToken = default)
        {
            // command 形如 "read boot" 或 "format userdata"
            return ProcessRunner.RunAsync(_toolPath, $"{command} --serial {serial}", _log, "MTK", 120000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> FlashPartitionAsync(string serial, string partition, string imagePath, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Info, "MTK", $"Writing partition '{partition}' from '{imagePath}'");
            return ProcessRunner.RunAsync(_toolPath, $"write {partition} \"{imagePath}\" --serial {serial}", _log, "MTK", 600000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> ErasePartitionAsync(string serial, string partition, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Info, "MTK", $"Formatting partition '{partition}'");
            return ProcessRunner.RunAsync(_toolPath, $"format {partition} --serial {serial}", _log, "MTK", 60000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> RebootAsync(string serial, RebootMode mode, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Info, "MTK", $"Reboot command on {serial}: {mode}");
            return ProcessRunner.RunAsync(_toolPath, $"reboot --serial {serial}", _log, "MTK", 10000, cancellationToken);
        }

        /// <inheritdoc />
        public void PopulateCapabilities(DeviceInfo info)
        {
            info.Manufacturer = "MediaTek";
            info.Capabilities.SupportsMtkBrom = true;
            info.Capabilities.SupportsFastbootFlash = false;
            info.Capabilities.SupportsRebootModes = false;
            info.Capabilities.KnownPartitions.Add("boot");
            info.Capabilities.KnownPartitions.Add("lk");
            info.Capabilities.KnownPartitions.Add("system");
            info.Capabilities.KnownPartitions.Add("userdata");
            info.Capabilities.KnownPartitions.Add("tee");
            info.Capabilities.SupportedImageFormats.Add("raw");
        }
    }
}
