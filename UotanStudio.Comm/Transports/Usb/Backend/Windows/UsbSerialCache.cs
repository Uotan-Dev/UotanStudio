namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// Remembers what one bounded, non-claiming descriptor probe read from a Windows USB interface — the iSerial serial and
/// the device descriptor's <c>bcdUSB</c> release number — so a later enumeration that cannot open the interface again
/// keeps the same identity and mode evidence instead of degrading the device to a <c>VID:PID</c> label or dropping the
/// only discriminator a boot ROM has between its mask ROM and loader stages. Entries are scoped to the device instance
/// id, which Windows changes when the device is re-plugged, so a stale value is never carried to another connection.
/// <para>记住一次有界、不认领接口的描述符探测从 Windows USB 接口读到的内容——iSerial 序列号与设备描述符的
/// <c>bcdUSB</c> 版本号——使之后无法再次打开该接口的枚举保持同一身份与模式证据，而不是降级成 <c>VID:PID</c>
/// 标签或丢失 boot ROM 区分 mask ROM 与 loader 阶段的唯一依据。条目按设备实例 ID 限定；设备重新插入时 Windows
/// 会改变该 ID，因此过期取值不会带到另一次连接上。</para>
/// </summary>
internal static class UsbSerialCache
{
    /// <summary>Upper bound of remembered interfaces; the oldest entry is dropped beyond it.
    /// <para>记住的接口数量上限；超出后丢弃最旧的条目。</para>
    /// </summary>
    private const int Capacity = 64;

    /// <summary>How long a successfully read value stays usable for the same instance id.
    /// <para>成功读到的取值在同一实例 ID 下可复用的时长。</para>
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long a failed read suppresses another attempt for the same instance id, so one scan does not
    /// probe the same busy interface twice.
    /// <para>读取失败后同一实例 ID 抑制再次尝试的时长，使一次扫描不会对同一个被占用的接口探测两次。</para>
    /// </summary>
    private static readonly TimeSpan UnavailableLifetime = TimeSpan.FromSeconds(2);

    private static readonly Lock _sync = new();
    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Entry(string? InstanceId, string? Serial, ushort? BcdUsb, bool Unavailable, long Stamp);

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
            if (!_entries.TryGetValue(path, out var entry) || entry.Serial is null || entry.Unavailable)
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

    /// <summary>Looks up the <c>bcdUSB</c> value remembered for the interface and instance id.
    /// <para>查询为该接口与实例 ID 记住的 <c>bcdUSB</c> 取值。</para>
    /// </summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <param name="instanceId">Device instance id observed now; entries recorded without one still match.
    /// <para>本次观测到的设备实例 ID；未记录实例 ID 的条目仍然匹配。</para></param>
    /// <param name="nowTicks">Current monotonic tick count. <para>当前单调计数值。</para></param>
    /// <param name="bcdUsb">The remembered <c>bcdUSB</c> value when the method returns <see langword="true"/>.
    /// <para>方法返回 <see langword="true"/> 时记住的 <c>bcdUSB</c> 取值。</para></param>
    /// <returns><see langword="true"/> when a live entry carries a <c>bcdUSB</c> value for this instance.
    /// <para>存在为该实例携带 <c>bcdUSB</c> 取值的有效条目时为 <see langword="true"/>。</para></returns>
    internal static bool TryGetBcdUsb(string path, string? instanceId, long nowTicks, out ushort? bcdUsb)
    {
        lock (_sync)
        {
            bcdUsb = null;
            if (!_entries.TryGetValue(path, out var entry) || entry.BcdUsb is null)
            {
                return false;
            }

            if (!Matches(entry, instanceId) || nowTicks - entry.Stamp > Lifetime.TotalMilliseconds)
            {
                return false;
            }

            bcdUsb = entry.BcdUsb;
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
            return _entries.TryGetValue(path, out var entry) && entry.Unavailable &&
                Matches(entry, instanceId) &&
                nowTicks - entry.Stamp <= UnavailableLifetime.TotalMilliseconds;
        }
    }

    /// <summary>Remembers one successfully read serial, keeping any <c>bcdUSB</c> value already known for the entry.
    /// <para>记住一次成功读取的序列号，并保留该条目已知的 <c>bcdUSB</c> 取值。</para>
    /// </summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <param name="instanceId">Device instance id the serial was observed under.
    /// <para>观测到该序列号时的设备实例 ID。</para></param>
    /// <param name="serial">The serial to remember; blank values are ignored.
    /// <para>要记住的序列号；空白值被忽略。</para></param>
    /// <param name="nowTicks">Current monotonic tick count. <para>当前单调计数值。</para></param>
    internal static void Remember(string path, string? instanceId, string serial, long nowTicks)
        => RememberDescriptors(path, instanceId, serial, bcdUsb: null, nowTicks);

    /// <summary>Remembers what one descriptor probe read: the serial and the <c>bcdUSB</c> value. A blank serial or an
    /// absent <c>bcdUSB</c> keeps whatever the entry already carried, and a successful read clears the unavailable mark.
    /// <para>记住一次描述符探测读到的内容：序列号与 <c>bcdUSB</c> 取值。空白序列号或缺席的 <c>bcdUSB</c>
    /// 保留条目原有取值，读取成功会清除“不可用”标记。</para>
    /// </summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <param name="instanceId">Device instance id observed now. <para>本次观测到的设备实例 ID。</para></param>
    /// <param name="serial">The serial that was read, or <see langword="null"/> when the probe read none.
    /// <para>读到的序列号；探测未读到时为 <see langword="null"/>。</para></param>
    /// <param name="bcdUsb">The <c>bcdUSB</c> value that was read, or <see langword="null"/> when it was not read.
    /// <para>读到的 <c>bcdUSB</c> 取值；未读到时为 <see langword="null"/>。</para></param>
    /// <param name="nowTicks">Current monotonic tick count. <para>当前单调计数值。</para></param>
    internal static void RememberDescriptors(string path, string? instanceId, string? serial, ushort? bcdUsb,
        long nowTicks)
    {
        var hasSerial = !string.IsNullOrWhiteSpace(serial);
        if (!hasSerial && bcdUsb is null)
        {
            return;
        }

        lock (_sync)
        {
            Prune(nowTicks);
            var kept = _entries.TryGetValue(path, out var existing) && Matches(existing, instanceId)
                ? existing
                : default;
            _entries[path] = new Entry(
                string.IsNullOrWhiteSpace(instanceId) ? null : instanceId,
                hasSerial ? serial : kept.Serial,
                bcdUsb ?? kept.BcdUsb,
                Unavailable: false,
                nowTicks);
        }
    }

    /// <summary>Remembers one failed read so the same scan does not retry it immediately, keeping the identity the
    /// entry already carried.
    /// <para>记住一次读取失败，使同一次扫描不会立刻重试，同时保留条目原有的身份信息。</para>
    /// </summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <param name="instanceId">Device instance id observed now. <para>本次观测到的设备实例 ID。</para></param>
    /// <param name="nowTicks">Current monotonic tick count. <para>当前单调计数值。</para></param>
    internal static void RememberUnavailable(string path, string? instanceId, long nowTicks)
    {
        lock (_sync)
        {
            Prune(nowTicks);
            var kept = _entries.TryGetValue(path, out var existing) && Matches(existing, instanceId)
                ? existing
                : default;
            _entries[path] = new Entry(
                string.IsNullOrWhiteSpace(instanceId) ? null : instanceId,
                kept.Serial,
                kept.BcdUsb,
                Unavailable: true,
                nowTicks);
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