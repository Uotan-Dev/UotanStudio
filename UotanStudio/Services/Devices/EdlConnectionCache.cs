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
                EdlProtocolState.Sahara when session.SaharaHandshake is { } handshake
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
        await session.InitializeAsync(null, cancellationToken).ConfigureAwait(false);
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