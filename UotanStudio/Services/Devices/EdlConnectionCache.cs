using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>
/// <para>Preserves probe handshakes until the next operation on the same physical endpoint. A
/// parked lease is reusable only when it carries a typed EDL session whose connection is still
/// live and, when a generation probe is wired, its transport generation still matches the
/// controller's current generation for that physical identity; a different generation means the
/// transport instantiation is gone, so the lease is retired and reopened instead of trusting
/// <c>IsConnected</c> alone.</para>
/// Preserves probe handshakes until the next operation on the same physical endpoint. A parked
/// lease is reusable only when <c>TryGetSession</c> yields a typed EDL session reporting a live
/// connection and the optional generation probe still matches the lease's transport generation;
/// a generation mismatch retires the lease and reopens rather than relying on <c>IsConnected</c>
/// alone.
/// </summary>
/// <param name="open">Opens a fresh lease for the endpoint.</param>
/// <param name="changed">Notifies the owner of publishable state changes.</param>
/// <param name="leaseIsCurrent">Reports whether a parked lease's transport generation still matches
/// the controller's current generation for the same physical identity; without contrary evidence
/// the lease is kept.</param>
internal sealed class EdlConnectionCache(
    Func<DetectedDevice, CancellationToken, Task<DeviceLease>> open,
    Action changed,
    Func<DeviceLease, bool>? leaseIsCurrent = null) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// <para>可选的代际判活探针，由宿主在服务构造后惰性接线（构造点位于监视器主文件时无法在
    /// 其它文件传参）；与构造参数同时存在时构造参数优先。探针缺失时仅按类型化会话与连接状态
    /// 判活。</para>
    /// Optional generation-liveness probe wired lazily by the host after construction (the
    /// construction site may live in a file the wiring cannot touch); the constructor delegate
    /// wins when both are present. Without any probe liveness falls back to the typed session and
    /// its connection state only.
    /// </summary>
    internal Func<DeviceLease, bool>? LeaseCurrentProbe { get; set; }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Operation = new(1, 1);
        internal DeviceLease? Parked;
        internal volatile EdlProtocolState State;
        internal volatile EdlDetailsSnapshot? Details;
        internal volatile bool Removed;
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
    /// <para>返回该端点最近一次探测附加采集到的只读详情快照。</para>
    /// Returns the read-only detail snapshot collected alongside the endpoint's latest probe.
    /// </summary>
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
        if (!await entry.Operation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
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
                entry.Parked = null;
                Publish(entry, EdlProtocolState.Unknown);
            }
            entry.Parked ??= await open(device, cancellationToken).ConfigureAwait(false);
            var session = RequireSession(entry.Parked);
            var state = await session.ProbeProtocolAsync(cancellationToken).ConfigureAwait(false);
            Publish(entry, state);
            await CollectDetailsAsync(entry, session, state, cancellationToken).ConfigureAwait(false);
            return entry.Removed ? EdlProtocolState.Unknown : state;
        }
        catch
        {
            Publish(entry, EdlProtocolState.Unknown);
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
        void OnProtocolChanged(object? sender, EdlProtocolState state) => Publish(entry, state);
        try
        {
            if (entry.Removed)
            {
                throw new IOException("The EDL endpoint was removed.");
            }
            lease = entry.Parked;
            entry.Parked = null;
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
            session = RequireSession(lease);
            session.ProtocolStateChanged += OnProtocolChanged;
            cancellationToken.ThrowIfCancellationRequested();
            var result = await operation(session).ConfigureAwait(false);
            Publish(entry, session.ProtocolState);
            return result;
        }
        catch
        {
            Publish(entry, EdlProtocolState.Unknown);
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
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                Release(entry);
            }
        }
    }

    private void Publish(Entry entry, EdlProtocolState state)
    {
        lock (_gate)
        {
            if (!_disposed && !entry.Removed && entry.State != state)
            {
                entry.State = state;
                changed();
            }
        }
    }

    /// <summary>
    /// <para>探测之后尽力附加采集只读设备详情：Sahara 模式取被动捕获的 HELLO 信息；Firehose 模式
    /// 复用已运行的编程器执行 configure 与 getstorageinfo（均为只读命令）。任何失败都只降级为
    /// "无详情"，绝不改变探测状态，也不让探测失败。</para>
    /// Best-effort read-only detail collection after the probe: Sahara mode uses the passively
    /// captured HELLO; Firehose mode reuses the already-running programmer for configure and
    /// getstorageinfo (both read-only). Any failure degrades to "no details" without changing the
    /// probed state or failing the probe.
    /// </summary>
    private static async Task CollectDetailsAsync(Entry entry, EdlSession session, EdlProtocolState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var portName = session.UnderlyingSerialPortName;
            EdlDetailsSnapshot? details = state switch
            {
                EdlProtocolState.Sahara when session.SaharaHandshake is { } handshake
                    => new EdlDetailsSnapshot { Sahara = handshake, SerialPortName = portName },
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
            // 详情采集失败不影响探测结果；保留旧快照或保持 null。
            // Detail collection failures never affect the probe result; keep the old snapshot or null.
            LogService.Instance.Write(LogLevel.Debug, "DeviceManager",
                $"EDL detail collection failed: {ex.Message}");
        }
    }

    private static async Task<EdlDetailsSnapshot> CollectFirehoseDetailsAsync(EdlSession session,
        string? serialPortName, CancellationToken cancellationToken)
    {
        await session.InitializeAsync(null, cancellationToken).ConfigureAwait(false);
        FirehoseResponse? storage = null;
        try
        {
            storage = await session.Firehose!.GetStorageInfoAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            // 存储查询是可选的附加信息；configure 能力快照已足够展示。
            // The storage query is optional enrichment; the configure capability snapshot is enough.
        }
        return new EdlDetailsSnapshot
        {
            Capabilities = session.Capabilities,
            Geometry = storage?.StorageGeometry,
            Storage = session.Firehose!.LastStorageDetails,
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
                entry.Parked?.Dispose();
                entry.Parked = null;
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
            }
        }
        foreach (var entry in removed)
        {
            if (entry.Operation.Wait(0))
            {
                Release(entry);
            }
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