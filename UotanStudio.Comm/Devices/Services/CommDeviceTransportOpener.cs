using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Opens exactly the discovered USB key or server target; never changes routes.
/// <para>只打开已发现的 USB 键或 server 目标；绝不改变路由。</para>
/// </summary>
/// <param name="kit">The kit used to open USB device sessions by key. <para>用于按键打开 USB 设备会话的套件。</para></param>
/// <param name="adbHost">Host of the adb server endpoint to match. <para>待匹配的 adb server 端点主机。</para></param>
/// <param name="adbPort">Port of the adb server endpoint to match. <para>待匹配的 adb server 端点端口。</para></param>
/// <param name="hdcHost">Host of the HDC server endpoint to match. <para>待匹配的 HDC server 端点主机。</para></param>
/// <param name="hdcPort">Port of the HDC server endpoint; null uses the default. <para>HDC server 端点端口；null 使用默认端口。</para></param>
internal sealed class CommDeviceTransportOpener(
    ICommKit kit, string adbHost = "127.0.0.1", int adbPort = 5037,
    string hdcHost = "127.0.0.1", int? hdcPort = null) : IDeviceTransportOpener
{
    private readonly int _hdcPort = hdcPort ?? HdcServerClient.DefaultServerPort;

    /// <summary>Opens exactly the endpoint's own transport without changing routes; an <c>edl</c> endpoint whose device was bound as a COM port falls back to that serial port.
    /// <para>只打开端点自身的传输且不改变路由；设备已被驱动绑定为 COM 口的 <c>edl</c> 端点回退到该串口。</para></summary>
    /// <param name="endpoint">The endpoint produced by discovery; anything unrecognized is rejected.
    /// <para>发现层产出的端点；任何无法识别的输入都会被拒绝。</para></param>
    /// <param name="cancellationToken">Cancels the open; a validation already in flight keeps its own bounded socket timeouts.
    /// <para>取消打开过程；已在执行的校验保留其自身有界的套接字超时。</para></param>
    /// <returns>An owned transport bound to the exact endpoint.<para>绑定到该精确端点的受管传输。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.
    /// <para><paramref name="endpoint"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="IOException">The route precondition, the server probe, or the target validation failed.
    /// <para>路由前置条件、server 探测或目标校验失败。</para></exception>
    /// <exception cref="NotSupportedException">The endpoint matches none of the configured opener routes.
    /// <para>端点不匹配任何已配置的开启器路由。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        if (endpoint.Source == UsbDiscoverySource.SourceName && endpoint.Route == "direct-usb" &&
            endpoint.Usb is { } usb && usb.Backend != UsbApiKind.Auto &&
            endpoint.Transport == usb.Backend.ToString() &&
            UsbDeviceIdentity.TryParseKeyFilter(endpoint.Address) is { } filter &&
            !string.IsNullOrWhiteSpace(filter.DevicePathEquals) &&
            string.Equals(filter.DevicePathEquals, usb.Path, StringComparison.OrdinalIgnoreCase) &&
            filter.VendorId == usb.VendorId && filter.ProductId == usb.ProductId &&
            filter.InterfaceClass == usb.InterfaceClass &&
            filter.InterfaceSubClass == usb.InterfaceSubClass &&
            filter.InterfaceProtocol == usb.InterfaceProtocol)
        {
            DebugBridgeSessionOpener.RequireDirectUsb(usb.InterfaceClass, usb.InterfaceSubClass, usb.InterfaceProtocol, endpoint.Serial);
            var session = kit.OpenUsbDeviceSessionByKey(endpoint.Address, usb.Backend);
            if (session is null)
            {
                if (string.Equals(endpoint.Protocol, "edl", StringComparison.Ordinal) &&
                    EdlSerialPortFallback.TryOpenConnected(usb.Path) is { } serial)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        serial.Dispose();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    return new OwnedDeviceTransport<SerialTransport>(serial);
                }
                throw new IOException(DescribeUnopenableEndpoint(endpoint, usb));
            }
            if (session.DeviceInfo.SourceApiKind != usb.Backend ||
                !UsbDeviceIdentity.MatchesOpenedKey(endpoint.Address, session.DeviceInfo.DeviceKey))
            {
                session.Dispose();
                throw new IOException("USB opener returned a different endpoint.");
            }
            if (cancellationToken.IsCancellationRequested)
            {
                session.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return new OwnedDeviceTransport<IUsbDeviceSession>(session);
        }

        if (endpoint.Source == $"adb-server@{adbHost}:{adbPort}" &&
            endpoint.Transport == "adb-server" && endpoint.Route == "server" &&
            !string.IsNullOrWhiteSpace(endpoint.Address) && endpoint.Serial == endpoint.Address)
        {
            if (!DebugBridgeRoute.UseAdbServer(adbHost, adbPort))
                throw new IOException("ADB server is unavailable.");
            var validation = Task.Run(() =>
                new AdbServerServices(endpoint.Address, adbHost, adbPort).ValidateTarget());
            try
            {
                await validation.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = validation.ContinueWith(static task =>
                {
                    _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new OwnedDeviceTransport<AdbSession>(
                AdbSession.ForServer(endpoint.Address, adbHost, adbPort));
        }

        if (endpoint.Source == $"hdc-server@{hdcHost}:{_hdcPort}" &&
            endpoint.Transport == "hdc-server" && endpoint.Route == "server" &&
            !string.IsNullOrWhiteSpace(endpoint.Address) && endpoint.Serial == endpoint.Address)
        {
            if (!DebugBridgeRoute.UseHdcServer(hdcHost, _hdcPort))
                throw new IOException("HDC server is unavailable.");
            using var client = new HdcServerClient(hdcHost, _hdcPort);
            var output = await client.ExecuteAsync(string.Empty, "list targets -v",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!HdcTargetInfo.Parse(Encoding.UTF8.GetString(output))
                .Any(target => string.Equals(target.ConnectKey, endpoint.Address, StringComparison.Ordinal)))
                throw new IOException("Exact HDC server target is unavailable.");
            cancellationToken.ThrowIfCancellationRequested();
            return new OwnedDeviceTransport<HdcSession>(client.CreateSession(endpoint.Address));
        }

        throw new NotSupportedException("Endpoint route is not configured for this opener.");
    }

    /// <summary>
    /// Explains why an enumerated USB endpoint could not be opened: the device disappeared between discovery and the
    /// open, or it is still present while no backend could open it — the usual cause being another process that holds
    /// an exclusive handle to the port, which a flasher-mode device rejects a second open on.
    /// <para>说明一个已枚举 USB 端点为何无法打开：设备在发现与打开之间消失，或设备仍在位但没有任何后端能打开它——
    /// 最常见的原因是另一个进程以独占方式持有该端口的句柄，处于刷机模式的设备会拒绝第二次打开。</para>
    /// </summary>
    /// <param name="endpoint">The endpoint that failed to open. <para>打开失败的端点。</para></param>
    /// <param name="usb">The USB details recorded at discovery. <para>发现时记录的 USB 详细信息。</para></param>
    /// <returns>The message carried by the thrown <see cref="IOException"/>. <para>抛出的 <see cref="IOException"/> 携带的消息。</para></returns>
    private string DescribeUnopenableEndpoint(DeviceEndpoint endpoint, UsbEndpointDetails usb)
    {
        var identity = $"USB device {usb.VendorId:X4}:{usb.ProductId:X4} at '{usb.Path}'";
        IReadOnlyList<UsbDeviceInfo> present;
        try
        {
            present = kit.EnumerateUsbDevices(usb.Backend);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return $"{identity} could not be opened through the {usb.Backend} backend and the device list could not be " +
                $"re-read to tell whether it is still connected ({ex.Message}).";
        }

        var stillPresent = present.Any(candidate =>
            string.Equals(candidate.DevicePath, usb.Path, StringComparison.OrdinalIgnoreCase));
        if (!stillPresent)
        {
            return $"{identity} is no longer connected; reconnect it and retry " +
                $"({endpoint.Protocol} endpoint).";
        }

        var instanceId = present
            .First(candidate => string.Equals(candidate.DevicePath, usb.Path, StringComparison.OrdinalIgnoreCase))
            .DeviceInstanceId;
        var instance = string.IsNullOrWhiteSpace(instanceId) ? string.Empty : $" (instance '{instanceId}')";
        return $"{identity}{instance} is still connected but no {usb.Backend} backend could open it: another process " +
            $"most likely holds an exclusive handle to this port. Close the other flasher or debug tool and retry " +
            $"({endpoint.Protocol} endpoint).";
    }
}