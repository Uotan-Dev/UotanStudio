using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Services;

public sealed partial class DeviceController
{
    /// <summary>Waits for a released MTK, EDL, Fastboot or Rockchip bootrom (RKDL) USB endpoint to
    /// disappear and opens its uniquely matched replacement.
    /// <para>等待已释放的 MTK、EDL、Fastboot 或 Rockchip bootrom（RKDL）USB 端点消失，并打开唯一匹配的替代端点。</para></summary>
    /// <remarks>The previous lease must be disposed before calling. This method never sends a reboot,
    /// changes USB speed, uploads an agent or replays a command. It requires a nonempty OS container ID,
    /// preserves the USB backend, and validates that identity again on the opened handle before creating
    /// a session. Missing container metadata, ambiguous interfaces, stale snapshots and open failures
    /// are not retried or replaced with serial, VID/PID or enumeration-order guesses. A returned MTK
    /// session is uninitialized; reconnecting alone does not recover a running DA's protocol state.
    /// Do not call from inside the previous lease's command dispatcher.
    /// <para>调用前必须释放旧租约。本方法不发送重启、不更改 USB 速度、不上传代理，也不重放命令。
    /// 必须具有非空的操作系统容器 ID，保持 USB 后端不变，并在创建会话前再次核验已打开句柄的身份。
    /// 缺失容器元数据、接口歧义、过期快照和打开失败均不重试，也不改用序列号、VID/PID 或枚举顺序猜测。
    /// 返回的 MTK 会话未初始化；仅重连不会恢复运行中 DA 的协议状态。不得在旧租约的命令调度器内部调用。</para></remarks>
    /// <param name="previous">The endpoint captured from the previous lease, including observed USB container metadata.
    /// <para>从旧租约捕获的端点，包括已观测的 USB 容器元数据。</para></param>
    /// <param name="kind">The explicitly expected MTK, EDL, Fastboot or Rockchip bootrom session kind.
    /// <para>显式预期的 MTK、EDL、Fastboot 或 Rockchip bootrom 会话类型。</para></param>
    /// <param name="timeoutMs">The cooperative discovery and open deadline, from 1 ms to 24 hours.
    /// <para>发现与打开的协作式截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for discovery, waiting and opening.
    /// <para>发现、等待与打开的取消令牌。</para></param>
    /// <returns>A caller-owned lease for the uniquely identified replacement endpoint.
    /// <para>由调用方持有的唯一识别替代端点租约。</para></returns>
    /// <exception cref="NotSupportedException">The endpoint lacks stable container evidence or the session kind is unsupported.
    /// <para>端点缺少稳定的容器证据或不支持该会话类型。</para></exception>
    /// <exception cref="InvalidOperationException">An open lease still holds the previous endpoint or any endpoint
    /// of the same physical device, several endpoints match, or a snapshot is stale.
    /// <para>仍有活动租约持有旧端点或同一物理设备上的任一端点、多个端点匹配或快照已过期。</para></exception>
    /// <exception cref="IOException">The opened USB handle does not match the selected endpoint and container.
    /// <para>已打开的 USB 句柄与所选端点和容器不匹配。</para></exception>
    /// <exception cref="TimeoutException">The cooperative deadline expired without returning a replacement.
    /// <para>协作式截止时间已到，未能返回替代端点。</para></exception>
    /// <exception cref="ObjectDisposedException">The controller has been disposed.
    /// <para>控制器已被释放。</para></exception>
    public Task<DeviceLease> WaitForUsbReconnectAsync(DeviceEndpoint previous, DeviceSessionKind kind,
        int timeoutMs = 30000, CancellationToken cancellationToken = default) =>
        WaitForUsbReconnectAsync(previous, kind, UsbReconnectIdentityPolicy.Container, timeoutMs, cancellationToken);

    /// <summary>Waits for USB re-enumeration using an explicitly selected container or physical-port policy.
    /// <para>使用显式选择的容器或物理端口策略等待 USB 重枚举。</para></summary>
    /// <remarks>The physical-port policy permits a device without a container ID to reconnect at the same
    /// observed bus and port chain. It cannot distinguish replacement hardware on that port and is not
    /// an authentication mechanism. Neither policy falls back to serial numbers, VID/PID or list order.
    /// The old lease must be released; the old endpoint must disappear; ambiguous matches are rejected.
    /// The opened handle is rechecked before session creation. No protocol commands are sent.
    /// <para>物理端口策略允许没有容器 ID 的设备在相同的已观测总线与端口链上重连。
    /// 它无法区分该端口上被替换的硬件，也不是认证机制。两种策略均不回退到序列号、VID/PID 或列表顺序。
    /// 必须释放旧租约并观测到旧端点消失，歧义匹配会被拒绝。创建会话前重新检查打开的句柄，不发送协议命令。</para></remarks>
    /// <param name="previous">The endpoint captured from the released lease.
    /// <para>从已释放租约捕获的端点。</para></param>
    /// <param name="kind">The explicitly expected MTK, EDL, Fastboot or Rockchip bootrom session.
    /// <para>显式预期的 MTK、EDL、Fastboot 或 Rockchip bootrom 会话。</para></param>
    /// <param name="identityPolicy">The required identity or connection-position evidence.
    /// <para>要求的身份或连接位置证据。</para></param>
    /// <param name="timeoutMs">The cooperative discovery and open deadline, from 1 ms through 24 hours.
    /// <para>发现与打开的协作式截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for discovery, waiting and opening.
    /// <para>发现、等待与打开的取消令牌。</para></param>
    /// <returns>The caller-owned lease for the unique verified replacement.
    /// <para>由调用方持有的唯一经校验替代端点租约。</para></returns>
    public async Task<DeviceLease> WaitForUsbReconnectAsync(DeviceEndpoint previous, DeviceSessionKind kind,
        UsbReconnectIdentityPolicy identityPolicy, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (!Enum.IsDefined(identityPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(identityPolicy));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 86400000);
        var protocol = kind switch
        {
            DeviceSessionKind.Mtk => "mtk",
            DeviceSessionKind.Edl => "edl",
            DeviceSessionKind.Fastboot => "fastboot",
            DeviceSessionKind.Bootrom => "rkdl",
            _ => throw new NotSupportedException("Only explicitly selected MTK, EDL, Fastboot and Rockchip bootrom USB transitions are supported.")
        };
        var previousInfo = previous.UsbInfo;
        if (previous.Source != UsbDiscoverySource.SourceName || previous.Route != "direct-usb"
            || previous.Protocol is not ("mtk" or "edl" or "fastboot" or "rkdl")
            || previous.Usb is not { } usb || usb.Backend == UsbApiKind.Auto
            || previousInfo is null
            || previousInfo.SourceApiKind != usb.Backend || previous.Transport != usb.Backend.ToString()
            || previousInfo.VendorId != usb.VendorId || previousInfo.ProductId != usb.ProductId
            || !string.Equals(previousInfo.DevicePath, usb.Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("USB reconnection requires observed metadata on one explicit native endpoint.");
        }
        if (identityPolicy == UsbReconnectIdentityPolicy.Container
            ? previousInfo.ContainerId is null || previousInfo.ContainerId == Guid.Empty
            : !IsUsableUsbPortPath(previousInfo.PhysicalPortPath))
        {
            throw new NotSupportedException("The previous endpoint lacks the evidence required by the selected USB reconnection policy.");
        }
        if (!_factories.TryGetValue(kind, out var factory))
        {
            throw new NotSupportedException($"No session factory is registered for {kind} sessions.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (HasBlockingLeaseForLocked(previous))
            {
                throw new InvalidOperationException("Release the previous endpoint lease before waiting for reconnection.");
            }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        var disappeared = false;
        try
        {
            while (true)
            {
                var snapshot = await RefreshAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                disappeared |= !snapshot.Endpoints.Any(endpoint => endpoint.Id == previous.Id);
                if (disappeared)
                {
                    var candidates = snapshot.Endpoints.Where(endpoint =>
                        endpoint.Source == previous.Source && endpoint.Route == "direct-usb"
                        && endpoint.Transport == previous.Transport && endpoint.Protocol == protocol
                        && endpoint.Usb?.Backend == usb.Backend
                        && MatchesUsbReconnectEvidence(previousInfo, endpoint.UsbInfo, identityPolicy))
                        .Take(2).ToArray();
                    if (candidates.Length > 1)
                    {
                        throw new InvalidOperationException("Several USB interfaces match the selected reconnection evidence and requested protocol.");
                    }
                    if (candidates.Length == 1)
                    {
                        return await OpenReconnectedUsbAsync(snapshot, candidates[0], previousInfo, identityPolicy, factory, token).ConfigureAwait(false);
                    }
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The USB reconnection deadline expired without a verified replacement.", error);
        }
    }

    private async Task<DeviceLease> OpenReconnectedUsbAsync(DeviceSnapshot snapshot, DeviceEndpoint selected,
        UsbDeviceInfo previousInfo, UsbReconnectIdentityPolicy identityPolicy, IDeviceSessionFactory factory, CancellationToken token)
    {
        var (endpoint, transport, generation) = await OpenTransportAsync(snapshot, selected.Id, token).ConfigureAwait(false);
        object? session = null;
        try
        {
            var native = LeasedTransport.Require<IUsbDeviceSession>(transport, factory.Kind);
            var opened = native.DeviceInfo;
            var expected = endpoint.Usb!;
            if (!MatchesUsbReconnectEvidence(previousInfo, opened, identityPolicy)
                || !MatchesUsbReconnectEvidence(endpoint.UsbInfo!, opened, identityPolicy)
                || opened.SourceApiKind != expected.Backend
                || opened.VendorId != expected.VendorId || opened.ProductId != expected.ProductId
                || opened.InterfaceClass != expected.InterfaceClass || opened.InterfaceSubClass != expected.InterfaceSubClass
                || opened.InterfaceProtocol != expected.InterfaceProtocol
                || !string.Equals(opened.DevicePath, expected.Path, StringComparison.OrdinalIgnoreCase)
                || !UsbDeviceIdentity.MatchesOpenedKey(endpoint.Address, opened.DeviceKey))
            {
                throw new IOException("The opened USB handle does not match the selected endpoint and reconnection evidence.");
            }
            token.ThrowIfCancellationRequested();
            session = await factory.CreateAsync(endpoint, transport, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            DeviceLease? lease = null;
            lease = new DeviceLease(endpoint, transport, session, () => Release(endpoint.Id, lease!), generation);
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
                switch (session)
                {
                    case IAsyncDisposable asyncSession:
                        await asyncSession.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable syncSession:
                        syncSession.Dispose();
                        break;
                }
            }
            finally
            {
                try
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    Release(endpoint.Id, null);
                }
            }
            throw;
        }
    }

    private static bool IsUsableUsbPortPath(string? path) =>
        path is { Length: > 0 and <= 1024 } && path.All(value => !char.IsWhiteSpace(value) && !char.IsControl(value));

    /// <summary>
    /// <para>判定是否仍有活动租约阻挡以该端点为旧端的 USB 重连等待：同一端点身份仍被占用即阻挡；
    /// 否则按 <see cref="PhysicalDeviceIdentity.CorrelatesWith"/> 检查每个活动租约的端点，容器 ID 优先、
    /// 物理端口链次之，防止同一物理设备在另一后端或另一端点身份下的租约形成双句柄；
    /// 关联不确定时回退到 OS 设备路径相等（路径会被回收，仅作弱证据兜底）。</para>
    /// Decides whether an open lease still blocks a USB reconnection wait for this previous endpoint:
    /// the same endpoint identity being reserved blocks; otherwise every open lease's endpoint is
    /// correlated through <see cref="PhysicalDeviceIdentity.CorrelatesWith"/> (container id first,
    /// physical-port path second) so a lease on another backend or another endpoint id of the same
    /// physical device cannot leave two handles alive; when correlation is indeterminate the check
    /// falls back to equal OS device paths, weak evidence because paths are recycled.
    /// </summary>
    private bool HasBlockingLeaseForLocked(DeviceEndpoint previous)
    {
        if (_leased.Contains(previous.Id))
        {
            return true;
        }

        foreach (var lease in _openLeases.Values)
        {
            if (CorrelatePhysicalObservation(previous.UsbInfo, lease.Endpoint.UsbInfo) is { } correlated)
            {
                if (correlated)
                {
                    return true;
                }

                continue;
            }

            if (IsSameUsbPath(previous.Usb, lease.Endpoint.Usb))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameUsbPath(UsbEndpointDetails? left, UsbEndpointDetails? right) =>
        left is { Path.Length: > 0 } && right is { Path.Length: > 0 }
        && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesUsbReconnectEvidence(UsbDeviceInfo expected, UsbDeviceInfo? actual,
        UsbReconnectIdentityPolicy policy)
    {
        if (actual is null)
        {
            return false;
        }
        if (policy == UsbReconnectIdentityPolicy.Container)
        {
            return expected.ContainerId == actual.ContainerId;
        }
        return IsUsableUsbPortPath(actual.PhysicalPortPath)
            && string.Equals(expected.PhysicalPortPath, actual.PhysicalPortPath, StringComparison.Ordinal)
            && !(expected.ContainerId is { } first && first != Guid.Empty
                && actual.ContainerId is { } second && second != Guid.Empty && first != second);
    }
}