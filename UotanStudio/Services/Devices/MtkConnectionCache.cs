using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Preserves MTK boot handshakes until the next operation on the same physical endpoint. A parked lease is
/// reused only while its typed MTK session is live and its transport generation still matches, so a preloader
/// port that accepts the complemented handshake for only a few seconds after enumeration keeps a completed
/// identity instead of losing its window while the caller assembles an operation.
/// <para>在同一物理端点的下一次操作之前保留 MTK boot 握手。仅当类型化 MTK 会话仍在线且传输代际仍然匹配时，
/// 才会复用暂存的租约，使枚举后只在数秒内接受反码握手的 preloader 端口保留已完成的身份，
/// 而不是在调用方组装操作期间丢失握手窗口。</para>
/// </summary>
/// <param name="open">Opens a fresh lease for the endpoint.<para>为该端点打开一个新的租约。</para></param>
/// <param name="changed">Notifies about publishable state changes.<para>通知可发布的状态变化。</para></param>
/// <param name="vcomOpen">Opens a lease directly over the VCOM port bound to a USB device, returning null when the path has no COM port.<para>在绑定到 USB 设备的 VCOM 端口上直接打开租约；路径没有 COM 口时返回 null。</para></param>
/// <param name="leaseIsCurrent">Reports whether a parked lease's transport generation is still current.<para>报告暂存租约的传输代际是否仍然有效。</para></param>
/// <param name="portResolver">Resolves the serial port name a USB endpoint is bound to without opening it; drives lease adoption. Returns null when the endpoint publishes no port or the platform cannot tell.<para>在不打开设备的情况下解析 USB 端点绑定的串口名；用于租约接管。端点不公布串口或平台无法判断时返回 null。</para></param>
internal sealed class MtkConnectionCache(
    Func<DetectedDevice, CancellationToken, Task<DeviceLease>> open,
    Action changed,
    Func<UsbDeviceInfo, int, CancellationToken, Task<DeviceLease?>>? vcomOpen = null,
    Func<DeviceLease, bool>? leaseIsCurrent = null,
    Func<UsbDeviceInfo, string?>? portResolver = null) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Prefix of the synthetic cache keys that park a lease under the serial port name a watcher
    /// reported, so device-keyed consumers can adopt it regardless of which arrival path opened it first.
    /// <para>以监视器上报的串口名暂存租约的合成缓存键前缀，使按设备键消费的调用方无论哪条到达路径先打开
    /// 都能接管它。</para></summary>
    private const string PortKeyPrefix = "port:";

    /// <summary>Prefix of the synthetic cache keys that park a non-serial arrival lease under the arrival path.
    /// Such a key never equals a scan device key, so retention must not read it as a vanished device.
    /// <para>以到达路径暂存非串口到达租约的合成缓存键前缀。这种键永远不会等于扫描设备键，
    /// 因此保留逻辑不得把它当作已消失的设备。</para></summary>
    private const string ArrivalKeyPrefix = "arrival:";

    /// <summary>How long a lease stays eligible for same-vid/pid adoption. Identical preloader models share one
    /// vid/pid pair, so the heuristic only matches while the handshake window is plausibly still open.
    /// <para>租约可按相同 vid/pid 被接管的最长时间。同型号 preloader 共享同一组 vid/pid，
    /// 因此该启发式只在握手窗口可能仍开的时限内匹配。</para></summary>
    private const long SamePidAdoptionWindowMilliseconds = 15_000;

    /// <summary>
    /// How long an explicit handshake waits for one already running on the same endpoint before reporting the
    /// state that run leaves behind; the window covers one full handshake without letting a pathological holder
    /// block the caller, and the caller's token still bounds it.
    /// <para>显式握手在同一端点上已有握手运行时最多等多久，之后回报那次运行留下的状态；该窗口覆盖一次完整
    /// 握手，又不让异常持有者阻塞调用方；调用方自己的令牌仍是等待的真正上界。</para>
    /// </summary>
    private const int HandshakeJoinBudgetMs = 8_000;

    /// <summary>
    /// Optional generation-liveness probe supplied after construction; the constructor delegate wins when both are
    /// present. Without a probe, liveness relies on the session's own connection state.
    /// <para>构造之后提供的可选代际判活探针；与构造参数同时存在时构造参数优先。没有探针时仅按会话自身的连接状态判活。</para>
    /// </summary>
    internal Func<DeviceLease, bool>? LeaseCurrentProbe { get; set; }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Operation = new(1, 1);
        internal DeviceLease? Parked;
        internal long ParkedAtMilliseconds;
        internal volatile bool Removed;
    }

    private const long ParkedLeaseIdleLimitMilliseconds = 60_000;

    private static void Park(Entry entry, DeviceLease lease)
    {
        entry.Parked = lease;
        Volatile.Write(ref entry.ParkedAtMilliseconds, Environment.TickCount64);
    }

    private static DeviceLease? TakeParked(Entry entry)
    {
        var lease = entry.Parked;
        if (lease is null)
        {
            return null;
        }

        entry.Parked = null;
        Volatile.Write(ref entry.ParkedAtMilliseconds, 0);
        return lease;
    }

    private static void ClearParked(Entry entry)
    {
        entry.Parked = null;
        Volatile.Write(ref entry.ParkedAtMilliseconds, 0);
    }

    /// <summary>Extracts the serial port name a lease's MTK session runs on, or null for non-serial transports.
    /// <para>提取租约 MTK 会话所在的串口名；非串口传输时为 null。</para></summary>
    private static string? LeasePortName(DeviceLease lease) =>
        lease.TryGetSession<MtkSession>(out var session) && session.Transport is SerialTransport serial
            ? serial.PortName
            : null;

    private static string PortKey(string portName) => string.Concat(PortKeyPrefix, portName);

    private bool LeaseIsLive(DeviceLease lease)
    {
        if (!lease.TryGetSession<MtkSession>(out var session) || !session.IsConnected)
        {
            return false;
        }

        var probe = leaseIsCurrent ?? LeaseCurrentProbe;
        return probe is null || probe(lease);
    }

    internal async Task<DeviceLease> HandshakeAsync(DetectedDevice device, CancellationToken cancellationToken,
        UsbDeviceInfo? usbInfo = null)
    {
        var entry = GetEntry(device);
        if (!await entry.Operation.WaitAsync(HandshakeJoinBudgetMs, cancellationToken).ConfigureAwait(false))
        {
            // A handshake on this endpoint has been running longer than one should take; open a fresh lease rather
            // than waiting forever behind it — the caller's own open path reports its own failure.
            // 该端点上的握手已超过一次握手应有的时长；不再无限等待，直接打开新租约——调用方自己的打开路径会报告其自身失败。
            return await open(device, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || entry.Removed, this);
            if (entry.Parked is { } parked && !LeaseIsLive(parked))
            {
                await entry.Parked.DisposeAsync().ConfigureAwait(false);
                ClearParked(entry);
            }
            if (entry.Parked is null
                && await TryAdoptCoveringLeaseAsync(entry, usbInfo ?? device.Endpoint?.UsbInfo)
                    .ConfigureAwait(false) is not null)
            {
                Publish();
            }
            if (entry.Parked is null)
            {
                var opened = await open(device, cancellationToken).ConfigureAwait(false);
                Park(entry, opened);
                await RequireSession(opened).InitializeAsync(cancellationToken).ConfigureAwait(false);
                Publish();
            }
            return entry.Parked
                ?? throw new InvalidOperationException("MTK handshake did not obtain a lease.");
        }
        catch
        {
            await DisposeParkedAsync(entry).ConfigureAwait(false);
            Publish();
            throw;
        }
        finally
        {
            Release(entry);
        }
    }

    /// <summary>
    /// Handshakes a VCOM-bound preloader directly from its USB descriptors, parking the resulting lease for the
    /// next operation. This bypasses the USB inventory walk that a short-lived preloader cannot survive.
    /// <para>直接从 USB 描述符在 VCOM 绑定的 preloader 上握手，并把结果租约暂存给下一次操作。
    /// 这绕过了短暂 preloader 无法承受的 USB 清单遍历。</para>
    /// </summary>
    internal async Task<DeviceLease?> HandshakeVcomAsync(UsbDeviceInfo info, DetectedDevice device,
        int baudRate, CancellationToken cancellationToken)
    {
        if (vcomOpen is null)
        {
            return null;
        }

        var entry = GetEntry(device);
        if (!await entry.Operation.WaitAsync(HandshakeJoinBudgetMs, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || entry.Removed, this);
            if (entry.Parked is null)
            {
                var opened = await vcomOpen(info, baudRate, cancellationToken).ConfigureAwait(false);
                if (opened is null)
                {
                    return null;
                }

                Park(entry, opened);
                Publish();
            }
            return entry.Parked;
        }
        catch
        {
            await DisposeParkedAsync(entry).ConfigureAwait(false);
            Publish();
            throw;
        }
        finally
        {
            Release(entry);
        }
    }
    /// <summary>
    /// Handshakes a bare serial port the moment a watcher reports it and parks the resulting lease under the
    /// port key. The open delegate owns the whole open-and-handshake exchange; the cache only serializes the
    /// port and preserves the result, so a preloader whose port appears for a few seconds keeps its completed
    /// handshake for every later device-keyed consumer.
    /// <para>在监视器上报裸串口名的瞬间握手该端口，并把结果租约以端口键暂存。打开委托负责完整的打开与握手
    /// 交换；缓存只负责串口的串行化并保留结果，使端口只出现数秒的 preloader 为后续所有按设备键消费的调用方
    /// 保留已完成的握手。</para>
    /// </summary>
    /// <param name="portName">The exact port the watcher observed. <para>监视器观测到的确切端口名。</para></param>
    /// <param name="openPort">Opens and handshakes the port, returning the live lease or
    /// <see langword="null"/> when the open could not take the port.
    /// <para>打开并握手该端口，返回在线租约；无法取得端口时返回 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancellation for the open and handshake.
    /// <para>取消打开与握手。</para></param>
    /// <returns>The parked lease, or <see langword="null"/> when another handshake already owns the port or the
    /// open delegate yielded nothing. <para>已暂存的租约；另一个握手已占用该端口或打开委托未返回租约时为
    /// <see langword="null"/>。</para></returns>
    internal async Task<DeviceLease?> HandshakePortAsync(string portName,
        Func<CancellationToken, Task<DeviceLease?>> openPort, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentNullException.ThrowIfNull(openPort);
        var entry = GetEntryByKey(PortKey(portName));
        if (!await entry.Operation.WaitAsync(HandshakeJoinBudgetMs, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed || entry.Removed, this);
            if (entry.Parked is { } parked && !LeaseIsLive(parked))
            {
                await parked.DisposeAsync().ConfigureAwait(false);
                ClearParked(entry);
            }
            if (entry.Parked is null)
            {
                var opened = await openPort(cancellationToken).ConfigureAwait(false);
                if (opened is null)
                {
                    return null;
                }

                Park(entry, opened);
                Publish();
            }

            return entry.Parked;
        }
        catch
        {
            await DisposeParkedAsync(entry).ConfigureAwait(false);
            Publish();
            throw;
        }
        finally
        {
            Release(entry);
        }
    }

    /// <summary>
    /// Parks a lease an arrival path handshook outside the cache, keyed by the lease's own serial port when it
    /// is a serial session so device-keyed consumers can adopt it, or by the caller's fallback key otherwise.
    /// <para>暂存某条到达路径在缓存之外握手的租约：串口会话按其自身串口名作键，使按设备键消费的调用方可以
    /// 接管；其余按调用方给定的回退键暂存。</para>
    /// </summary>
    /// <param name="lease">The handshaken lease to preserve. <para>要保留的已完成握手的租约。</para></param>
    /// <param name="fallbackKey">The key used when the lease carries no serial port.
    /// <para>租约不带串口时使用的键。</para></param>
    /// <param name="cancellationToken">Cancels the wait for a running handshake on the same key.
    /// <para>取消对同键上正在运行的握手的等待。</para></param>
    internal async Task ParkArrivalLeaseAsync(DeviceLease lease, string fallbackKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackKey);
        var key = LeasePortName(lease) is { } portName ? PortKey(portName) : string.Concat(ArrivalKeyPrefix, fallbackKey);
        var entry = GetEntryByKey(key);
        if (!await entry.Operation.WaitAsync(HandshakeJoinBudgetMs, cancellationToken).ConfigureAwait(false))
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed || entry.Removed, this);
            if (entry.Parked is { } parked)
            {
                // The arrival fired twice for one endpoint and both opens succeeded only if the first lease had
                // already died; a live parked lease means the new lease duplicates it and must not keep both.
                // 同一端点的到达触发两次，且只有第一个租约已死亡时两次打开才会都成功；已暂存租约在线意味着
                // 新租约与之重复，不能两者都保留。
                ClearParked(entry);
                await parked.DisposeAsync().ConfigureAwait(false);
            }
            Park(entry, lease);
            Publish();
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            Release(entry);
        }
    }

    /// <summary>
    /// Moves a live parked lease that covers the USB endpoint into the caller's entry. The automatic arrival paths
    /// park under port or arrival keys this cache cannot derive from a <see cref="DetectedDevice"/> key alone;
    /// adoption is what makes a completed handshake consumable without opening a second lease that the operating
    /// system then reports busy.
    /// <para>把覆盖该 USB 端点的在线暂存租约移入调用方条目。自动到达路径以端口键或到达键暂存，仅凭
    /// <see cref="DetectedDevice"/> 键无法找到它们；正是接管让已完成的握手可被消费，而不是再打开一个
    /// 会被操作系统报忙的租约。</para>
    /// </summary>
    /// <param name="entry">The entry to adopt into. <para>要接收到其中的条目。</para></param>
    /// <param name="info">The USB descriptors of the endpoint a lease is wanted for, or null when unknown.
    /// <para>需要租约的端点的 USB 描述符；未知时为 null。</para></param>
    /// <returns>The adopted live lease, or <see langword="null"/> when no parked lease covers the endpoint.
    /// <para>被接管的在线租约；没有暂存租约覆盖该端点时为 <see langword="null"/>。</para></returns>
    private async Task<DeviceLease?> TryAdoptCoveringLeaseAsync(Entry entry, UsbDeviceInfo? info)
    {
        if (entry.Parked is not null || info is null)
        {
            return null;
        }

        string? portName = null;
        try
        {
            portName = portResolver?.Invoke(info);
        }
        catch (Exception)
        {
            portName = null;
        }

        List<(Entry Source, DeviceLease Lease)> matches = [];
        lock (_gate)
        {
            foreach (var (key, candidate) in _entries)
            {
                if (ReferenceEquals(candidate, entry) || candidate.Parked is not { } parked)
                {
                    continue;
                }

                if (Covers(parked, key, info, portName, Volatile.Read(ref candidate.ParkedAtMilliseconds)))
                {
                    matches.Add((candidate, parked));
                }
            }
        }

        foreach (var (source, _) in matches)
        {
            if (!source.Operation.Wait(0))
            {
                continue;
            }

            try
            {
                if (source.Parked is not { } lease)
                {
                    continue;
                }

                TakeParked(source);
                if (LeaseIsLive(lease))
                {
                    Park(entry, lease);
                    return lease;
                }

                await lease.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Release(source);
            }
        }

        return null;
    }

    /// <summary>Reports whether a parked lease belongs to the same physical endpoint the caller is about to open.
    /// <para>报告暂存租约是否属于调用方即将打开的同一物理端点。</para></summary>
    /// <remarks>Matching runs from the strongest evidence down: the exact serial port, the exact USB device path,
    /// vid/pid with equal serials, and finally vid/pid alone while the handshake window is plausibly still open.
    /// <para>按证据强度递减匹配：精确串口、精确 USB 设备路径、vid/pid 加相同序列号，最后是握手窗口可能仍开
    /// 时段内的仅 vid/pid。</para></remarks>
    private bool Covers(DeviceLease lease, string leaseKey, UsbDeviceInfo? info, string? portName,
        long parkedAtMilliseconds)
    {
        if (portName is not null && leaseKey.StartsWith(PortKeyPrefix, StringComparison.Ordinal)
            && string.Equals(leaseKey[PortKeyPrefix.Length..], portName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (info is null)
        {
            return false;
        }

        var leaseInfo = lease.Endpoint.UsbInfo;
        if (leaseInfo is not null)
        {
            if (!string.IsNullOrEmpty(info.DevicePath) && !string.IsNullOrEmpty(leaseInfo.DevicePath)
                && string.Equals(leaseInfo.DevicePath, info.DevicePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrEmpty(info.SerialNumber)
                && string.Equals(leaseInfo.SerialNumber, info.SerialNumber, StringComparison.Ordinal)
                && leaseInfo.VendorId == info.VendorId
                && leaseInfo.ProductId == info.ProductId;
        }

        var usb = lease.Endpoint.Usb;
        return usb is not null
            && usb.VendorId == info.VendorId
            && usb.ProductId == info.ProductId
            && Environment.TickCount64 - parkedAtMilliseconds < SamePidAdoptionWindowMilliseconds;
    }

    internal async Task<TResult> RunAsync<TResult>(DetectedDevice device,
        Func<MtkSession, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var entry = GetEntry(device);
        await entry.Operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        DeviceLease? lease = null;
        var succeeded = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || entry.Removed, this);
            lease = TakeParked(entry);
            if (lease is not null && !LeaseIsLive(lease))
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
            }
            lease ??= await TryAdoptCoveringLeaseAsync(entry, device.Endpoint?.UsbInfo).ConfigureAwait(false);
            lease ??= await open(device, cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(entry.Removed, this);
            var session = RequireSession(lease);
            if (session.State == MtkSessionState.Created)
            {
                await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            var result = await operation(session).ConfigureAwait(false);
            succeeded = true;
            return result;
        }
        finally
        {
            try
            {
                if (lease is not null)
                {
                    // Only a session that still sits at the identity-only Bootloader stage is parked: an operation
                    // that advanced the bring-up (agent started, shutdown acknowledged) leaves a stage no later
                    // caller can resume, so its lease is released and the next connection handshakes afresh — the
                    // same contract a re-enumerated port offers.
                    // 只有仍处于纯身份阶段的 Bootloader 会话才被暂存：已推进启动流程的操作（代理已启动、关机已确认）
                    // 留下的是后续调用方无法续用的阶段，因此释放其租约，下一次连接重新握手——
                    // 与重枚举端口的契约一致。
                    if (succeeded && !entry.Removed && RequireSession(lease).State == MtkSessionState.Bootloader
                        && LeaseIsLive(lease))
                    {
                        Park(entry, lease);
                    }
                    else
                    {
                        await lease.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                Release(entry);
            }
        }
    }

    private Entry GetEntry(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Mtk)
        {
            throw new ArgumentException("An MTK endpoint is required.", nameof(device));
        }

        return GetEntryByKey(device.Key);
    }

    private Entry GetEntryByKey(string key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }

            return entry;
        }
    }

    /// <summary>Notifies subscribers that a parked handshake state changed. <para>暂存握手状态变化时通知订阅方。</para></summary>
    private void Publish()
    {
        if (!_disposed)
        {
            changed();
        }
    }

    private MtkSession RequireSession(DeviceLease lease)
        => lease.TryGetSession<MtkSession>(out var session)
            ? session : throw new IOException("The endpoint has no MTK session.");

    private async Task DisposeParkedAsync(Entry entry)
    {
        var parked = entry.Parked;
        ClearParked(entry);
        if (parked is not null)
        {
            await parked.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Release(Entry entry)
    {
        try
        {
            if (entry.Removed)
            {
                var parked = entry.Parked;
                ClearParked(entry);
                parked?.Dispose();
            }
        }
        finally
        {
            entry.Operation.Release();
        }
    }

    /// <summary>Forgets every device that is not in <paramref name="keys"/>, disposing the leases they still
    /// hold, and reclaims parked leases that have gone idle.
    /// <para>忘记不在 <paramref name="keys"/> 中的所有设备并释放其仍持有的租约，同时回收已空闲的暂存租约。</para>
    /// </summary>
    /// <param name="keys">The device keys observed by the latest scan. <para>最新扫描观测到的设备键。</para></param>
    /// <param name="removePortEntries">Also removes the port-keyed entries; only a full teardown passes true,
    /// because a live port lease may still hold a handshake window its device has not appeared in a scan for yet.
    /// <para>是否同时移除端口键条目；只有整体拆除才传 true，因为在线的端口租约可能仍持有握手窗口，
    /// 而其设备尚未出现在任何扫描中。</para></param>
    internal void RetainDevices(IReadOnlySet<string> keys, bool removePortEntries = false)
    {
        List<Entry> removed = [];
        List<Entry> idle = [];
        var now = Environment.TickCount64;
        lock (_gate)
        {
            foreach (var (key, entry) in _entries.ToArray())
            {
                if (key.StartsWith(PortKeyPrefix, StringComparison.Ordinal)
                    || key.StartsWith(ArrivalKeyPrefix, StringComparison.Ordinal))
                {
                    if (removePortEntries)
                    {
                        entry.Removed = true;
                        _entries.Remove(key);
                        removed.Add(entry);
                        continue;
                    }

                    if (entry.Parked is not null
                        && now - Volatile.Read(ref entry.ParkedAtMilliseconds) >= ParkedLeaseIdleLimitMilliseconds)
                    {
                        idle.Add(entry);
                    }
                    continue;
                }

                if (!keys.Contains(key))
                {
                    entry.Removed = true;
                    _entries.Remove(key);
                    removed.Add(entry);
                }
                else if (entry.Parked is not null
                    && now - Volatile.Read(ref entry.ParkedAtMilliseconds) >= ParkedLeaseIdleLimitMilliseconds)
                {
                    idle.Add(entry);
                }
            }
        }
        foreach (var entry in removed)
        {
            if (entry.Operation.Wait(0))
            {
                Release(entry);
            }
        }
        foreach (var entry in idle)
        {
            ReclaimIdleParkedLease(entry);
        }
    }

    private void ReclaimIdleParkedLease(Entry entry)
    {
        if (!entry.Operation.Wait(0))
        {
            return;
        }

        try
        {
            if (entry.Removed)
            {
                var abandoned = entry.Parked;
                ClearParked(entry);
                abandoned?.Dispose();
                return;
            }

            var parked = entry.Parked;
            if (parked is null
                || Environment.TickCount64 - Volatile.Read(ref entry.ParkedAtMilliseconds) < ParkedLeaseIdleLimitMilliseconds)
            {
                return;
            }

            ClearParked(entry);
            parked.Dispose();
        }
        finally
        {
            entry.Operation.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        RetainDevices(new HashSet<string>(StringComparer.Ordinal), removePortEntries: true);
    }
}