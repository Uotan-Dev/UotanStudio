using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Devices;
using UotanStudio.Abstractions.Logging;

namespace UotanStudio.Services.Devices
{
    /// <summary>
    /// <para>Fastboot 协议处理器（bootloader 模式）。通过 <c>fastboot</c> 可执行文件枚举、刷写、擦除、重启。</para>
    /// Fastboot protocol handler (bootloader mode). Uses the <c>fastboot</c> executable to enumerate, flash, erase, reboot.
    /// </summary>
    public sealed class FastbootProtocolHandler : IProtocolHandler
    {
        private readonly IUnifiedLog _log;
        private readonly string _fastbootPath;

        /// <summary>
        /// <para>创建 Fastboot 协议处理器。</para>
        /// Creates the Fastboot protocol handler.
        /// </summary>
        public FastbootProtocolHandler(IUnifiedLog log, string fastbootPath = "fastboot")
        {
            _log = log;
            _fastbootPath = fastbootPath;
        }

        /// <inheritdoc />
        public DeviceProtocol Protocol => DeviceProtocol.Fastboot;

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            // fastboot 没有 devices 命令；通过 getvar product 探测。这里依赖宿主 USB 枚举，
            // 简化返回空，实际设备识别由 DeviceManager 的 ConnectAsync 路径触发。
            await Task.CompletedTask.ConfigureAwait(false);
            return System.Array.Empty<string>();
        }

        /// <inheritdoc />
        public async Task<bool> IsPresentAsync(string serial, CancellationToken cancellationToken = default)
        {
            var res = await ProcessRunner.RunAsync(_fastbootPath, $"-s {serial} getvar product", _log, "Fastboot", 5000, cancellationToken).ConfigureAwait(false);
            return res.Success && !res.Output.Contains("no devices", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public Task ConnectAsync(string serial, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Debug, "Fastboot", $"Connecting to {serial}");
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> ExecuteAsync(string serial, string command, CancellationToken cancellationToken = default)
        {
            return ProcessRunner.RunAsync(_fastbootPath, $"-s {serial} {command}", _log, "Fastboot", 30000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> FlashPartitionAsync(string serial, string partition, string imagePath, CancellationToken cancellationToken = default)
        {
            return ProcessRunner.RunAsync(_fastbootPath, $"-s {serial} flash {partition} \"{imagePath}\"", _log, "Fastboot", 120000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> ErasePartitionAsync(string serial, string partition, CancellationToken cancellationToken = default)
        {
            return ProcessRunner.RunAsync(_fastbootPath, $"-s {serial} erase {partition}", _log, "Fastboot", 30000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> RebootAsync(string serial, RebootMode mode, CancellationToken cancellationToken = default)
        {
            var arg = mode switch
            {
                RebootMode.Normal => "reboot",
                RebootMode.Recovery => "reboot recovery",
                RebootMode.Fastbootd => "reboot fastboot",
                RebootMode.Sideload => "reboot sideload",
                RebootMode.Edl => "reboot edl",
                _ => "reboot"
            };
            return ProcessRunner.RunAsync(_fastbootPath, $"-s {serial} {arg}", _log, "Fastboot", 10000, cancellationToken);
        }

        /// <inheritdoc />
        public void PopulateCapabilities(DeviceInfo info)
        {
            info.Capabilities.SupportsFastbootFlash = true;
            info.Capabilities.SupportsPartitionErase = true;
            info.Capabilities.SupportsRebootModes = true;
            info.Capabilities.SupportsOtaSideload = false;
            info.Capabilities.SupportsAdbShell = false;
        }
    }
}
