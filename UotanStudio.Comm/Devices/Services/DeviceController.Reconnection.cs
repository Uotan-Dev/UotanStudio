using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Services;

public sealed partial class DeviceController
{
    /// <summary>Waits for a released MTK, EDL, Fastboot or Rockchip bootrom (RKDL) USB endpoint to disappear and opens its uniquely matched replacement.
    /// <para>等待已释放的 MTK、EDL、Fastboot 或 Rockchip bootrom（RKDL）USB 端点消失，并打开唯一匹配的替代端点。</para></summary>
    /// <remarks>The previous lease must be disposed before calling. This method never sends a reboot, changes USB speed, uploads an agent or replays a command. It requires a nonempty OS container ID, preserves the USB backend, and validates that identity again on the opened handle before creating a session. Missing container metadata, ambiguous interfaces, stale snapshots and open failures are not retried or replaced with serial, VID/PID or enumeration-order guesses. A returned MTK session is uninitialized; reconnecting alone does not recover a running DA's protocol state. Do not call from inside the previous lease's command dispatcher.
    /// <para>调用前必须释放旧租约。本方法不发送重启、不更改 USB 速度、不上传代理，也不重放命令。 必须具有非空的操作系统容器 ID，保持 USB 后端不变，并在创建会话前再次核验已打开句柄的身份。 缺失容器元数据、接口歧义、过期快照和打开失败均不重试，也不改用序列号、VID/PID 或枚举顺序猜测。 返回的 MTK 会话未初始化；仅重连不会恢复运行中 DA 的协议状态。不得在旧租约的命令调度器内部调用。</para></remarks>
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
    /// <exception cref="InvalidOperationException">An open lease still holds the previous endpoint or any endpoint of the same physical device, several endpoints match, or a snapshot is stale.
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
    /// <remarks>The physical-port policy permits a device without a container ID to reconnect at the same observed bus and port chain. It cannot distinguish replacement hardware on that port and is not an authentication mechanism. Neither policy falls back to serial numbers, VID/PID or list order. The old lease must be released; the old endpoint must disappear; ambiguous matches are rejected. The opened handle is rechecked before session creation. No protocol commands are sent.
    /// <para>物理端口策略允许没有容器 ID 的设备在相同的已观测总线与端口链上重连。 它无法区分该端口上被替换的硬件，也不是认证机制。两种策略均不回退到序列号、VID/PID 或列表顺序。 必须释放旧租约并观测到旧端点消失，歧义匹配会被拒绝。创建会话前重新检查打开的句柄，不发送协议命令。</para>
    /// The previous device node's PnP location chain dies with the node: after the stage switch the old instance id no longer resolves, so re-reading the chain from previousInfo during the wait would always fail and leave only the differing serial tails. The chain is stable physical topology, so it is read once, while the previous node is alive, and reused for every later comparison.
    /// <para>旧设备节点的 PnP 位置链随节点一起消失：阶段切换后旧实例 ID 不再能解析，等待期间从 previousInfo 重读链必然失败，只剩下不一致的序列号尾部。链是稳定的物理拓扑，因此在旧节点仍存在时读一次并供后续所有比较复用。</para>
    /// A stage switch that Windows serves by reusing the same device node keeps the previous endpoint id and device path while the observation itself changes (the loader rewrites the serial and the OS may assign a new container id). The loader policy therefore also treats "still listed, but the same-location observation now reports a different identity" as the previous endpoint being replaced; otherwise the wait never sees a disappearance and times out although the replacement is already in the snapshot.
    /// <para>由 Windows 复用同一设备节点完成的阶段切换会保留旧端点 ID 与设备路径，而观测本身已经变化（loader 改写序列号，操作系统可能分配新的容器 ID）。因此 loader 策略把「仍在清单中，但同位置观测已报告不同身份」同样视为旧端点已被替代；否则等待永远看不到消失，即使替代端点已在快照中也会超时。</para></remarks>
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
    /// <param name="capturedLocationChain">
    /// The previous device node's hub chain captured while that node was alive (see
    /// <see cref="CaptureUsbLocationChain(UotanStudio.Comm.Transports.Usb.Abstractions.UsbDeviceInfo)"/>);
    /// when supplied it replaces an in-method capture, which can no longer resolve after the node was
    /// replaced.
    /// <para>旧设备节点存活时捕获的集线器链（见
    /// <see cref="CaptureUsbLocationChain(UotanStudio.Comm.Transports.Usb.Abstractions.UsbDeviceInfo)"/>）；
    /// 提供后替代方法内捕获——节点被替换后方法内捕获无法再解析。</para></param>
    /// <returns>The caller-owned lease for the unique verified replacement.
    /// <para>由调用方持有的唯一经校验替代端点租约。</para></returns>
    public async Task<DeviceLease> WaitForUsbReconnectAsync(DeviceEndpoint previous, DeviceSessionKind kind,
        UsbReconnectIdentityPolicy identityPolicy, int timeoutMs = 30000, CancellationToken cancellationToken = default,
        string? capturedLocationChain = null)
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
        var evidenceSatisfied = identityPolicy switch
        {
            UsbReconnectIdentityPolicy.Container => previousInfo.ContainerId is null || previousInfo.ContainerId == Guid.Empty,
            UsbReconnectIdentityPolicy.PhysicalPort => !IsUsableUsbPortPath(previousInfo.PhysicalPortPath),
            _ => !HasRockchipLoaderLocationEvidence(previousInfo)
        };
        if (!evidenceSatisfied)
        {
            throw new NotSupportedException("The previous endpoint lacks the evidence required by the selected USB reconnection policy.");
        }

        var stickyLocationChain = identityPolicy == UsbReconnectIdentityPolicy.RockchipLoader
            ? (capturedLocationChain is { Length: > 0 } ? capturedLocationChain : ReadRockchipLocationChain(previousInfo))
            : null;
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
        DeviceSnapshot? lastSnapshot = null;
        try
        {
            while (true)
            {
                var snapshot = await RefreshAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lastSnapshot = snapshot;
                var previousStillListed = snapshot.Endpoints.FirstOrDefault(endpoint => endpoint.Id == previous.Id);
                if (identityPolicy == UsbReconnectIdentityPolicy.RockchipLoader &&
                    previousStillListed is { } listed &&
                    RockchipObservationReplaced(previousInfo, listed.UsbInfo))
                {
                    disappeared = true;
                }

                disappeared |= previousStillListed is null;
                if (disappeared)
                {
                    var candidates = snapshot.Endpoints.Where(endpoint =>
                        endpoint.Source == previous.Source && endpoint.Route == "direct-usb"
                        && endpoint.Transport == previous.Transport && endpoint.Protocol == protocol
                        && endpoint.Usb?.Backend == usb.Backend
                        && MatchesUsbReconnectEvidence(previousInfo, endpoint.UsbInfo, identityPolicy, stickyLocationChain))
                        .Take(2).ToArray();
                    if (candidates.Length > 1)
                    {
                        throw new InvalidOperationException("Several USB interfaces match the selected reconnection evidence and requested protocol.");
                    }
                    if (candidates.Length == 1)
                    {
                        return await OpenReconnectedUsbAsync(snapshot, candidates[0], previousInfo, identityPolicy, factory, stickyLocationChain, token).ConfigureAwait(false);
                    }

                    LogRejectedReconnectionCandidates(snapshot, protocol, previousInfo, identityPolicy, usb.Backend);
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The USB reconnection deadline expired without a verified replacement." +
                DescribeReconnectionDeadEnd(lastSnapshot, protocol, previousInfo, identityPolicy, usb.Backend, stickyLocationChain),
                error);
        }
    }

    private async Task<DeviceLease> OpenReconnectedUsbAsync(DeviceSnapshot snapshot, DeviceEndpoint selected,
        UsbDeviceInfo previousInfo, UsbReconnectIdentityPolicy identityPolicy, IDeviceSessionFactory factory,
        string? stickyLocationChain, CancellationToken token)
    {
        var (endpoint, transport, generation) = await OpenTransportAsync(snapshot, selected.Id, token).ConfigureAwait(false);
        object? session = null;
        try
        {
            var native = LeasedTransport.Require<IUsbDeviceSession>(transport, factory.Kind);
            var opened = native.DeviceInfo;
            var expected = endpoint.Usb!;
            if (!MatchesUsbReconnectEvidence(previousInfo, opened, identityPolicy, stickyLocationChain)
                || !MatchesUsbReconnectEvidence(endpoint.UsbInfo!, opened, identityPolicy, stickyLocationChain)
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
    /// Summarizes why the wait ended without a replacement: whether the previous endpoint is still listed and
    /// whether same-protocol endpoints exist with their identity evidence, so a timeout states what the last
    /// snapshot saw.
    /// <para>总结等待为何没有等到替代端点：旧端点是否仍在清单中、同协议端点及其身份证据是什么，使超时
    /// 说明最后一次快照看到了什么。</para>
    /// </summary>
    private static string DescribeReconnectionDeadEnd(DeviceSnapshot? snapshot, string protocol,
        UsbDeviceInfo previousInfo, UsbReconnectIdentityPolicy identityPolicy, UsbApiKind backend,
        string? stickyLocationChain)
    {
        if (snapshot is null)
        {
            return " No snapshot was committed during the wait. 等待期间没有提交任何快照。";
        }

        var sameProtocol = snapshot.Endpoints.Where(endpoint =>
            endpoint.Protocol == protocol && endpoint.Usb?.Backend == backend).ToArray();
        if (sameProtocol.Length == 0)
        {
            return " The last snapshot lists no same-protocol endpoint. 最后一次快照没有同协议端点。";
        }

        var details = sameProtocol.Select(endpoint =>
        {
            var info = endpoint.UsbInfo;
            return $"{endpoint.Id} serial={info?.SerialNumber ?? "-"} bcdUSB={(info?.BcdUsb is { } bcd ? $"0x{bcd:X4}" : "-")} instance={info?.DeviceInstanceId ?? "-"}";
        });
        return $" Captured previous location chain: {stickyLocationChain ?? "-"}. 已捕获的旧位置链：{stickyLocationChain ?? "-"}. " +
            $"Same-protocol endpoints at the deadline: {string.Join("; ", details)}. 超时时的同协议端点：";
    }

    /// <summary>
    /// Logs, rate-limited to one line per endpoint per wait, why a same-protocol endpoint that appeared after
    /// the previous one disappeared still failed the reconnection evidence: the per-observation chain, serial,
    /// <c>bcdUSB</c> and instance tail name the exact rejected evidence on real hardware.
    /// <para>对同一等待内的每个端点限速记录一行：同协议端点在旧端点消失后出现却仍通过不了重连证据的原因——
    /// 逐观测的链、序列号、<c>bcdUSB</c> 与实例尾部直接点名真机上被拒绝的证据。</para>
    /// </summary>
    private void LogRejectedReconnectionCandidates(DeviceSnapshot snapshot, string protocol,
        UsbDeviceInfo previousInfo, UsbReconnectIdentityPolicy identityPolicy, UsbApiKind backend)
    {
        foreach (var endpoint in snapshot.Endpoints.Where(endpoint =>
            endpoint.Protocol == protocol && endpoint.Usb?.Backend == backend))
        {
            var info = endpoint.UsbInfo;
            var previousChain = ReadRockchipLocationChain(previousInfo);
            var candidateChain = info is null ? null : ReadRockchipLocationChain(info);
            CommTrace.Debug("reconnect", $"Reconnect wait: candidate {endpoint.Id} rejected by {identityPolicy} evidence. " +
                $"previous chain={previousChain ?? "-"} serial={previousInfo.SerialNumber ?? "-"} bcdUSB={(previousInfo.BcdUsb is { } pb ? $"0x{pb:X4}" : "-")} instance={previousInfo.DeviceInstanceId ?? "-"}; " +
                $"candidate chain={candidateChain ?? "-"} serial={info?.SerialNumber ?? "-"} bcdUSB={(info?.BcdUsb is { } cb ? $"0x{cb:X4}" : "-")} instance={info?.DeviceInstanceId ?? "-"}.");
        }
    }

    /// <summary>
    /// Decides whether an open lease still blocks a USB reconnection wait for this previous endpoint:
    /// the same endpoint identity being reserved blocks; otherwise every open lease's endpoint is
    /// correlated through <see cref="PhysicalDeviceIdentity.CorrelatesWith"/> (container id first,
    /// physical-port path second) so a lease on another backend or another endpoint id of the same
    /// physical device cannot leave two handles alive; when correlation is indeterminate the check
    /// falls back to equal OS device paths, weak evidence because paths are recycled.
    /// <para>判定是否仍有活动租约阻挡以该端点为旧端的 USB 重连等待：同一端点身份仍被占用即阻挡；
    /// 否则按 <see cref="PhysicalDeviceIdentity.CorrelatesWith"/> 检查每个活动租约的端点，容器 ID 优先、
    /// 物理端口链次之，防止同一物理设备在另一后端或另一端点身份下的租约形成双句柄；
    /// 关联不确定时回退到 OS 设备路径相等（路径会被回收，仅作弱证据兜底）。</para>
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
        UsbReconnectIdentityPolicy policy, string? stickyLocationChain = null)
    {
        if (actual is null)
        {
            return false;
        }
        if (policy == UsbReconnectIdentityPolicy.Container)
        {
            return expected.ContainerId == actual.ContainerId;
        }
        if (policy == UsbReconnectIdentityPolicy.RockchipLoader)
        {
            return MatchesRockchipLoaderEvidence(expected, actual, stickyLocationChain);
        }
        return IsUsableUsbPortPath(actual.PhysicalPortPath)
            && string.Equals(expected.PhysicalPortPath, actual.PhysicalPortPath, StringComparison.Ordinal)
            && !(expected.ContainerId is { } first && first != Guid.Empty
                && actual.ContainerId is { } second && second != Guid.Empty && first != second);
    }

    /// <summary>
    /// Reports whether the replacement observation is a loader-stage Rockchip bootrom port at the previous
    /// endpoint's physical location: the hub chain behind the OS device instance id (or the backend port path,
    /// or the PnP location tail when the chain is unavailable) must be unchanged, and the loader stage must be
    /// evidenced by <c>bcdUSB</c> bit 0, the vendor's <c>ROCKCHIP</c> loader serial, or the instance tail. The
    /// container ID is deliberately not compared: the stage switch rewrites the reported identity.
    /// <para>判定替代端点观测是否为旧端点物理位置上的 loader 阶段瑞芯微 bootrom 端口：OS 设备实例 ID 背后的
    /// 集线器链（或后端端口路径，链不可用时回退 PnP 位置尾部）必须不变，且 loader 阶段须由 <c>bcdUSB</c>
    /// bit 0、厂商 <c>ROCKCHIP</c> loader 序列号或实例尾部证明。容器 ID 有意不参与比较：阶段切换本就会
    /// 改写上报身份。</para>
    /// </summary>
    /// <param name="previous">The previous endpoint's observation.<para>旧端点的观测。</para></param>
    /// <param name="replacement">The replacement's observation.<para>替代端点的观测。</para></param>
    /// <param name="stickyLocationChain">
    /// The previous device node's hub chain captured while that node was still alive, or
    /// <see langword="null"/> when it could not be read; it outlives the node and decides the location
    /// comparison when present.
    /// <para>旧设备节点仍存活时捕获的集线器链；无法读取时为 <see langword="null"/>。它比节点存活得更久，
    /// 存在时决定位置比较。</para></param>
    internal static bool MatchesRockchipLoaderEvidence(UsbDeviceInfo previous, UsbDeviceInfo replacement,
        string? stickyLocationChain = null)
        => MatchesRockchipLocation(previous, replacement, stickyLocationChain) && IsRockchipLoaderStage(replacement);

    /// <summary>
    /// Reports whether the replacement sits at the same physical location as the previous observation. A
    /// captured previous hub chain is authoritative when present; otherwise the Windows hub chain derived
    /// from each side's device instance id decides when both are available; the backend port path decides
    /// when both carry one; the PnP location tail is the last fallback. When no location evidence exists on
    /// either side the match is refused instead of guessed.
    /// <para>判定替代端点是否位于与旧观测相同的物理位置。已捕获的旧集线器链存在时最优先；否则两侧都有
    /// 设备实例 ID 时以 Windows 集线器链判定；两侧都有后端端口路径时以端口路径判定；PnP 位置尾部是最后
    /// 的回退。任一侧都没有位置证据时拒绝匹配，不做猜测。</para>
    /// </summary>
    /// <param name="previous">The previous endpoint's observation.<para>旧端点的观测。</para></param>
    /// <param name="replacement">The replacement's observation.<para>替代端点的观测。</para></param>
    /// <param name="stickyLocationChain">
    /// The previous hub chain captured while the previous device node was alive, or
    /// <see langword="null"/> when unavailable.
    /// <para>旧设备节点存活时捕获的集线器链；不可用时为 <see langword="null"/>。</para></param>
    internal static bool MatchesRockchipLocation(UsbDeviceInfo previous, UsbDeviceInfo replacement,
        string? stickyLocationChain = null)
    {
        if (stickyLocationChain is { Length: > 0 })
        {
            var replacementChainOnly = ReadRockchipLocationChain(replacement);
            if (replacementChainOnly is not null)
            {
                return string.Equals(stickyLocationChain, replacementChainOnly, StringComparison.Ordinal);
            }
        }

        var previousChain = ReadRockchipLocationChain(previous);
        var replacementChain = ReadRockchipLocationChain(replacement);
        if (previousChain is not null && replacementChain is not null)
        {
            return string.Equals(previousChain, replacementChain, StringComparison.Ordinal);
        }

        if (IsUsableUsbPortPath(previous.PhysicalPortPath) && IsUsableUsbPortPath(replacement.PhysicalPortPath))
        {
            return string.Equals(previous.PhysicalPortPath, replacement.PhysicalPortPath, StringComparison.Ordinal);
        }

        var previousTail = RkdlSerialReader.InstanceLocationTail(previous.DeviceInstanceId ?? string.Empty);
        var replacementTail = RkdlSerialReader.InstanceLocationTail(replacement.DeviceInstanceId ?? string.Empty);
        return previousTail is not null && replacementTail is not null
            && string.Equals(previousTail, replacementTail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the stable physical hub chain of an observed USB device while that device node is still alive.
    /// A stage switch replaces the node, so the chain must be captured beforehand and passed to the
    /// reconnection wait as the captured location chain argument.
    /// <para>在 USB 设备节点仍存活时读取其稳定物理集线器链。阶段切换会替换节点，因此必须先捕获，
    /// 再作为捕获的位置链参数传给重连等待。</para>
    /// </summary>
    /// <param name="info">The observed USB metadata to read from.<para>要读取的 USB 观测元数据。</para></param>
    /// <returns>The hub chain, or <see langword="null"/> when no usable chain was reported.
    /// <para>集线器链；没有可用链时为 <see langword="null"/>。</para></returns>
    public static string? CaptureUsbLocationChain(UsbDeviceInfo info) => ReadRockchipLocationChain(info);

    /// <summary>
    /// Reads the stable physical hub chain of one observation: the Windows chain behind the device instance id
    /// when available, otherwise the backend port path. Returns <see langword="null"/> when neither evidence
    /// source reported a usable chain.
    /// <para>读取一次观测的稳定物理集线器链：优先设备实例 ID 背后的 Windows 集线器链，否则后端端口路径；
    /// 两个证据源都没有可用链时返回 <see langword="null"/>。</para>
    /// </summary>
    private static string? ReadRockchipLocationChain(UsbDeviceInfo info)
    {
        if (info.DeviceInstanceId is { Length: > 0 } instanceId
            && UsbLocationChain.TryRead(instanceId) is { Length: > 0 } chain)
        {
            return chain;
        }

        return IsUsableUsbPortPath(info.PhysicalPortPath) ? info.PhysicalPortPath : null;
    }

    /// <summary>
    /// Reports whether the observation now listed under the previous endpoint's id no longer describes the
    /// previous identity: a changed container id, a changed serial, or a serial the stage switch overwrote
    /// with the vendor's <c>ROCKCHIP</c> loader name. Used only by the Rockchip loader policy, whose stage
    /// switch may keep the same device node.
    /// <para>报告现在挂在旧端点 ID 下的观测是否已不再描述旧身份：容器 ID 变化、序列号变化，或序列号已被
    /// 阶段切换覆写为厂商的 <c>ROCKCHIP</c> loader 名。仅由 Rockchip loader 策略使用，其阶段切换可能
    /// 复用同一设备节点。</para>
    /// </summary>
    internal static bool RockchipObservationReplaced(UsbDeviceInfo previous, UsbDeviceInfo? listed)
    {
        if (listed is null)
        {
            return false;
        }

        if (previous.ContainerId is { } previousContainer && previousContainer != Guid.Empty &&
            listed.ContainerId is { } listedContainer && listedContainer != Guid.Empty &&
            previousContainer != listedContainer)
        {
            return true;
        }

        var previousSerial = previous.SerialNumber?.Trim();
        var listedSerial = listed.SerialNumber?.Trim();
        if (!string.IsNullOrEmpty(previousSerial) && !string.IsNullOrEmpty(listedSerial) &&
            !string.Equals(previousSerial, listedSerial, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrEmpty(listedSerial) &&
            string.Equals(listedSerial, "ROCKCHIP", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(previousSerial, "ROCKCHIP", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reports whether a replacement observation carries loader-stage evidence: <c>bcdUSB</c> bit 0 set, the
    /// vendor's <c>ROCKCHIP</c> loader serial the stage switch writes over the maskrom serial, or the same
    /// <c>ROCKCHIP</c> name as the device instance id's tail — Windows embeds the reported iSerial into the
    /// instance path, and a <c>rockusb.sys</c> device whose serial the snapshot could not resolve otherwise
    /// still carries it there.
    /// <para>判定替代端点观测是否携带 loader 阶段证据：<c>bcdUSB</c> bit 0 置位、阶段切换覆写 maskrom
    /// 序列号后写入的厂商 <c>ROCKCHIP</c> loader 序列号，或设备实例 ID 尾部同样的 <c>ROCKCHIP</c> 名——
    /// Windows 会把上报的 iSerial 编入实例路径，而快照无法以其他方式解析序列号的 <c>rockusb.sys</c>
    /// 设备仍在该处携带它。</para>
    /// </summary>
    internal static bool IsRockchipLoaderStage(UsbDeviceInfo replacement)
        => replacement.BcdUsb is ushort bcdUsb && (bcdUsb & 0x0001) != 0
        || string.Equals(replacement.SerialNumber?.Trim(), "ROCKCHIP", StringComparison.OrdinalIgnoreCase)
        || InstanceIdTail(replacement.DeviceInstanceId) is { } tail
            && !tail.Contains('&')
            && string.Equals(tail, "ROCKCHIP", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the device instance id's tail after the last separator, or <see langword="null"/> when the
    /// instance id is blank.
    /// <para>返回设备实例 ID 最后一个分隔符之后的尾部；实例 ID 为空时返回 <see langword="null"/>。</para>
    /// </summary>
    private static string? InstanceIdTail(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        var separator = instanceId.LastIndexOf('\\');
        return separator >= 0 ? instanceId[(separator + 1)..] : instanceId;
    }

    /// <summary>
    /// Reports whether the previous endpoint carries the location evidence the Rockchip loader policy needs:
    /// a Windows hub chain behind its device instance id or a usable backend port path.
    /// <para>判定旧端点是否携带 Rockchip loader 策略所需的位置证据：设备实例 ID 背后的 Windows 集线器链，
    /// 或可用的后端端口路径。</para>
    /// </summary>
    private static bool HasRockchipLoaderLocationEvidence(UsbDeviceInfo info)
        => ReadRockchipLocationChain(info) is not null
        || RkdlSerialReader.InstanceLocationTail(info.DeviceInstanceId ?? string.Empty) is not null;

}