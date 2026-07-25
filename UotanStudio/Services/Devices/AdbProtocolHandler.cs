using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Devices;
using UotanStudio.Abstractions.Logging;

namespace UotanStudio.Services.Devices
{
    /// <summary>
    /// <para>ADB 协议处理器。通过 <c>adb</c> 可执行文件枚举、连接、执行 shell、推送/拉取文件等。</para>
    /// ADB protocol handler. Uses the <c>adb</c> executable to enumerate, connect, run shell, push/pull files, etc.
    /// </summary>
    public sealed class AdbProtocolHandler : IProtocolHandler
    {
        private readonly IUnifiedLog _log;
        private readonly string _adbPath;

        /// <summary>
        /// <para>创建 ADB 协议处理器。</para>
        /// Creates the ADB protocol handler.
        /// </summary>
        /// <param name="log">
        /// <para>统一日志服务。</para>
        /// Unified log service.
        /// </param>
        /// <param name="adbPath">
        /// <para><c>adb</c> 可执行文件路径，缺省为 <c>adb</c>（依赖 PATH）。</para>
        /// Path to the <c>adb</c> executable; defaults to <c>adb</c> (relies on PATH).
        /// </param>
        public AdbProtocolHandler(IUnifiedLog log, string adbPath = "adb")
        {
            _log = log;
            _adbPath = adbPath;
        }

        /// <inheritdoc />
        public DeviceProtocol Protocol => DeviceProtocol.Adb;

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            var res = await ProcessRunner.RunAsync(_adbPath, "devices", _log, "ADB", 5000, cancellationToken).ConfigureAwait(false);
            if (!res.Success) return System.Array.Empty<string>();

            var serials = new System.Collections.Generic.List<string>();
            foreach (var line in res.Output.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("List of devices")) continue;
                var parts = trimmed.Split(System.Array.Empty<char>(), System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1] == "device")
                {
                    serials.Add(parts[0]);
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
            // ADB 不需要显式 connect；仅获取设备属性填充元数据
            _log.Write(LogLevel.Debug, "ADB", $"Connecting to {serial}");
            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> ExecuteAsync(string serial, string command, CancellationToken cancellationToken = default)
        {
            // command 形如 "shell ls /" 或 "push src dst"
            return ProcessRunner.RunAsync(_adbPath, $"-s {serial} {command}", _log, "ADB", 30000, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<DeviceCommandResult> FlashPartitionAsync(string serial, string partition, string imagePath, CancellationToken cancellationToken = default)
        {
            // ADB 模式下刷写需要先 reboot bootloader，这里仅占位记录
            _log.Write(LogLevel.Warn, "ADB", $"Flash on ADB mode requires reboot to bootloader: {serial}/{partition}");
            await Task.CompletedTask.ConfigureAwait(false);
            return DeviceCommandResult.Failed("Flash not supported in ADB mode; reboot to bootloader first");
        }

        /// <inheritdoc />
        public async Task<DeviceCommandResult> ErasePartitionAsync(string serial, string partition, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Warn, "ADB", $"Erase on ADB mode requires reboot to bootloader: {serial}/{partition}");
            await Task.CompletedTask.ConfigureAwait(false);
            return DeviceCommandResult.Failed("Erase not supported in ADB mode");
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> RebootAsync(string serial, RebootMode mode, CancellationToken cancellationToken = default)
        {
            var arg = mode switch
            {
                RebootMode.Bootloader => "reboot bootloader",
                RebootMode.Recovery => "reboot recovery",
                RebootMode.Sideload => "reboot sideload",
                RebootMode.Fastbootd => "reboot fastboot",
                RebootMode.Edl => "reboot edl",
                _ => "reboot"
            };
            return ProcessRunner.RunAsync(_adbPath, $"-s {serial} {arg}", _log, "ADB", 10000, cancellationToken);
        }

        /// <inheritdoc />
        public void PopulateCapabilities(DeviceInfo info)
        {
            info.Manufacturer = "Unknown";
            info.Capabilities.SupportsAdbShell = true;
            info.Capabilities.SupportsRebootModes = true;
            info.Capabilities.SupportsFastbootFlash = false; // ADB 模式不能直接 fastboot flash
            info.Capabilities.SupportsOtaSideload = true;
        }
    }
}
