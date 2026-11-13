using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Preserves probe handshakes until the next operation on the same physical endpoint. A parked lease is reused
/// only while its typed EDL session is live and its generation probe still matches.
/// <para>在同一物理端点的下一次操作之前保留探测握手。仅当类型化 EDL 会话仍在线且代际探针仍然匹配时，才会复用暂存的租约。</para>
/// </summary>
/// <param name="open">Opens a fresh lease for the endpoint.<para>为该端点打开一个新的租约。</para></param>
/// <param name="changed">Notifies about publishable state changes.<para>通知可发布的状态变化。</para></param>
/// <param name="leaseIsCurrent">Reports whether a parked lease's transport generation is still current.<para>报告暂存租约的传输代际是否仍然有效。</para></param>
internal sealed class EdlConnectionCache(
    Func<DetectedDevice, CancellationToken, Task<DeviceLease>> open,
    Action changed,
    Func<DeviceLease, bool>? leaseIsCurrent = null) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// How long an explicit probe waits for a probe already running on the same endpoint before reporting the state
    /// that probe leaves behind. A caller asking for the state is asking for the result of a probe, so returning the
    /// value the entry held before the in-flight probe ran reports a stale <see cref="EdlProtocolState.Unknown"/> for
    /// a target that is about to be classified — which is exactly what a caller arriving right after discovery sees,
    /// because discovery starts its own probe. The window covers one full probe (read budget, wake and the loader
    /// transition) without letting a pathological holder block the caller; the caller's token still bounds it.
    /// <para>显式探测在同一端点上已有探测运行时最多等多久，之后回报那次探测留下的状态。向调用方索取状态就是索取一次
    /// 探测的结果，因此返回正在进行的探测运行之前的旧值，会把一个即将被分类的目标报成过期的
    /// <see cref="EdlProtocolState.Unknown"/>——而这正是设备刚被发现后立刻赶到的调用方所看到的情形，因为发现流程
    /// 自己会启动一次探测。该窗口覆盖一次完整探测（读取预算、唤醒与 loader 切换），又不让异常持有者阻塞调用方；
    /// 调用方自己的令牌仍是等待的真正上界。</para>
    /// </summary>
    private const int ProbeJoinBudgetMs = 6_000;

    /// <summary>
    /// Optional generation-liveness probe supplied after construction; the constructor delegate wins when both are
    /// present. Without a probe, liveness relies on the session's own connection state.
    /// <para>构造之后提供的可选代际判活探针；与构造参数同时存在时构造参数优先。没有探针时仅按会话自身的连接状态判活。</para>
    /// </summary>
    /// <value>The generation-liveness probe, or <c>null</c> when generation is not checked.<para>代际判活探针；不检查代际时为 <c>null</c>。</para></value>
    internal Func<DeviceLease, bool>? LeaseCurrentProbe { get; set; }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Operation = new(1, 1);
        internal DeviceLease? Parked;
        internal long ParkedAtMilliseconds;
        internal volatile EdlProtocolState State;
        internal volatile EdlDetailsSnapshot? Details;
        internal volatile bool Removed;
    }

    private const long ParkedLeaseIdleLimitMilliseconds = 60_000;

    /// <summary>
    /// Upper bound applied to the relink offer's own timeout so one failed run can never park the
    /// caller on a wait measured in hours.
    /// <para>对重链提议自带超时的上限，使一次失败的运行绝不会把调用方挂在以小时计的等待上。</para>
    /// </summary>
    private const int MaximumRelinkWaitMilliseconds = 120_000;

    /// <summary>
    /// How many re-enumeration rebuilds one run may consume; one relink attempt keeps the operation
    /// bounded while still covering the single post-programmer mode switch of a flash run.
    /// <para>一次运行可消费的重枚举重建次数；单次重链尝试既覆盖刷写运行唯一一次写入 programmer 后的
    /// 模式切换，又让操作保持有界。</para>
    /// </summary>
    private const int MaximumRelinkAttempts = 1;

    /// <summary>
    /// Gets or sets the delegate that waits for the re-enumerated replacement endpoint of a failed link
    /// and opens a lease on it. It receives the consumed relink offer, the cooperative wait deadline in
    /// milliseconds and the cancellation token, and returns <see langword="null"/> when the device did
    /// not re-enumerate in time or the evidence cannot be correlated. When unset, failures keep the
    /// original behaviour of publishing the offer and releasing the lease without waiting.
    /// <para>获取或设置等待失败链路的重枚举替代端点并在其上打开租约的委托。它收到已消费的重链提议、
    /// 协作式等待的毫秒截止时间与取消令牌，设备未按时重枚举或证据无法关联时返回
    /// <see langword="null"/>。未设置时，失败保持原有行为：发布提议并释放租约，不等待。</para>
    /// </summary>
    internal Func<EdlRelinkOffer, int, CancellationToken, Task<DeviceLease?>>? ReconnectWaiter { get; set; }

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
        if (!lease.TryGetSession<EdlSession>(out var session) || !session.IsConnected)
        {
            return false;
        }

        var probe = leaseIsCurrent ?? LeaseCurrentProbe;
        return probe is null || probe(lease);
    }

    internal EdlProtocolState GetState(string key)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.State : EdlProtocolState.Unknown;
        }
    }

    /// <summary>
    /// Returns the read-only detail snapshot collected alongside the endpoint's latest probe.
    /// <para>返回该端点最近一次探测附加采集到的只读详情快照。</para>
    /// </summary>
    /// <param name="key">The endpoint cache key.<para>端点缓存键。</para></param>
    /// <returns>The collected detail snapshot, or <c>null</c> when none exists.<para>已采集的详情快照；不存在时为 <c>null</c>。</para></returns>
    internal EdlDetailsSnapshot? GetDetails(string key)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.Details : null;
        }
    }

    private Entry GetEntry(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Edl)
        {
            throw new ArgumentException("An EDL endpoint is required.", nameof(device));
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

    internal async Task<EdlProtocolState> ProbeAsync(DetectedDevice device, CancellationToken cancellationToken)
    {
        var entry = GetEntry(device);
        if (!await entry.Operation.WaitAsync(ProbeJoinBudgetMs, cancellationToken).ConfigureAwait(false))
        {
            // A probe on this endpoint has been running longer than one should take; report what it leaves behind
            // instead of opening a second probe on the same handle.
            // 该端点上的探测已超过一次探测应有的时长；回报它留下的状态，而不是在同一个句柄上再开一次探测。
            return entry.State;
        }
        try
        {
            if (entry.Removed)
            {
                return EdlProtocolState.Unknown;
            }
            if (entry.Parked is { } parked && !LeaseIsLive(parked))
            {
                await entry.Parked.DisposeAsync().ConfigureAwait(false);
                ClearParked(entry);
                Publish(entry, EdlProtocolState.Unknown);
            }
            if (entry.Parked is null)
            {
                Park(entry, await open(device, cancellationToken).ConfigureAwait(false));
            }
            var sessionLease = entry.Parked
                ?? throw new InvalidOperationException("EDL probe did not obtain a lease.");
            var session = RequireSession(sessionLease);
            var state = await session.ProbeProtocolAsync(cancellationToken).ConfigureAwait(false);
            Publish(entry, state);
            try
            {
                await CollectDetailsAsync(entry, session, state, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogService.Instance.Write(LogLevel.Debug, "DeviceManager",
                    $"EDL detail collection for '{device.Key}' was cancelled; the verified {state} state is kept.");
            }
            return entry.Removed ? EdlProtocolState.Unknown : state;
        }
        catch
        {
            PublishFailure(entry, device.Key, "probe");
            throw;
        }
        finally
        {
            Release(entry);
        }
    }

    internal async Task<TResult> RunAsync<TResult>(DetectedDevice device,
        Func<EdlSession, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var entry = GetEntry(device);
        await entry.Operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        DeviceLease? lease = null;
        EdlSession? session = null;
        var succeeded = false;
        void OnProtocolChanged(object? sender, EdlProtocolState state) => Publish(entry, state);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (entry.Removed)
                    {
                        throw new IOException("The EDL endpoint was removed.");
                    }
                    if (lease is null)
                    {
                        lease = TakeParked(entry);
                        if (lease is not null && !LeaseIsLive(lease))
                        {
                            await lease.DisposeAsync().ConfigureAwait(false);
                            lease = null;
                            Publish(entry, EdlProtocolState.Unknown);
                        }
                        lease ??= await open(device, cancellationToken).ConfigureAwait(false);
                        if (entry.Removed)
                        {
                            throw new IOException("The EDL endpoint was removed.");
                        }
                    }
                    session = RequireSession(lease);
                    session.ProtocolStateChanged += OnProtocolChanged;
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await operation(session).ConfigureAwait(false);
                    Publish(entry, session.ProtocolState);
                    succeeded = true;
                    return result;
                }
                catch (Exception failure) when (attempt < MaximumRelinkAttempts
                    && failure is not OperationCanceledException
                    && TakeRelinkOffer(session, failure, out var offer))
                {
                    LogService.Instance.Write(LogLevel.Info, "DeviceManager",
                        $"EDL operation on '{device.Key}' failed with reconnection evidence; consuming the relink offer " +
                        $"and waiting up to {Math.Min((int)offer.Timeout.TotalMilliseconds, MaximumRelinkWaitMilliseconds)} ms " +
                        "for the re-enumerated endpoint.");
                    if (session is not null)
                    {
                        session.ProtocolStateChanged -= OnProtocolChanged;
                        session = null;
                    }
                    if (lease is not null)
                    {
                        await lease.DisposeAsync().ConfigureAwait(false);
                        lease = null;
                    }

                    var replacement = await OpenReconnectedLeaseAsync(offer, cancellationToken).ConfigureAwait(false);
                    if (replacement is null)
                    {
                        LogService.Instance.Write(LogLevel.Warn, "DeviceManager",
                            $"EDL relink for '{device.Key}' produced no replacement endpoint; the original failure is reported.");
                        throw;
                    }

                    lease = replacement;
                    Publish(entry, EdlProtocolState.Unknown);
                }
            }
        }
        catch
        {
            PublishFailure(entry, device.Key, "operation");
            throw;
        }
        finally
        {
            try
            {
                if (session is not null)
                {
                    session.ProtocolStateChanged -= OnProtocolChanged;
                }
                if (lease is not null)
                {
                    if (succeeded && session is not null && !entry.Removed && LeaseIsLive(lease))
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

    /// <summary>
    /// Consumes the failed session's relink offer when the failure carries reconnection evidence: the
    /// session already faulted, or the failure classifies as a lost connection, a lost completion, or an
    /// unknown execution state. Without a configured reconnection waiter the offer is left in place so a
    /// later consumer can still act on it.
    /// <para>当失败携带重连证据时取走失败会话的重链提议：会话已失效，或失败被归类为连接丢失、完成丢失或
    /// 未知的执行状态。未配置重连等待委托时提议保留原处，后续消费者仍可行动。</para>
    /// </summary>
    /// <param name="session">The session the operation ran on, or <see langword="null"/> when it never opened.
    /// <para>运行操作的会话；未打开时为 <see langword="null"/>。</para></param>
    /// <param name="failure">The observed failure.<para>观测到的失败。</para></param>
    /// <param name="offer">The consumed offer when this method returns <see langword="true"/>.
    /// <para>本方法返回 <see langword="true"/> 时为已取走的提议。</para></param>
    /// <returns><see langword="true"/> when a relinkable offer was consumed.
    /// <para>已取走可重链的提议时为 <see langword="true"/>。</para></returns>
    private bool TakeRelinkOffer(EdlSession? session, Exception failure, out EdlRelinkOffer offer)
    {
        offer = null!;
        if (ReconnectWaiter is null || session is null)
        {
            return false;
        }

        var sessionFaulted = session.Firehose?.RequiresReconnect == true;
        var classified = EdlFailureClassification.Classify(failure);
        if (!sessionFaulted
            && classified is not (EdlFailureKind.ConnectionLost or EdlFailureKind.CompletionLost
                or EdlFailureKind.ExecutionUnknown or EdlFailureKind.CancelledAfterTransfer))
        {
            return false;
        }

        var taken = session.TakeRelinkOffer();
        if (taken?.PreviousEndpoint is null)
        {
            return false;
        }

        offer = taken;
        return true;
    }

    /// <summary>
    /// Waits, bounded by the offer timeout, for the re-enumerated replacement endpoint through the
    /// configured waiter and reports the opened lease; every waiter failure degrades to
    /// <see langword="null"/> so the original operation failure is surfaced unchanged.
    /// <para>通过已配置的等待委托在提议超时内等待重枚举出的替代端点并返回打开的租约；等待的任何失败都
    /// 降级为 <see langword="null"/>，使原始操作失败原样上报。</para>
    /// </summary>
    /// <param name="offer">The consumed relink offer carrying the previous endpoint.
    /// <para>已取走、携带旧端点的重链提议。</para></param>
    /// <param name="cancellationToken">Cancels the wait.<para>取消等待。</para></param>
    /// <returns>The lease opened on the replacement endpoint, or <see langword="null"/>.
    /// <para>在替代端点上打开的租约；或为 <see langword="null"/>。</para></returns>
    private async Task<DeviceLease?> OpenReconnectedLeaseAsync(EdlRelinkOffer offer, CancellationToken cancellationToken)
    {
        var waiter = ReconnectWaiter!;
        var timeoutMs = (int)Math.Clamp(offer.Timeout.TotalMilliseconds, 1, MaximumRelinkWaitMilliseconds);
        try
        {
            return await waiter(offer, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or NotSupportedException or InvalidOperationException
            or IOException or ArgumentException)
        {
            LogService.Instance.Write(LogLevel.Info, "DeviceManager",
                $"EDL relink wait for '{offer.PreviousEndpoint?.Id}' ended without a replacement: {ex.Message}");
            return null;
        }
    }

    private void Publish(Entry entry, EdlProtocolState state)
    {
        var notify = false;
        lock (_gate)
        {
            if (!_disposed && !entry.Removed && entry.State != state)
            {
                entry.State = state;
                notify = true;
            }
        }
        if (notify)
        {
            changed();
        }
    }

    /// <summary>
    /// Keeps the last stage that was actually observed when a probe or operation fails, so a transient timeout cannot
    /// turn a known Sahara or Firehose endpoint back into <see cref="EdlProtocolState.Unknown"/>. Entries are dropped
    /// when the endpoint disappears, so a retained stage never describes a different device.
    /// <para>探测或操作失败时保留最后一次真正观测到的阶段，使一次瞬时超时不会把已知的 Sahara 或 Firehose 端点
    /// 变回 <see cref="EdlProtocolState.Unknown"/>。端点消失时条目会被移除，因此保留的阶段绝不会描述另一台设备。</para>
    /// </summary>
    /// <param name="entry">The endpoint entry.<para>端点条目。</para></param>
    /// <param name="key">The endpoint cache key, for the diagnostic log.<para>端点缓存键，用于诊断日志。</para></param>
    /// <param name="source">Which operation failed.<para>失败的操作。</para></param>
    private void PublishFailure(Entry entry, string key, string source)
    {
        if (entry.State == EdlProtocolState.Unknown)
        {
            return;
        }

        LogService.Instance.Write(LogLevel.Debug, "DeviceManager",
            $"EDL {source} for '{key}' ended without a conclusion; keeping the last observed stage {entry.State}.");
    }

    /// <summary>
    /// Collects read-only device details after a probe from the captured HELLO or the running programmer.
    /// Failures degrade to no details without changing the probed state.
    /// <para>在探测之后，从已捕获的 HELLO 或正在运行的编程器采集只读设备详情。采集失败只会降级为无详情，不改变探测得到的状态。</para>
    /// </summary>
    /// <param name="entry">The endpoint entry that stores the snapshot.<para>保存快照的端点条目。</para></param>
    /// <param name="session">The session the probe produced.<para>探测得到的会话。</para></param>
    /// <param name="state">The protocol state reported by the probe.<para>探测上报的协议状态。</para></param>
    /// <param name="cancellationToken">Cancels the detail reads.<para>用于取消详情读取。</para></param>
    private static async Task CollectDetailsAsync(Entry entry, EdlSession session, EdlProtocolState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var portName = session.UnderlyingSerialPortName;
            EdlDetailsSnapshot? details = state switch
            {
                EdlProtocolState.Sahara or EdlProtocolState.SaharaMemoryDebug
                    when session.SaharaHandshake is { } handshake
                    => new EdlDetailsSnapshot { Sahara = handshake, SerialPortName = portName },
                EdlProtocolState.Streaming => portName is null ? null : new EdlDetailsSnapshot { SerialPortName = portName },
                EdlProtocolState.Firehose => await CollectFirehoseDetailsAsync(session, portName, cancellationToken)
                    .ConfigureAwait(false),
                _ => portName is null ? null : new EdlDetailsSnapshot { SerialPortName = portName },
            };
            if (details is not null)
            {
                entry.Details = details;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Instance.Write(LogLevel.Debug, "DeviceManager",
                $"EDL detail collection failed: {ex.Message}");
        }
    }

    private static async Task<EdlDetailsSnapshot> CollectFirehoseDetailsAsync(EdlSession session,
        string? serialPortName, CancellationToken cancellationToken)
    {
        if (session.VendorAuthenticationRequired)
        {
            return new EdlDetailsSnapshot
            {
                Capabilities = session.Capabilities,
                SerialPortName = serialPortName,
                RequiresVendorAuthentication = true,
            };
        }

        try
        {
            await session.InitializeAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (session.VendorAuthenticationRequired)
        {
            return new EdlDetailsSnapshot
            {
                Capabilities = session.Capabilities,
                SerialPortName = serialPortName,
                RequiresVendorAuthentication = true,
            };
        }

        if (session.Backend == EdlDownloadBackend.StreamDload || session.Firehose is not { } firehose)
        {
            return new EdlDetailsSnapshot
            {
                Capabilities = session.Capabilities,
                SerialPortName = serialPortName,
            };
        }

        FirehoseResponse? storage = null;
        try
        {
            storage = await firehose.GetStorageInfoAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
        }
        return new EdlDetailsSnapshot
        {
            Capabilities = session.Capabilities,
            Geometry = storage?.StorageGeometry,
            Storage = firehose.LastStorageDetails,
            SerialPortName = serialPortName,
        };
    }

    private static EdlSession RequireSession(DeviceLease lease)
        => lease.Session as EdlSession ?? throw new IOException("The endpoint has no EDL session.");

    private static void Release(Entry entry)
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
            Publish(entry, EdlProtocolState.Unknown);
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