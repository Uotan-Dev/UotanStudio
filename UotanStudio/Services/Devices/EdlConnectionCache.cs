using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>Preserves probe handshakes until the next operation on the same physical endpoint.</summary>
internal sealed class EdlConnectionCache(
    Func<DetectedDevice, CancellationToken, Task<DeviceLease>> open,
    Action changed) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Operation = new(1, 1);
        internal DeviceLease? Parked;
        internal volatile EdlProtocolState State;
        internal volatile bool Removed;
    }

    internal EdlProtocolState GetState(string key)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.State : EdlProtocolState.Unknown;
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
            if (entry.Parked?.Session is EdlSession { IsConnected: false })
            {
                await entry.Parked.DisposeAsync().ConfigureAwait(false);
                entry.Parked = null;
                Publish(entry, EdlProtocolState.Unknown);
            }
            entry.Parked ??= await open(device, cancellationToken).ConfigureAwait(false);
            var session = RequireSession(entry.Parked);
            var state = await session.ProbeProtocolAsync(cancellationToken).ConfigureAwait(false);
            Publish(entry, state);
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
            if (lease?.Session is EdlSession { IsConnected: false })
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