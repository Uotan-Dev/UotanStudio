using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Mtk;
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
internal sealed class MtkConnectionCache(
    Func<DetectedDevice, CancellationToken, Task<DeviceLease>> open,
    Action changed,
    Func<UsbDeviceInfo, int, CancellationToken, Task<DeviceLease?>>? vcomOpen = null,
    Func<DeviceLease, bool>? leaseIsCurrent = null) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

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

    private bool LeaseIsLive(DeviceLease lease)
    {
        if (!lease.TryGetSession<MtkSession>(out var session) || !session.IsConnected)
        {
            return false;
        }

        var probe = leaseIsCurrent ?? LeaseCurrentProbe;
        return probe is null || probe(lease);
    }

    internal async Task<DeviceLease> HandshakeAsync(DetectedDevice device, CancellationToken cancellationToken)
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

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(device.Key, out var entry))
            {
                entry = new Entry();
                _entries.Add(device.Key, entry);
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

    internal void RetainDevices(IReadOnlySet<string> keys)
    {
        List<Entry> removed = [];
        List<Entry> idle = [];
        var now = Environment.TickCount64;
        lock (_gate)
        {
            foreach (var (key, entry) in _entries.ToArray())
            {
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
        RetainDevices(new HashSet<string>(StringComparer.Ordinal));
    }
}