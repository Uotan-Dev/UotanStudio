namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// Remembers the serial a Windows USB interface reported, so a later enumeration that cannot read it again keeps the
/// same identity instead of degrading the device to a <c>VID:PID</c> label. Entries are scoped to the device instance
/// id, which Windows changes when the device is re-plugged, so a stale serial is never carried to another connection.
/// <para>记住 Windows USB 接口上报过的序列号，使之后无法再次读取的枚举保持同一身份，而不是降级成 <c>VID:PID</c>
/// 标签。条目按设备实例 ID 限定；设备重新插入时 Windows 会改变该 ID，因此过期的序列号不会带到另一次连接上。</para>
/// </summary>
internal static class UsbSerialCache
{
    /// <summary>Upper bound of remembered interfaces; the oldest entry is dropped beyond it.
    /// <para>记住的接口数量上限；超出后丢弃最旧的条目。</para>
    /// </summary>
    private const int Capacity = 64;

    /// <summary>How long a successfully read serial stays usable for the same instance id.
    /// <para>成功读到的序列号在同一实例 ID 下可复用的时长。</para>
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long a failed read suppresses another attempt for the same instance id, so one scan does not
    /// probe the same busy interface twice.
    /// <para>读取失败后同一实例 ID 抑制再次尝试的时长，使一次扫描不会对同一个被占用的接口探测两次。</para>
    /// </summary>
    private static readonly TimeSpan UnavailableLifetime = TimeSpan.FromSeconds(2);

    private static readonly Lock _sync = new();
    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Entry(string? InstanceId, string? Serial, long Stamp);

    /// <summary>Looks up the serial remembered for the interface and instance id.
    /// <para>查询为该接口与实例 ID 记住的序列号。</para>
    /// </summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <param name="instanceId">Device instance id observed now; entries recorded without one still match.
    /// <para>本次观测到的设备实例 ID；未记录实例 ID 的条目仍然匹配。</para></param>
    /// <param name="nowTicks">Current monotonic tick count. <para>当前单调计数值。</para></param>
    /// <param name="serial">The remembered serial when the method returns <see langword="true"/>.
    /// <para>方法返回 <see langword="true"/> 时记住的序列号。</para></param>
    /// <returns><see langword="true"/> when a live positive entry matches.
    /// <para>存在有效正向条目并匹配时为 <see langword="true"/>。</para></returns>
    internal static bool TryGet(string path, string? instanceId, long nowTicks, out string? serial)
    {
        lock (_sync)
        {
            serial = null;
            if (!_entries.TryGetValue(path, out var entry) || entry.Serial is null)
            {
                return false;
            }

            if (!Matches(entry, instanceId) || nowTicks - entry.Stamp > Lifetime.TotalMilliseconds)
            {
                return false;
            }

            serial = entry.Serial;
            return true;
        }
    }

    /// <summary>Suppresses another read attempt for the interface while the last failure is recent.
    /// <para>在最近一次失败仍然新鲜时抑制对该接口的再次读取。</para>
    /// </summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <param name="instanceId">Device instance id observed now. <para>本次观测到的设备实例 ID。</para></param>
    /// <param name="nowTicks">Current monotonic tick count. <para>当前单调计数值。</para></param>
    /// <returns><see langword="true"/> when the caller must not probe again yet.
    /// <para>调用方此刻不得再次探测时为 <see langword="true"/>。</para></returns>
    internal static bool IsRecentlyUnavailable(string path, string? instanceId, long nowTicks)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(path, out var entry) && entry.Serial is null &&
                Matches(entry, instanceId) &&
                nowTicks - entry.Stamp <= UnavailableLifetime.TotalMilliseconds;
        }
    }

    /// <summary>Remembers one successfully read serial. <para>记住一次成功读取的序列号。</para></summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <param name="instanceId">Device instance id the serial was observed under.
    /// <para>观测到该序列号时的设备实例 ID。</para></param>
    /// <param name="serial">The serial to remember; blank values are ignored.
    /// <para>要记住的序列号；空白值被忽略。</para></param>
    /// <param name="nowTicks">Current monotonic tick count. <para>当前单调计数值。</para></param>
    internal static void Remember(string path, string? instanceId, string serial, long nowTicks)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return;
        }

        lock (_sync)
        {
            Prune(nowTicks);
            _entries[path] = new Entry(string.IsNullOrWhiteSpace(instanceId) ? null : instanceId, serial, nowTicks);
        }
    }

    /// <summary>Remembers one failed read so the same scan does not retry it immediately.
    /// <para>记住一次读取失败，使同一次扫描不会立刻重试。</para>
    /// </summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <param name="instanceId">Device instance id observed now. <para>本次观测到的设备实例 ID。</para></param>
    /// <param name="nowTicks">Current monotonic tick count. <para>当前单调计数值。</para></param>
    internal static void RememberUnavailable(string path, string? instanceId, long nowTicks)
    {
        lock (_sync)
        {
            Prune(nowTicks);
            _entries[path] = new Entry(string.IsNullOrWhiteSpace(instanceId) ? null : instanceId, null, nowTicks);
        }
    }

    /// <summary>Drops every entry; used by tests and by callers that must not keep identity across an unbind.
    /// <para>清除所有条目；供测试以及不得跨解绑保留身份的调用方使用。</para>
    /// </summary>
    internal static void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    private static bool Matches(Entry entry, string? instanceId)
        => entry.InstanceId is null ||
            (instanceId is not null && string.Equals(entry.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

    private static void Prune(long nowTicks)
    {
        if (_entries.Count < Capacity)
        {
            foreach (var stale in _entries.Where(pair => nowTicks - pair.Value.Stamp > Lifetime.TotalMilliseconds).ToArray())
            {
                _entries.Remove(stale.Key);
            }

            return;
        }

        var oldest = _entries.MinBy(pair => pair.Value.Stamp);
        _entries.Remove(oldest.Key);
    }
}