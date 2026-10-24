using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Shares one authenticated direct HDC session per endpoint. A WinUSB-bound HDC interface accepts a single
/// open handle: the second open fails with <c>ERROR_ACCESS_DENIED</c>, so a fresh session per operation made
/// the endpoint look unopenable as soon as any other direct consumer (shell, logcat, mirror, property read)
/// was alive. The HDC protocol multiplexes channels over one connection, so consumers lease the shared
/// session and open their own channel instead.
/// <para>按端点共享同一条已认证直连 HDC 会话。绑定 WinUSB 的 HDC 接口只接受一个句柄，第二次打开会以
/// <c>ERROR_ACCESS_DENIED</c> 失败，因此"每次操作新开会话"会让端点在任何其它直连消费方（shell、logcat、
/// 投屏、参数读取）存活时表现为无法打开。HDC 协议本就在一条连接上复用多个通道，因此消费方改为租用
/// 共享会话并各自打开通道。</para>
/// </summary>
internal static class HdcDirectSessionPool
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> Sessions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, object> OpenGates = new(StringComparer.Ordinal);

    /// <summary>
    /// One live direct session and the number of operations currently leasing it. A session that is no
    /// longer connected is replaced on the next acquire instead of reused.
    /// <para>一条活动直连会话及其当前租用它的操作数。不再处于连接状态的会话会在下次获取时被替换，而不是复用。</para>
    /// </summary>
    private sealed class Entry(string key, DeviceLease lease, HdcSession session)
    {
        public string Key { get; } = key;
        public DeviceLease Lease { get; } = lease;
        public HdcSession Session { get; } = session;
        public int References { get; set; } = 1;
        public bool Closed { get; set; }
        public bool Alive => !Closed && Session.IsConnected;
    }

    /// <summary>
    /// Leases the shared direct session for the device, opening it only when no live one exists. Server
    /// ownership is re-checked here so a server that appeared after discovery still wins and direct access
    /// stays fail-closed.
    /// <para>租用该设备的共享直连会话，仅在不存在活动会话时才打开。这里会重新校验 server 归属，
    /// 使发现之后出现的 server 仍然优先，直连保持失败即关闭。</para>
    /// </summary>
    /// <param name="monitor">The monitor owning the controller lease. <para>持有控制器租约的监视器。</para></param>
    /// <param name="device">The HDC target to open directly. <para>要直连打开的 HDC 目标。</para></param>
    /// <returns>The reference handle and the shared session. <para>引用句柄与共享会话。</para></returns>
    /// <exception cref="IOException">ServerOnly mode is fixed, or a server now owns the target.
    /// <para>固定为仅 server 模式，或 server 现在拥有该目标。</para></exception>
    internal static (HdcSessionHandle Handle, HdcSession Session) Acquire(DeviceMonitorService monitor, DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(device);
        if (DebugBridgeRoute.Mode == DebugBridgeRouteMode.ServerOnly)
        {
            throw new IOException(
                "The ServerOnly route mode forbids direct HDC access; open the target through the HDC server.");
        }

        if (DeviceMonitorService.UseHdcServerFor(device))
        {
            throw new IOException("HDC server appeared; direct access is prohibited. Retry through the server.");
        }

        var key = PoolKey(device);
        lock (GateFor(key))
        {
            Entry? replaced = null;
            lock (Gate)
            {
                if (Sessions.TryGetValue(key, out var existing))
                {
                    if (existing.Alive)
                    {
                        existing.References++;
                        return (HdcSessionHandle.ForDirect(() => Release(existing)), existing.Session);
                    }

                    Sessions.Remove(key);
                    existing.Closed = true;
                    replaced = existing;
                }
            }

            if (replaced is not null)
            {
                DisposeEntry(replaced);
            }

            var opened = HdcDirectConnection.OpenFresh(monitor, device);
            var entry = new Entry(key, opened.Lease, opened.Session);
            lock (Gate)
            {
                Sessions[key] = entry;
            }

            return (HdcSessionHandle.ForDirect(() => Release(entry)), entry.Session);
        }
    }

    private static void Release(Entry entry)
    {
        var dispose = false;
        lock (Gate)
        {
            if (entry.References > 0)
            {
                entry.References--;
            }

            if (entry.References == 0 &&
                Sessions.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                Sessions.Remove(entry.Key);
                entry.Closed = true;
                dispose = true;
            }
        }

        if (dispose)
        {
            DisposeEntry(entry);
        }
    }

    private static void DisposeEntry(Entry entry)
    {
        try
        {
            entry.Lease.Dispose();
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "DeviceMonitor",
                $"Releasing the shared direct HDC session failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static object GateFor(string key)
    {
        lock (Gate)
        {
            if (!OpenGates.TryGetValue(key, out var gate))
            {
                gate = new object();
                OpenGates[key] = gate;
            }

            return gate;
        }
    }

    private static string PoolKey(DetectedDevice device) =>
        device.Transport == DeviceTransportKind.Tcp ? "tcp|" + device.Serial : "usb|" + device.Key;
}