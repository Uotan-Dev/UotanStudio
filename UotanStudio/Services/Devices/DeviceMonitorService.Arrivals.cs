using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Quality of each arrival signal; a later, more precise descriptor supersedes a coarser path signal for the
    /// same physical device instead of starting a second handshake on the same short-lived port.
    /// <para>各到达信号的质量；同一物理设备后续更精确的描述符会抢占较粗的路径信号，而不是在同一短暂端口上
    /// 发起第二次握手。</para>
    /// </summary>
    private static class ArrivalQuality
    {
        public const int ArrivalPath = 1;
        public const int ComPort = 2;
        public const int Descriptor = 3;
        public const int Inventoried = 4;
    }

    /// <summary>
    /// Builds the bounded handshake and best-effort detail work for one classified arrival. Only protocols with a
    /// short-lived download port claim the 3s path; debug bridges and ordinary download ports get their identity
    /// refreshed asynchronously after the handshake settles.
    /// <para>为一次已分类到达构建有界握手与尽力详情工作。只有短暂下载端口的协议占用 3s 路径；调试桥与普通
    /// 下载端口在握手结束后异步刷新身份。</para>
    /// </summary>
    private DeviceArrivalWork? BuildArrivalWork(DeviceArrival arrival)
    {
        if (arrival.Protocol is null)
        {
            return null;
        }

        return arrival.Protocol switch
        {
            DeviceProtocolKind.Mtk => BuildMtkArrivalWork(arrival),
            DeviceProtocolKind.Unisoc => BuildUnisocArrivalWork(arrival),
            DeviceProtocolKind.Edl => BuildEdlArrivalWork(arrival),
            _ => new DeviceArrivalWork(_ => Task.CompletedTask,
                _ => RefreshDeviceListAsync()),
        };
    }

    private DeviceArrivalWork BuildMtkArrivalWork(DeviceArrival arrival)
    {
        async Task HandshakeAsync(CancellationToken token)
        {
            // A bare arrival path or COM port carries no USB descriptors: handshake it on that exact path first,
            // because a serial-bound preloader publishes no openable USB interface at all.
            // 裸到达路径或 COM 口没有 USB 描述符：先在该精确路径上握手，因为串口绑定的 preloader
            // 完全没有可打开的 USB 接口。
            if (arrival.FallbackKey is { } fallback)
            {
                var lease = LooksLikeComPort(fallback)
                    ? await _controller.HandshakeMtkSerialPortAsync(fallback, AutomaticMtkBaudRate, token)
                        .ConfigureAwait(false)
                    : await _controller.HandshakeMtkArrivalPathAsync(fallback, AutomaticMtkBaudRate, token)
                        .ConfigureAwait(false);
                if (lease is not null)
                {
                    RememberMtkIdentity(arrival.StableKey, lease);
                    if (!LooksLikeComPort(fallback))
                    {
                        await _mtkConnections.ParkArrivalLeaseAsync(lease, fallback, token).ConfigureAwait(false);
                    }
                    return;
                }

                // A bare path is not a USB device key. Falling through to HandshakeAsync would hand this raw
                // path to OpenLeaseByKeyAsync and fail with a malformed-key ArgumentException. The descriptor
                // arrival carries the real key and will handshake it; this coarse signal simply yields.
                // 裸路径不是 USB 设备键。继续走 HandshakeAsync 会把该原始路径交给 OpenLeaseByKeyAsync，
                // 以键格式错误告败。携带真实键的描述符到达会完成握手，这一粗粒度信号直接让位。
                return;
            }

            var device = new DetectedDevice
            {
                Key = arrival.StableKey,
                Serial = arrival.StableKey,
                Protocol = DeviceProtocolKind.Mtk,
                Transport = DeviceTransportKind.Usb,
            };

            // A USB descriptor arrival can still be VCOM-bound: try its serial port before the exact USB key.
            // 带 USB 描述符的到达也可能是串口绑定：先试串口，再回退到精确 USB 键。
            var info = FindUsbInfoByKey(arrival.StableKey);
            if (info is not null)
            {
                var vcom = await _mtkConnections.HandshakeVcomAsync(info, device, AutomaticMtkBaudRate, token)
                    .ConfigureAwait(false);
                if (vcom is not null)
                {
                    RememberMtkIdentity(arrival.StableKey, vcom);
                    return;
                }
            }

            var handshaken = await _mtkConnections.HandshakeAsync(device, token, info).ConfigureAwait(false);
            RememberMtkIdentity(arrival.StableKey, handshaken);
        }

        return new DeviceArrivalWork(HandshakeAsync, _ => RefreshDeviceListAsync());
    }

    private void RememberMtkIdentity(string stableKey, DeviceLease lease)
    {
        if (lease.TryGetSession<MtkSession>(out var session) && session.DeviceInfo is { } identity)
        {
            _mtkHandshakeIdentities[stableKey] = identity;
        }
    }

    private DeviceArrivalWork BuildUnisocArrivalWork(DeviceArrival arrival)
    {
        async Task HandshakeAsync(CancellationToken token)
        {
            // A raw arrival path names the transient download port before the inventory walk. Handshake it on
            // that exact path first, mirroring the MTK arrival path, so a serial-bound gadget is caught inside
            // its short window instead of waiting for a full scan.
            // 裸到达路径在清单遍历之前就给出了短暂下载端口：先在该精确路径上握手（镜像 MTK 到达路径），
            // 使串口绑定设备在其短窗口内被捕获，而不必等待整棵清单扫描。
            if (arrival.FallbackKey is { } fallbackKey)
            {
                var lease = await _controller.HandshakeUnisocArrivalPathAsync(fallbackKey,
                    AutomaticUnisocBaudRate, token).ConfigureAwait(false);
                if (lease is null)
                {
                    // No transport shape answered; reporting success here would make the arrival pipeline claim a
                    // handshake it never performed and skip the slower inventory-driven probe that can still win.
                    throw new IOException(
                        $"Unisoc arrival path '{fallbackKey}' answered neither the serial nor the USB shape.");
                }

                lease.Dispose();
                return;
            }

            var device = new DetectedDevice
            {
                Key = arrival.StableKey,
                Serial = arrival.StableKey,
                Protocol = DeviceProtocolKind.Unisoc,
                Transport = DeviceTransportKind.Usb,
            };
            // Opening a UNISOC session runs the stage handshake, which is the act that keeps a download port from
            // rolling back into the application. The identity it captures is written back by the detail pass.
            // 打开紫光会话会执行阶段握手，正是该动作阻止下载端口退回应用态；其捕获的身份由详情阶段写回。
            var status = await QueryUnisocStatusAsync(device, token).ConfigureAwait(false);
            // The detail snapshot is published through the scan; an arrival has no inventoried endpoint yet to
            // attach these properties to, so they stay on the session identity rather than a stale endpoint.
            _ = status;
        }

        return new DeviceArrivalWork(HandshakeAsync, _ => RefreshDeviceListAsync());
    }

    private DeviceArrivalWork BuildEdlArrivalWork(DeviceArrival arrival)
    {
        async Task HandshakeAsync(CancellationToken token)
        {
            var device = new DetectedDevice
            {
                Key = arrival.StableKey,
                Serial = arrival.StableKey,
                Protocol = DeviceProtocolKind.Edl,
                Transport = DeviceTransportKind.Usb,
            };
            // ProbeAsync parks the lease, classifies Sahara/Firehose and collects its detail snapshot in one pass.
            // A slow bootrom that does not answer inside the budget is reported Unknown and resumed asynchronously
            // by the existing scan-driven probe, never blocking the device list.
            // ProbeAsync 一次完成租约暂存、Sahara/Firehose 分类与详情采集；预算内不应答的慢速 bootrom
            // 上报 Unknown，由现有扫描探测异步继续，绝不阻塞设备列表。
            await ProbeEdlProtocolAsync(device, token).ConfigureAwait(false);
        }

        return new DeviceArrivalWork(HandshakeAsync, _ => RefreshDeviceListAsync());
    }

    private static bool LooksLikeComPort(string key)
        => key.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("/dev/tty", StringComparison.Ordinal)
            || key.StartsWith("/dev/cu.", StringComparison.Ordinal);

    private UsbDeviceInfo? FindUsbInfoByKey(string key)
    {
        foreach (var info in ListUsbDevices())
        {
            if (string.Equals(info.DeviceKey, key, StringComparison.Ordinal))
            {
                return info;
            }
        }
        return null;
    }

    private Task RefreshDeviceListAsync()
    {
        ScheduleChange();
        return Task.CompletedTask;
    }

    private Task OnArrivalResult(DeviceArrivalResult result)
    {
        var level = result.HandshakeCompleted ? LogLevel.Info : LogLevel.Debug;
        LogService.Instance.Write(level, "DeviceMonitor",
            $"Arrival '{result.StableKey}' ({result.Protocol?.ToString() ?? "unknown"}): " +
            $"handshake={(result.HandshakeCompleted ? "ok" : "no")} in {result.HandshakeMilliseconds} ms, " +
            $"details={(result.DetailsCompleted ? "ok" : "skipped")}" +
            (string.IsNullOrEmpty(result.Reason) ? string.Empty : $" ({result.Reason})"));
        return Task.CompletedTask;
    }

    private void OnArrivalFault(Exception exception)
        => LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor",
            $"Arrival pipeline fault: {exception.GetType().Name}: {exception.Message}");

    /// <summary>Submits a USB arrival carrying full descriptors to the pipeline.</summary>
    internal void OnDeviceArrived(UsbDeviceInfo info, int quality)
    {
        var protocol = Classify(info);
        if (protocol is null)
        {
            return;
        }

        _ = _arrivalPipeline.Submit(new DeviceArrival(
            info.DeviceKey,
            protocol,
            quality,
            NeedsHandshake(protocol.Value),
            null));
    }

    /// <summary>Submits an OS arrival path to the pipeline using the canonical VID/PID classification.</summary>
    internal void OnArrivalPathArrived(string devicePath)
    {
        var token = UotanStudio.Comm.Devices.Discovery.UsbProtocolClassifier.ClassifyPath(devicePath);
        var protocol = ProtocolKindFromToken(token);
        if (protocol is null || !NeedsHandshake(protocol.Value))
        {
            return;
        }

        _ = _arrivalPipeline.Submit(new DeviceArrival(
            devicePath,
            protocol,
            ArrivalQuality.ArrivalPath,
            true,
            null,
            devicePath));
    }

    /// <summary>Submits a bare COM port arrival using the canonical PnP hardware-id classification.</summary>
    internal void OnComPortArrivedForPipeline(string port)
    {
        string? token = null;
        if (OperatingSystem.IsWindows())
        {
            token = UotanStudio.Comm.Transports.Usb.Backend.Windows.FastBootPortEnumerator.ClassifyPort(port);
        }

        var protocol = ProtocolKindFromToken(token);
        if (protocol is null || !NeedsHandshake(protocol.Value))
        {
            return;
        }

        _ = _arrivalPipeline.Submit(new DeviceArrival(
            port,
            protocol,
            ArrivalQuality.ComPort,
            true,
            null,
            port));
    }

    private static bool NeedsHandshake(DeviceProtocolKind protocol) => protocol is
        DeviceProtocolKind.Mtk or DeviceProtocolKind.Unisoc or DeviceProtocolKind.Edl;
}