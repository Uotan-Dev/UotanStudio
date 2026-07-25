using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Devices;
using UotanStudio.Abstractions.Logging;

namespace UotanStudio.Services.Devices
{
    /// <summary>
    /// <para>Qualcomm EDL（Emergency Download）协议处理器。通过 firehose / sahara 协议与 USB 上的 9008 设备通讯。</para>
    /// Qualcomm EDL (Emergency Download) protocol handler. Communicates with USB 9008 devices via the firehose / sahara protocol.
    /// </summary>
    /// <remarks>
    /// <para>本实现为 AOT 友好的骨架：USB 枚举依赖宿主层，刷写/擦除使用外部工具（如 <c>qfil</c> 或自研 firehose 引擎）。</para>
    /// This is an AOT-friendly skeleton: USB enumeration depends on the host layer; flashing/erasing uses external tools (e.g. <c>qfil</c> or a custom firehose engine).
    /// </remarks>
    public sealed class EdlProtocolHandler : IProtocolHandler
    {
        private readonly IUnifiedLog _log;
        private readonly string _toolPath;

        /// <summary>
        /// <para>创建 EDL 协议处理器。</para>
        /// Creates the EDL protocol handler.
        /// </summary>
        public EdlProtocolHandler(IUnifiedLog log, string toolPath = "qfil")
        {
            _log = log;
            _toolPath = toolPath;
        }

        /// <inheritdoc />
        public DeviceProtocol Protocol => DeviceProtocol.EdlQcom;

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            // 真实场景需通过 libusb / WinUSB 枚举 VID:PID 0x05C6:0x9008
            // 这里仅返回占位，便于上层通过 ConnectAsync 显式连接
            await Task.CompletedTask.ConfigureAwait(false);
            return Array.Empty<string>();
        }

        /// <inheritdoc />
        public async Task<bool> IsPresentAsync(string serial, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            // serial 形如 "usb:05C6:9008:0" 或枚举出的端点 ID
            return true;
        }

        /// <inheritdoc />
        public async Task ConnectAsync(string serial, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Info, "EDL", $"Opening firehose channel to {serial}");
            // 真实实现：发送 NOP / firehose 配置，读取 chip serial / PKH hash
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> ExecuteAsync(string serial, string command, CancellationToken cancellationToken = default)
        {
            // command 形如 "program boot boot.img" 或 "erase userdata"
            return ProcessRunner.RunAsync(_toolPath, $"{command} --serial {serial}", _log, "EDL", 120000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> FlashPartitionAsync(string serial, string partition, string imagePath, CancellationToken cancellationToken = default)
        {
            // firehose 的 program 命令：指定分区表起始扇区与镜像大小
            _log.Write(LogLevel.Info, "EDL", $"Programming partition '{partition}' from '{imagePath}'");
            return ProcessRunner.RunAsync(_toolPath, $"program {partition} \"{imagePath}\" --serial {serial}", _log, "EDL", 600000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> ErasePartitionAsync(string serial, string partition, CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Info, "EDL", $"Erasing partition '{partition}'");
            return ProcessRunner.RunAsync(_toolPath, $"erase {partition} --serial {serial}", _log, "EDL", 60000, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCommandResult> RebootAsync(string serial, RebootMode mode, CancellationToken cancellationToken = default)
        {
            // EDL 重启通过 firehose 的 power 命令
            _log.Write(LogLevel.Info, "EDL", $"Power reset on {serial}");
            return ProcessRunner.RunAsync(_toolPath, $"power reset --serial {serial}", _log, "EDL", 10000, cancellationToken);
        }

        /// <inheritdoc />
        public void PopulateCapabilities(DeviceInfo info)
        {
            info.Manufacturer = "Qualcomm";
            info.Capabilities.SupportsEdl = true;
            info.Capabilities.SupportsFastbootFlash = false; // EDL 模式不能直接 fastboot flash
            info.Capabilities.RequiresUnlock = false; // EDL 通常绕过解锁检查
            info.Capabilities.SupportsRebootModes = false;
            info.Capabilities.KnownPartitions.Add("boot");
            info.Capabilities.KnownPartitions.Add("system");
            info.Capabilities.KnownPartitions.Add("userdata");
            info.Capabilities.KnownPartitions.Add("modem");
            info.Capabilities.SupportedImageFormats.Add("raw");
            info.Capabilities.SupportedImageFormats.Add("sparse");
        }
    }
}
