using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Devices;
using UotanStudio.Abstractions.Logging;

namespace UotanStudio.Services.Devices
{
    /// <summary>
    /// <para>设备管理器实现。统一兼容 ADB、Fastboot、Fastbootd、Recovery、Qualcomm EDL、MTK COM、Rockchip BootROM 等通讯协议。</para>
    /// Device manager implementation. Uniformly compatible with ADB, Fastboot, Fastbootd, Recovery, Qualcomm EDL, MTK COM, Rockchip BootROM protocols.
    /// </summary>
    /// <remarks>
    /// <para>AOT 友好：无反射；外部工具进程通过 <see cref="Process"/> 启动，串口设备由 <c>SerialProtocolHandler</c> 抽象。</para>
    /// AOT-friendly: no reflection; external tool processes are launched via <see cref="Process"/>, serial devices are abstracted by <c>SerialProtocolHandler</c>.
    /// </remarks>
    public sealed class DeviceManager : IDeviceManager
    {
        private readonly IUnifiedLog _log;
        private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DeviceProtocol, IProtocolHandler> _handlers = new();

        /// <summary>
        /// <para>创建设备管理器，注册所有协议处理器。</para>
        /// Creates the device manager, registering all protocol handlers.
        /// </summary>
        public DeviceManager(IUnifiedLog log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            RegisterHandler(new AdbProtocolHandler(_log));
            RegisterHandler(new FastbootProtocolHandler(_log));
            RegisterHandler(new EdlProtocolHandler(_log));
            RegisterHandler(new MtkProtocolHandler(_log));
            RegisterHandler(new RockchipProtocolHandler(_log));
        }

        /// <inheritdoc />
        public IReadOnlyList<DeviceInfo> Devices
        {
            get
            {
                var snapshot = new List<DeviceInfo>();
                foreach (var kv in _devices) snapshot.Add(kv.Value);
                return snapshot;
            }
        }

        /// <inheritdoc />
        public event DeviceListChangedEventHandler? DevicesChanged;

        /// <summary>
        /// <para>注册协议处理器。</para>
        /// Registers a protocol handler.
        /// </summary>
        public void RegisterHandler(IProtocolHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _handlers[handler.Protocol] = handler;
            _log.Write(LogLevel.Debug, "DeviceMgr", $"Registered protocol handler: {handler.Protocol}");
        }

        /// <inheritdoc />
        public async Task EnumerateAsync(CancellationToken cancellationToken = default)
        {
            _log.Write(LogLevel.Info, "DeviceMgr", "Enumerating devices...");

            foreach (var kv in _handlers)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var proto = kv.Key;
                var handler = kv.Value;
                try
                {
                    var ids = await handler.EnumerateAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var id in ids)
                    {
                        if (_devices.ContainsKey(id)) continue;
                        var info = new DeviceInfo
                        {
                            Serial = id,
                            Protocol = proto,
                            State = DeviceConnectionState.Connected,
                            LastUpdatedTicks = DateTime.Now.Ticks
                        };
                        info.Capabilities.SupportedProtocols.Add(proto);
                        _devices[id] = info;
                        DevicesChanged?.Invoke(this, new DeviceListChangedEventArgs { Kind = DeviceChangeKind.Added, Device = info });
                        _log.Write(LogLevel.Info, "DeviceMgr", $"Device discovered: {id} via {proto}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Write(LogLevel.Warn, "DeviceMgr", $"Enumerate {proto} failed: {ex.Message}");
                }
            }

            // 移除已消失设备
            var stale = new List<string>();
            foreach (var kv in _devices)
            {
                var handler = _handlers.GetValueOrDefault(kv.Value.Protocol);
                if (handler == null) { stale.Add(kv.Key); continue; }
                try
                {
                    var still = await handler.IsPresentAsync(kv.Key, cancellationToken).ConfigureAwait(false);
                    if (!still) stale.Add(kv.Key);
                }
                catch
                {
                    stale.Add(kv.Key);
                }
            }
            foreach (var s in stale)
            {
                if (_devices.TryRemove(s, out var info))
                {
                    DevicesChanged?.Invoke(this, new DeviceListChangedEventArgs { Kind = DeviceChangeKind.Removed, Device = info });
                    _log.Write(LogLevel.Info, "DeviceMgr", $"Device removed: {s}");
                }
            }
        }

        /// <inheritdoc />
        public async Task<DeviceInfo?> ConnectAsync(string serial, DeviceProtocol protocol, CancellationToken cancellationToken = default)
        {
            var handler = _handlers.GetValueOrDefault(protocol);
            if (handler == null)
            {
                _log.Write(LogLevel.Error, "DeviceMgr", $"No handler registered for protocol {protocol}");
                return null;
            }

            var info = new DeviceInfo
            {
                Serial = serial,
                Protocol = protocol,
                State = DeviceConnectionState.Connected,
                LastUpdatedTicks = DateTime.Now.Ticks
            };

            try
            {
                await handler.ConnectAsync(serial, cancellationToken).ConfigureAwait(false);
                info.State = DeviceConnectionState.Ready;
                info.Capabilities.SupportedProtocols.Add(protocol);
                handler.PopulateCapabilities(info);
                _devices[serial] = info;
                DevicesChanged?.Invoke(this, new DeviceListChangedEventArgs { Kind = DeviceChangeKind.CapabilitiesRefreshed, Device = info });
                _log.Write(LogLevel.Info, "DeviceMgr", $"Connected: {serial} via {protocol}, capabilities populated");
                return info;
            }
            catch (Exception ex)
            {
                info.State = DeviceConnectionState.Error;
                _log.Write(LogLevel.Error, "DeviceMgr", $"Connect {serial} ({protocol}) failed: {ex.Message}");
                return info;
            }
        }

        /// <inheritdoc />
        public Task DisconnectAsync(string serial, CancellationToken cancellationToken = default)
        {
            if (_devices.TryRemove(serial, out var info))
            {
                DevicesChanged?.Invoke(this, new DeviceListChangedEventArgs { Kind = DeviceChangeKind.Removed, Device = info });
                _log.Write(LogLevel.Info, "DeviceMgr", $"Disconnected: {serial}");
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<DeviceCommandResult> ExecuteAsync(string serial, string command, CancellationToken cancellationToken = default)
        {
            if (!_devices.TryGetValue(serial, out var info))
            {
                return DeviceCommandResult.Failed($"Device not found: {serial}");
            }

            var handler = _handlers.GetValueOrDefault(info.Protocol);
            if (handler == null)
            {
                return DeviceCommandResult.Failed($"No handler for protocol {info.Protocol}");
            }

            info.State = DeviceConnectionState.Busy;
            DevicesChanged?.Invoke(this, new DeviceListChangedEventArgs { Kind = DeviceChangeKind.StateChanged, Device = info });

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await handler.ExecuteAsync(serial, command, cancellationToken).ConfigureAwait(false);
                return new DeviceCommandResult
                {
                    Success = result.Success,
                    Output = result.Output,
                    Error = result.Error,
                    ExitCode = result.ExitCode,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
            finally
            {
                info.State = DeviceConnectionState.Ready;
                DevicesChanged?.Invoke(this, new DeviceListChangedEventArgs { Kind = DeviceChangeKind.StateChanged, Device = info });
            }
        }

        /// <inheritdoc />
        public async Task<DeviceCommandResult> FlashPartitionAsync(string serial, string partition, string imagePath, CancellationToken cancellationToken = default)
        {
            if (!_devices.TryGetValue(serial, out var info)) return DeviceCommandResult.Failed($"Device not found: {serial}");
            var handler = _handlers.GetValueOrDefault(info.Protocol);
            if (handler == null) return DeviceCommandResult.Failed($"No handler for protocol {info.Protocol}");
            _log.Write(LogLevel.Info, "DeviceMgr", $"Flashing partition '{partition}' from '{imagePath}' to '{serial}'");
            return await handler.FlashPartitionAsync(serial, partition, imagePath, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<DeviceCommandResult> ErasePartitionAsync(string serial, string partition, CancellationToken cancellationToken = default)
        {
            if (!_devices.TryGetValue(serial, out var info)) return DeviceCommandResult.Failed($"Device not found: {serial}");
            var handler = _handlers.GetValueOrDefault(info.Protocol);
            if (handler == null) return DeviceCommandResult.Failed($"No handler for protocol {info.Protocol}");
            _log.Write(LogLevel.Info, "DeviceMgr", $"Erasing partition '{partition}' on '{serial}'");
            return await handler.ErasePartitionAsync(serial, partition, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<DeviceCommandResult> RebootAsync(string serial, RebootMode mode, CancellationToken cancellationToken = default)
        {
            if (!_devices.TryGetValue(serial, out var info)) return DeviceCommandResult.Failed($"Device not found: {serial}");
            var handler = _handlers.GetValueOrDefault(info.Protocol);
            if (handler == null) return DeviceCommandResult.Failed($"No handler for protocol {info.Protocol}");
            _log.Write(LogLevel.Info, "DeviceMgr", $"Rebooting '{serial}' to mode {mode}");
            return await handler.RebootAsync(serial, mode, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public DeviceCapabilities? GetCapabilities(string serial)
        {
            return _devices.TryGetValue(serial, out var info) ? info.Capabilities : null;
        }
    }
}
