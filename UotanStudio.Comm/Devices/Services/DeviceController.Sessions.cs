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
    /// <param name="snapshot">
    /// The live snapshot the endpoint was taken from; a stale snapshot is rejected. <para>端点所来自的当前快照；过期快照会被拒绝。</para>
    /// </param>
    /// <param name="id">
    /// Exact endpoint identity to lease. <para>待租用的精确端点身份。</para>
    /// </param>
    /// <param name="kind">
    /// Session kind selecting the protocol factory. <para>用于选择协议工厂的会话类型。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the open before the transport is acquired; a leased endpoint is released on cancellation.
    /// <para>在传输取得之前取消开启；取消时已租用的端点会被归还。</para>
    /// </param>
    /// <returns>The lease owning the transport and the built session. <para>持有传输与已构建会话的租约。</para></returns>
    /// <exception cref="InvalidOperationException">
    /// The snapshot is stale, the endpoint is already leased, or the opener returned no transport.
    /// <para>快照过期、端点已被租用，或开启器未返回传输。</para></exception>
    /// <exception cref="KeyNotFoundException">
    /// The endpoint is not present in the snapshot. <para>快照中不存在该端点。</para></exception>
    /// <exception cref="NotSupportedException">
    /// No session factory is registered for <paramref name="kind"/>. <para>未为 <paramref name="kind"/> 注册会话工厂。</para></exception>
    /// <exception cref="ObjectDisposedException">
    /// The controller has been disposed. <para>控制器已被释放。</para></exception>
    public async Task<DeviceLease> OpenAsync(DeviceSnapshot snapshot, DeviceEndpointId id,
        DeviceSessionKind kind, CancellationToken cancellationToken = default)
    {
        var (endpoint, transport, generation) = await OpenTransportAsync(snapshot, id, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_factories.TryGetValue(kind, out var factory))
            {
                throw new NotSupportedException($"No session factory is registered for {kind} sessions.");
            }

            var session = await factory.CreateAsync(endpoint, transport, cancellationToken).ConfigureAwait(false);
            DeviceLease? lease = null;
            lease = new DeviceLease(endpoint, transport, session, () => Release(id, lease!), generation);
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
    /// Opens the USB device by its stable key and leases it with the requested protocol session; returns null when the key cannot be opened. The server-ownership guard fails closed inside the native open, and sessions are built only for the protocol observed on the endpoint.
    /// <para>按稳定键打开 USB 设备并以指定协议会话租用，键无法打开时返回 null。server 所有权守卫在原生打开内部失败即关闭，且仅对端点上观测到的协议构建会话。</para>
    /// </summary>
    /// <param name="deviceKey">
    /// Nonempty stable device key of the physical node to open. <para>待打开物理节点的非空稳定设备键。</para>
    /// </param>
    /// <param name="kind">
    /// Session kind selecting the protocol factory. <para>用于选择协议工厂的会话类型。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before opening and passed to the session factory. <para>开启前检查，并传递给会话工厂。</para>
    /// </param>
    /// <returns>The lease, or <see langword="null"/> when no device matches the key.
    /// <para>租约；没有设备匹配该键时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="deviceKey"/> is null, empty, or whitespace. <para><paramref name="deviceKey"/> 为 null、空白或仅含空白字符。</para></exception>
    /// <exception cref="NotSupportedException">
    /// The controller has no communication kit, or no factory is registered for <paramref name="kind"/>.
    /// <para>控制器没有通信套件，或未为 <paramref name="kind"/> 注册工厂。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// The synthesized endpoint is already leased. <para>合成的端点已被租用。</para></exception>
    /// <exception cref="ObjectDisposedException">
    /// The controller has been disposed. <para>控制器已被释放。</para></exception>
    public async Task<DeviceLease?> OpenLeaseByKeyAsync(string deviceKey, DeviceSessionKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        ThrowIfDisposed();
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
            lease = new DeviceLease(endpoint, transport, session, () => Release(endpoint.Id, lease!), CurrentGenerationFor(info));
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