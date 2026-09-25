using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Services;

public sealed partial class DeviceController
{
    /// <summary>
    /// Leases the exact endpoint and builds the requested protocol session on top of the opened transport.
    /// <para>租用精确端点，并在已打开的传输之上构建所请求的协议会话。</para>
    /// The kind selects only the session factory; route decisions and identity validation happened in
    /// discovery and the transport opener, and a factory failure never falls back to another route.
    /// <para>会话类型仅选择会话工厂；路由决策与身份校验已在发现和传输开启阶段完成，
    /// 工厂失败绝不会回退到其他路由。</para>
    /// </summary>
    public async Task<DeviceLease> OpenAsync(DeviceSnapshot snapshot, DeviceEndpointId id,
        DeviceSessionKind kind, CancellationToken cancellationToken = default)
    {
        var (endpoint, transport) = await OpenTransportAsync(snapshot, id, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_factories.TryGetValue(kind, out var factory))
            {
                throw new NotSupportedException($"No session factory is registered for {kind} sessions.");
            }

            var session = await factory.CreateAsync(endpoint, transport, cancellationToken).ConfigureAwait(false);
            DeviceLease? lease = null;
            lease = new DeviceLease(endpoint, transport, session, () => Release(id, lease!));
            lock (_gate)
            {
                _openLeases[id] = lease;
            }

            return lease;
        }
        catch
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Release(id, null);
            }

            throw;
        }
    }

    /// <summary>
    /// <para>按稳定设备键直开 USB 并租约化所请求协议的会话；键无法打开时返回 <c>null</c>。
    /// 端点占用按与发现源同构的身份登记；server 归属守卫在原生打开路径内 fail-closed，
    /// 会话工厂只按端点上观测到的协议构建会话，失败绝不回退其他路由。</para>
    /// Opens the USB device by its stable key and leases it with the requested protocol session;
    /// returns <c>null</c> when the key cannot be opened. The endpoint reservation uses the same
    /// identity shape as discovery, the server-ownership guard fails closed inside the native open,
    /// and the session factory only builds sessions for the protocol observed on the endpoint.
    /// </summary>
    public async Task<DeviceLease?> OpenLeaseByKeyAsync(string deviceKey, DeviceSessionKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (_kit is null)
        {
            throw new NotSupportedException("This controller was composed without a communication kit and cannot open by key.");
        }

        var usb = _kit.OpenUsbDeviceSessionByKey(deviceKey);
        if (usb is null)
        {
            return null;
        }

        var info = usb.DeviceInfo;
        var endpoint = new DeviceEndpoint
        {
            Source = UsbDiscoverySource.SourceName,
            Transport = info.SourceApiKind.ToString(),
            Address = deviceKey,
            DisplayName = info.SerialNumber,
            Serial = info.SerialNumber,
            Route = "direct-usb",
            Protocol = UsbProtocolClassifier.Classify(info),
            UsbInfo = info,
            Usb = new(info.SourceApiKind, info.DevicePath, info.VendorId, info.ProductId,
                info.InterfaceClass, info.InterfaceSubClass, info.InterfaceProtocol)
        };
        lock (_gate)
        {
            if (!_leased.Add(endpoint.Id))
            {
                usb.Dispose();
                throw new InvalidOperationException("Endpoint is already leased.");
            }
        }

        IAsyncDisposable transport = new OwnedDeviceTransport<IUsbDeviceSession>(usb);
        try
        {
            if (!_factories.TryGetValue(kind, out var factory))
            {
                throw new NotSupportedException($"No session factory is registered for {kind} sessions.");
            }

            var session = await factory.CreateAsync(endpoint, transport, cancellationToken).ConfigureAwait(false);
            DeviceLease? lease = null;
            lease = new DeviceLease(endpoint, transport, session, () => Release(endpoint.Id, lease!));
            lock (_gate)
            {
                _openLeases[endpoint.Id] = lease;
            }

            return lease;
        }
        catch
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Release(endpoint.Id, null);
            }

            throw;
        }
    }
}