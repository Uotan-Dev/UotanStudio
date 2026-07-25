using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Devices;
using UotanStudio.Abstractions.Logging;

namespace UotanStudio.Services.Devices
{
    /// <summary>
    /// <para>Rockchip BootROM 协议处理器。通过 <c>rkdeveloptool</c> 或 <c>upgrade_tool</c> 与 RK 设备通讯。</para>
    /// Rockchip BootROM protocol handler. Communicates with RK devices via <c>rkdeveloptool</c> or <c>upgrade_tool</c>.
    /// </summary>
    public sealed class RockchipProtocolHandler : IProtocolHandler
    {
        private readonly IUnifiedLog _log;
        private readonly string _toolPath;

        /// <summary>
        /// <para>创建 Rockchip 协议处理器。</para>
        /// Creates the Rockchip protocol handler.
        /// </summary>
        public RockchipProtocolHandler(IUnifiedLog log, string toolPath = "rkdeveloptool")
        {
            _log = log;
            _toolPath = toolPath;
        }

        /// <inheritdoc />
        public DeviceProtocol Protocol => DeviceProtocol.RkBootrom;

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            // rkdeveloptool list-devices
            var res = await ProcessRunner.RunAsync(_toolPath, "list", _log, "RK", 5000, cancellationToken).ConfigureAwait(false);
            if (!res.Success) return Array.Empty<string>();

            var serials = new List<string>();
            foreach (var line in res.Output.Replace("\r\n", "\n").Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0 && !t.StartsWith("DevNo", StringComparison.OrdinalIgnoreCase))
                {
                    serials.Add(t);
                }
            }
            return serials;
        }

        /// <inheritdoc />
        public async Task<bool> IsPresentAsync(string serial, CancellationToken cancellationToken = default)
        {
            var serials = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
            foreach (var s in serials) if (s == serial) return true;
            return false;
        }

        /// <inheritdoc />
        public async Task ConnectAsync(string serial, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Info, "RK", $"Connecting to Rockchip device {serial}");
            // Rockchip 协议无需显式连接，但需要选择设备
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> ExecuteAsync(string serial, string command, CancellationToken cancellationToken = default)
        {
            // command 形如 "read-flash-info" 或 "read-partition boot"
            return ProcessRunner.RunAsync(_toolPath, $"{command}", _log, "RK", 30000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> FlashPartitionAsync(string serial, string partition, string imagePath, CancellationToken cancellationToken = default)
        {
            // rkdeveloptool wl <offset> <image>  或  write-partition <partition> <image>
            _log.Write(LogLevel.Info, "RK", $"Writing partition '{partition}' from '{imagePath}'");
            return ProcessRunner.RunAsync(_toolPath, $"write-partition {partition} \"{imagePath}\"", _log, "RK", 600000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> ErasePartitionAsync(string serial, string partition, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Info, "RK", $"Erasing partition '{partition}'");
            return ProcessRunner.RunAsync(_toolPath, $"erase-partition {partition}", _log, "RK", 60000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> RebootAsync(string serial, RebootMode mode, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Info, "RK", $"Reboot to mode {mode}");
            return ProcessRunner.RunAsync(_toolPath, "reboot", _log, "RK", 10000, cancellationToken);
        }

        /// <inheritdoc />
        public void PopulateCapabilities(DeviceInfo info)
        {
            info.Manufacturer = "Rockchip";
            info.Capabilities.SupportsRkUpgrade = true;
            info.Capabilities.SupportsFastbootFlash = false;
            info.Capabilities.SupportsRebootModes = false;
            info.Capabilities.KnownPartitions.Add("boot");
            info.Capabilities.KnownPartitions.Add("recovery");
            info.Capabilities.KnownPartitions.Add("rootfs");
            info.Capabilities.KnownPartitions.Add("uboot");
            info.Capabilities.KnownPartitions.Add("user");
            info.Capabilities.SupportedImageFormats.Add("raw");
            info.Capabilities.SupportedImageFormats.Add("rkaf");
        }
    }
}
