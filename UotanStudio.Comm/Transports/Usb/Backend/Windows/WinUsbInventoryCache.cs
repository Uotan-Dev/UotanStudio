using System.Diagnostics.CodeAnalysis;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// Remembers what the Windows enumeration learned per device instance, so the pass after the first one does
/// not re-read the registry to reclassify interfaces it has already seen. Every pass still walks every present
/// device class and every registered interface — a USB node can carry a vendor-renamed instance id, and
/// narrowing the walk would drop it — but a warm pass answers the interface triple, the container id and the
/// device-interface GUIDs from these records instead of opening the registry key again, and reuses the
/// <c>Control\DeviceClasses</c> reverse map across passes. Records are scoped to the PnP instance id, which
/// PnP rewrites when a device is re-plugged or changes mode, so a stale record never travels to another
/// connection; a record is only ever an accelerator, because anything that fails to open falls back to the
/// full detection sequence.
/// <para>记住 Windows 枚举针对每个设备实例所学到的内容，使第一轮之后的各轮无需为了重新分类已见过的接口而重读注册表。
/// 每一轮仍然遍历全部在位设备类与全部已注册接口——实例 ID 可能被厂商重命名，收窄遍历会把这类节点丢掉——但变暖后的一轮
/// 由这些记录直接给出接口三元组、容器 ID 与设备接口 GUID，不再为它们打开注册表键，并跨轮复用
/// <c>Control\DeviceClasses</c> 反向映射。记录按 PnP 实例 ID 限定，设备重新插入或切换模式时 PnP 会改写该 ID，
/// 因此过期记录绝不会带到另一次连接；记录只是加速器，打开失败时一律回退到完整探测序列。</para>
/// </summary>
internal static class WinUsbInventoryCache
{
    /// <summary>
    /// Upper bound on remembered instances; beyond it the oldest-inserted keys are dropped, so recycled
    /// topology noise cannot grow the cache without limit.
    /// <para>记住的实例数量上限；超出后丢弃最早插入的键，使被回收的拓扑噪声不会无限撑大缓存。</para>
    /// </summary>
    private const int Capacity = 1024;

    /// <summary>
    /// How long the remembered <c>Control\DeviceClasses</c> reverse map stays usable. Device interfaces are
    /// added and removed by driver installs and mode switches, which the hotplug monitor reports and
    /// invalidates immediately; the bound only covers the polling fallback, where no event carries a path.
    /// <para>记住的 <c>Control\DeviceClasses</c> 反向映射可复用的时长。设备接口的增删由驱动安装与模式切换产生，
    /// 热插拔监视会立刻报告并使其失效；该时限只覆盖没有携带路径信息事件的轮询回退路径。</para>
    /// </summary>
    private static readonly TimeSpan DeviceClassMapLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long one learned record stays usable even without a device-change event, so a driver rebind that
    /// reported nothing cannot keep answering with a stale interface triple indefinitely.
    /// <para>单条已学记录在没有设备变化事件时仍可使用的时长，使一次未上报的驱动重绑不会无限地用过期接口
    /// 三元组作答。</para>
    /// </summary>
    private static readonly TimeSpan RecordLifetime = TimeSpan.FromMinutes(10);

    private static readonly Lock Sync = new();

    /// <summary>Remembered instances keyed by their PnP instance id.</summary>
    private static readonly Dictionary<string, Record> ByInstance = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, List<Guid>>? _deviceClassGuidMap;
    private static long _deviceClassGuidMapTicks;
    private static bool _completedFullPass;

    /// <summary>
    /// What one pass learned about one device instance.
    /// <para>一轮枚举针对一个设备实例所学到的内容。</para>
    /// </summary>
    /// <param name="VendorId">Vendor id parsed from the interface path.
    /// <para>从接口路径解析的厂商 ID。</para></param>
    /// <param name="ProductId">Product id parsed from the interface path.
    /// <para>从接口路径解析的产品 ID。</para></param>
    /// <param name="InterfaceClass">Interface class recovered from <c>CompatibleIDs</c>.
    /// <para>从 <c>CompatibleIDs</c> 恢复的接口类。</para></param>
    /// <param name="InterfaceSubClass">Interface subclass recovered from <c>CompatibleIDs</c>.
    /// <para>从 <c>CompatibleIDs</c> 恢复的接口子类。</para></param>
    /// <param name="InterfaceProtocol">Interface protocol recovered from <c>CompatibleIDs</c>.
    /// <para>从 <c>CompatibleIDs</c> 恢复的接口协议。</para></param>
    /// <param name="MetadataObserved"><see langword="true"/> when the triple was recovered,
    /// <see langword="false"/> when the node was read and reported none, and <see langword="null"/> when no
    /// pass has read it yet.
    /// <para>三元组已恢复时为 <see langword="true"/>，节点已读取但未报告时为 <see langword="false"/>，
    /// 尚未有任何一轮读取过时为 <see langword="null"/>。</para></param>
    /// <param name="ContainerId">Container id read for the node, or <see langword="null"/> when the node
    /// reports none or none was read.
    /// <para>为该节点读到的容器 ID；节点未报告或尚未读取时为 <see langword="null"/>。</para></param>
    /// <param name="ContainerRead">Whether a pass already read the container id, so a reported absence is
    /// not mistaken for an unread one.
    /// <para>是否已有一轮读取过容器 ID，使「确实没有」不被误认为「尚未读取」。</para></param>
    /// <param name="BackendKind">Backend the interface was last opened through; empty when it was never
    /// opened.
    /// <para>上次打开该接口所用的后端；从未打开时为空。</para></param>
    /// <param name="InterfaceGuids">Device interface GUIDs the node registers, or <see langword="null"/> when
    /// no pass has read them yet.
    /// <para>节点注册的设备接口 GUID；尚未有任何一轮读取过时为 <see langword="null"/>。</para></param>
    /// <param name="Stamp">Monotonic tick count of the pass that last wrote this record, which bounds how
    /// long it may answer without a device-change event.
    /// <para>最后一次写入该记录的遍历单调计数，用于限制在没有设备变化事件时它还能作答多久。</para></param>
    internal sealed record Record(
        ushort VendorId,
        ushort ProductId,
        byte? InterfaceClass,
        byte? InterfaceSubClass,
        byte? InterfaceProtocol,
        bool? MetadataObserved,
        Guid? ContainerId,
        bool ContainerRead,
        string BackendKind,
        IReadOnlyList<Guid>? InterfaceGuids,
        long Stamp);

    /// <summary>
    /// Gets whether a complete pass has already been recorded, which lets every later pass run incrementally
    /// until <see cref="Reset"/>.
    /// <para>获取是否已记录过一次完整遍历；此后每轮都按增量执行，直到 <see cref="Reset"/>。</para>
    /// </summary>
    /// <value><see langword="true"/> once a full pass completed.
    /// <para>一次全量遍历完成后为 <see langword="true"/>。</para></value>
    internal static bool HasCompletedFullPass
    {
        get
        {
            lock (Sync)
            {
                return _completedFullPass;
            }
        }
    }

    /// <summary>
    /// Gets the learned record for one instance id.
    /// <para>取出某个实例 ID 已学到的记录。</para>
    /// </summary>
    /// <param name="instanceId">PnP instance id; blank never matches.
    /// <para>PnP 实例 ID；空白永不匹配。</para></param>
    /// <param name="record">The remembered record when the method returns <see langword="true"/>.
    /// <para>方法返回 <see langword="true"/> 时记住的记录。</para></param>
    /// <returns><see langword="true"/> when an earlier pass recorded this instance.
    /// <para>更早一轮确实记录过该实例时为 <see langword="true"/>。</para></returns>
    internal static bool TryGet(string? instanceId, [NotNullWhen(true)] out Record? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        lock (Sync)
        {
            if (!ByInstance.TryGetValue(instanceId, out record))
            {
                return false;
            }

            if (Environment.TickCount64 - record.Stamp > RecordLifetime.TotalMilliseconds)
            {
                ByInstance.Remove(instanceId);
                record = null;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Records the device interface GUIDs one node registers, creating the node's record when needed.
    /// <para>记录某节点注册的设备接口 GUID，必要时创建该节点的记录。</para>
    /// </summary>
    /// <param name="instanceId">PnP instance id; blank values are ignored.
    /// <para>PnP 实例 ID；空白值被忽略。</para></param>
    /// <param name="vendorId">Vendor id parsed from the interface path.
    /// <para>从接口路径解析的厂商 ID。</para></param>
    /// <param name="productId">Product id parsed from the interface path.
    /// <para>从接口路径解析的产品 ID。</para></param>
    /// <param name="interfaceGuids">The GUIDs the node registers; may be empty.
    /// <para>节点注册的 GUID；可以为空。</para></param>
    internal static void RememberInterfaceGuids(
        string? instanceId,
        ushort vendorId,
        ushort productId,
        IReadOnlyList<Guid> interfaceGuids)
    {
        ArgumentNullException.ThrowIfNull(interfaceGuids);
        Update(instanceId, (ref Record record) => record = record with
        {
            VendorId = vendorId != 0 ? vendorId : record.VendorId,
            ProductId = productId != 0 ? productId : record.ProductId,
            InterfaceGuids = interfaceGuids,
            Stamp = Environment.TickCount64
        });
    }

    /// <summary>
    /// Records the interface classification learned for one instance, merging with the GUIDs and backend the
    /// node already carries so neither pass erases the other.
    /// <para>记录为该接口学到的分类信息，并与节点已有的 GUID 与后端合并，使任意一轮都不会抹掉另一轮的成果。</para>
    /// </summary>
    /// <param name="instanceId">PnP instance id; blank values are ignored.
    /// <para>PnP 实例 ID；空白值被忽略。</para></param>
    /// <param name="vendorId">Vendor id parsed from the interface path.
    /// <para>从接口路径解析的厂商 ID。</para></param>
    /// <param name="productId">Product id parsed from the interface path.
    /// <para>从接口路径解析的产品 ID。</para></param>
    /// <param name="interfaceClass">Recovered interface class.
    /// <para>恢复出的接口类。</para></param>
    /// <param name="interfaceSubClass">Recovered interface subclass.
    /// <para>恢复出的接口子类。</para></param>
    /// <param name="interfaceProtocol">Recovered interface protocol.
    /// <para>恢复出的接口协议。</para></param>
    /// <param name="metadataObserved">Whether the triple was recovered from <c>CompatibleIDs</c>.
    /// <para>三元组是否由 <c>CompatibleIDs</c> 恢复。</para></param>
    /// <param name="containerId">Container id read for the node, or <see langword="null"/>.
    /// <para>为该节点读到的容器 ID，或 <see langword="null"/>。</para></param>
    /// <param name="backendKind">Backend the interface was opened through; empty when it was not opened, in
    /// which case a previously learned backend is kept.
    /// <para>打开该接口所用的后端；未打开时为空，此时保留先前学到的后端。</para></param>
    internal static void RememberInterfaceMetadata(
        string? instanceId,
        ushort vendorId,
        ushort productId,
        byte? interfaceClass,
        byte? interfaceSubClass,
        byte? interfaceProtocol,
        bool metadataObserved,
        Guid? containerId,
        string backendKind)
    {
        Update(instanceId, (ref Record record) => record = record with
        {
            VendorId = vendorId != 0 ? vendorId : record.VendorId,
            ProductId = productId != 0 ? productId : record.ProductId,
            InterfaceClass = interfaceClass,
            InterfaceSubClass = interfaceSubClass,
            InterfaceProtocol = interfaceProtocol,
            MetadataObserved = metadataObserved,
            ContainerId = containerId,
            ContainerRead = true,
            BackendKind = string.IsNullOrEmpty(backendKind) ? record.BackendKind : backendKind,
            Stamp = Environment.TickCount64
        });
    }

    /// <summary>
    /// Drops the records for one instance and every instance sharing its hardware-id prefix, so a PnP change
    /// event on a composite parent also invalidates the interfaces under it.
    /// <para>丢弃该实例以及共享其硬件 ID 前缀的全部实例记录，使组合父节点的 PnP 变化事件也能使其下属接口失效。</para>
    /// </summary>
    /// <param name="instanceId">Instance id reported by the change event; blank invalidates nothing.
    /// <para>变化事件报告的实例 ID；空白时不使任何记录失效。</para></param>
    internal static void InvalidateInstance(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        // A composite device reports "USB\VID_x&PID_y\serial" while its interfaces report
        // "USB\VID_x&PID_y&MI_nn\..."; dropping the shared hardware-id prefix covers both.
        var separator = instanceId.LastIndexOf('\\');
        var prefix = separator > 0 ? instanceId[..separator] : instanceId;
        lock (Sync)
        {
            foreach (var key in ByInstance.Keys
                         .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                                       prefix.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                ByInstance.Remove(key);
            }
        }
    }

    /// <summary>
    /// Drops every record the pass did not observe, so an interface that left the bus stops being reported
    /// from the cache. Only an unfiltered pass may call this: a pass that searched one exact path
    /// legitimately observed nothing else.
    /// <para>丢弃本轮没有观测到的全部记录，使已下总线的接口不再由缓存上报。只有无过滤的一轮可以调用：
    /// 只查找某条精确路径的一轮本就不应观测到其它接口。</para>
    /// </summary>
    /// <param name="observedInstanceIds">Instance ids seen by this pass.
    /// <para>本轮观测到的实例 ID 集合。</para></param>
    internal static void PruneUnobserved(HashSet<string> observedInstanceIds)
    {
        ArgumentNullException.ThrowIfNull(observedInstanceIds);
        lock (Sync)
        {
            foreach (var key in ByInstance.Keys.Where(key => !observedInstanceIds.Contains(key)).ToArray())
            {
                ByInstance.Remove(key);
            }
        }
    }

    /// <summary>
    /// Forgets every record and forces the next pass to be complete, which is what an explicit user refresh
    /// asks for.
    /// <para>遗忘全部记录并强制下一轮为完整遍历，这正是用户显式刷新所要求的。</para>
    /// </summary>
    internal static void Reset()
    {
        lock (Sync)
        {
            ByInstance.Clear();
            _deviceClassGuidMap = null;
            _deviceClassGuidMapTicks = 0;
            _completedFullPass = false;
        }
    }

    /// <summary>
    /// Marks the pass that just finished, so the following passes may run incrementally.
    /// <para>标记刚结束的这一轮，使后续轮可以按增量执行。</para>
    /// </summary>
    internal static void CompleteFullPass()
    {
        lock (Sync)
        {
            _completedFullPass = true;
        }
    }

    /// <summary>
    /// Gets the remembered <c>Control\DeviceClasses</c> reverse map, or <see langword="null"/> when it was not
    /// built yet, has expired, or was invalidated. The map only changes when device interfaces are added or
    /// removed, which the hotplug monitor reports and invalidates at once, so it survives across passes; the
    /// lifetime bound covers the polling fallback, where no event carries a path.
    /// <para>取出记住的 <c>Control\DeviceClasses</c> 反向映射；尚未构建、已过期或已失效时返回
    /// <see langword="null"/>。该映射只在设备接口增删时变化，而热插拔监视会立刻报告并使其失效，因此可跨轮复用；
    /// 时限用于覆盖没有路径事件的轮询回退路径。</para>
    /// </summary>
    /// <returns>The cached map, or <see langword="null"/>.
    /// <para>缓存的映射，或 <see langword="null"/>。</para></returns>
    internal static Dictionary<string, List<Guid>>? GetDeviceClassGuidMap()
    {
        lock (Sync)
        {
            if (_deviceClassGuidMap is null)
            {
                return null;
            }

            if (Environment.TickCount64 - _deviceClassGuidMapTicks > DeviceClassMapLifetime.TotalMilliseconds)
            {
                _deviceClassGuidMap = null;
                return null;
            }

            return _deviceClassGuidMap;
        }
    }

    /// <summary>
    /// Remembers the built reverse map so later passes skip the full registry walk.
    /// <para>记住构建出的反向映射，使后续轮跳过整棵注册表树遍历。</para>
    /// </summary>
    /// <param name="map">The map produced by this pass. <para>本轮构建出的映射。</para></param>
    internal static void RememberDeviceClassGuidMap(Dictionary<string, List<Guid>> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        lock (Sync)
        {
            _deviceClassGuidMap = map;
            _deviceClassGuidMapTicks = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Forgets the reverse map so the next pass that needs it rebuilds it, which the hotplug monitor does for
    /// every arrival or removal because those are the only events that change the device-interface tree.
    /// <para>遗忘反向映射，使下一轮需要时重新构建；热插拔监视对每次插入或移除都这样做，因为这正是改变设备接口树的唯一事件。</para>
    /// </summary>
    internal static void InvalidateDeviceClassGuidMap()
    {
        lock (Sync)
        {
            _deviceClassGuidMap = null;
            _deviceClassGuidMapTicks = 0;
        }
    }

    /// <summary>
    /// Clears the learned records for the instances a device-change event touched: the reported node itself,
    /// its composite parent and its interfaces, so the next pass re-reads exactly what changed.
    /// <para>清除设备变化事件所涉及实例的已学记录：被报告的节点本身、其组合父节点及其各接口，使下一轮只重读真正变化之处。</para>
    /// </summary>
    /// <param name="instanceIds">Instance ids touched by the event.
    /// <para>事件所涉及的实例 ID。</para></param>
    internal static void InvalidateInstances(IEnumerable<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        lock (Sync)
        {
            foreach (var instanceId in instanceIds)
            {
                ByInstance.Remove(instanceId);
            }
        }
    }

    private static void Update(string? instanceId, UpdateCallback apply)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (Sync)
        {
            if (!ByInstance.TryGetValue(instanceId, out var record))
            {
                if (ByInstance.Count >= Capacity)
                {
                    // Dictionary insertion order is stable for keys that are never removed, so the first key
                    // is the oldest entry; evicting it bounds growth without a separate timestamp list.
                    ByInstance.Remove(ByInstance.Keys.First());
                }

                record = new Record(0, 0, null, null, null, null, null, false, string.Empty, null, 0);
            }

            apply(ref record);
            ByInstance[instanceId] = record;
        }
    }

    private delegate void UpdateCallback(ref Record record);
}