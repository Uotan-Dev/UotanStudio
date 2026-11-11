using UotanStudio.Comm.Protocols.SpacemiT;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// The device and page facts a control's own availability condition is evaluated against.
/// <para>控件自身可用性条件的判定依据，包含设备与页面事实。</para>
/// </summary>
internal readonly record struct FlashGateContext(
    FlashDeviceMode Mode,
    FlashDeviceKind Kind,
    RockchipStage Stage,
    bool IsSpacemiTFastboot,
    bool HasAdbSu,
    bool HasSelectedPartitions = false,
    SpacemiTBootStage FastbootStage = SpacemiTBootStage.Unknown);

/// <summary>
/// One control's own availability condition: the control holds this object, and the page asks that control whether
/// the selected device offers it instead of looking the control up in a page-wide table.
/// <para>单个控件自身的可用性条件：控件持有该对象，页面直接询问该控件是否被所选设备提供，而不再到页面级表里查控件。</para>
/// </summary>
/// <remarks>
/// Each factory below states one policy in the place the control is declared, so a control's requirement travels with
/// the control rather than with a name-keyed table. The condition receives only the device snapshot, which keeps it
/// independent of the page state that owns the enable intent. A gate never hides its control: a device that does not
/// offer an action leaves that action visible and disabled, so every entry stays discoverable and only the section a
/// device actually owns reports usable controls.
/// <para>下面的每个工厂都在声明控件的地方给出对应策略，使控件的要求随控件本身而不是随按名称索引的表走。条件只接收设备快照，
/// 因此与持有启用意图的页面状态保持独立。门控从不隐藏控件：设备不提供某个动作时，该动作保持可见并置灰，使每个入口都能被
/// 发现，只有设备真正拥有的分类才呈现可用控件。</para>
/// </remarks>
internal sealed class FlashControlGate
{
    private FlashControlGate(string name, Func<FlashGateContext, bool> isOffered, bool busyDisabled)
    {
        Name = name;
        IsOffered = isOffered;
        BusyDisabled = busyDisabled;
    }

    /// <summary>Name of the XAML control this gate belongs to. <para>该门控所属的 XAML 控件名。</para></summary>
    internal string Name { get; }

    /// <summary>The condition the selected device has to satisfy. <para>所选设备必须满足的条件。</para></summary>
    internal Func<FlashGateContext, bool> IsOffered { get; }

    /// <summary>Whether the page operation turns this control off. <para>页面操作运行期间是否关闭该控件。</para></summary>
    internal bool BusyDisabled { get; }

    /// <summary>
    /// The shared flash action: every selected device offers it except HDC and Allwinner endpoints, which have no image
    /// plan; ADB additionally waits for a confirmed noninteractive root shell, and a Rockchip endpoint proven to be in
    /// mask ROM cannot receive a write.
    /// <para>通用烧录动作：除没有镜像计划的 HDC 与全志端点外，任何已选设备都提供；ADB 还要等待已确认的非交互 root shell，
    /// 被证实处于 mask ROM 的瑞芯微端点不能接收写入。</para>
    /// </summary>
    internal static FlashControlGate SharedFlash(string name, bool busyDisabled = false) => new(name,
        static context => context.Mode != FlashDeviceMode.None &&
            context.Kind is not (FlashDeviceKind.Hdc or FlashDeviceKind.Sunxi) &&
            !(context.Mode == FlashDeviceMode.Rkdl && context.Stage == RockchipStage.Maskrom) &&
            !(context.Kind == FlashDeviceKind.Fastboot && context.IsSpacemiTFastboot &&
                context.FastbootStage == SpacemiTBootStage.BromFdl) &&
            (context.Kind != FlashDeviceKind.Adb || context.HasAdbSu),
        busyDisabled);

    /// <summary>
    /// The root-package picker: offered to every selected device, because the root package is a local artefact and the
    /// page only needs a device before that package can be applied. Only a page operation turns it off.
    /// <para>Root 包选择入口：对任何已选设备都提供，因为 Root 包是本地产物，页面只需要有一台设备才能应用它。只有页面操作
    /// 运行期间才关闭它。</para>
    /// </summary>
    internal static FlashControlGate FlashOptionAnyDevice(string name, bool busyDisabled = false) => new(name,
        static context => context.Mode != FlashDeviceMode.None,
        busyDisabled);

    /// <summary>
    /// Whether the selected SpacemiT fastboot gadget can answer an <c>oem read</c>: the U-Boot gadget does, an
    /// unproven stage stays allowed, and the BootROM download mode has no U-Boot command set to read with.
    /// <para>所选进迭时空 fastboot gadget 能否应答 <c>oem read</c>：U-Boot gadget 可以，未确定的阶段保持允许，
    /// BootROM 下载模式没有可供回读的 U-Boot 命令集。</para>
    /// </summary>
    private static bool IsSpacemiTReadCapable(FlashGateContext context) =>
        context.Kind == FlashDeviceKind.Fastboot &&
        context.IsSpacemiTFastboot &&
        context.FastbootStage != SpacemiTBootStage.BromFdl;

    /// <summary>
    /// The shared storage predicate: offered only to protocols without their own restricted kind, and a Rockchip
    /// endpoint proven to be in mask ROM cannot answer the storage commands they use.
    /// <para>通用存储判定：只提供给没有受限种类的协议；被证实处于 mask ROM 的瑞芯微端点无法应答它们使用的存储命令。</para>
    /// </summary>
    private static bool IsSharedStorageOffered(FlashGateContext context) =>
        context.Mode != FlashDeviceMode.None &&
        context.Kind == FlashDeviceKind.Unknown &&
        !(context.Mode == FlashDeviceMode.Rkdl && context.Stage == RockchipStage.Maskrom);

    /// <summary>
    /// The shared storage, erase and show-all actions: offered only to protocols without their own restricted kind, and
    /// a Rockchip endpoint proven to be in mask ROM cannot answer the storage commands they use. The read-back action
    /// has its own <see cref="Readback"/> gate that extends this policy for the SpacemiT gadget.
    /// <para>通用存储、擦除与显示全部动作：只提供给没有受限种类的协议；被证实处于 mask ROM 的瑞芯微端点无法应答它们使用的
    /// 存储命令。回读动作另有 <see cref="Readback"/> 门控，在此策略之上为进迭时空 gadget 做了扩展。</para>
    /// </summary>
    internal static FlashControlGate SharedStorage(string name, bool busyDisabled = false) => new(name,
        static context => IsSharedStorageOffered(context),
        busyDisabled);

    /// <summary>
    /// The read-back action, extended for the SpacemiT gadget: every protocol without its own restricted kind still
    /// offers it, and a SpacemiT fastboot endpoint offers it too once at least one partition row is selected, because
    /// the read-back run then has a partition to pull with the device's <c>oem read</c>. Other devices keep the exact
    /// shared-storage behaviour.
    /// <para>回读动作，并为进迭时空 gadget 扩展：没有受限种类的协议照旧提供；进迭时空 fastboot 端点在至少勾选一行分区时
    /// 也提供，因为此时回读运行才有可用 <c>oem read</c> 拉取的分区。其它设备保持与通用存储完全一致的行为。</para>
    /// </summary>
    internal static FlashControlGate Readback(string name, bool busyDisabled = false) => new(name,
        static context => IsSharedStorageOffered(context) ||
            (IsSpacemiTReadCapable(context) && context.HasSelectedPartitions),
        busyDisabled);

    /// <summary>
    /// The device-info read: offered to every protocol whose information the page can actually read, which includes
    /// fastboot — that endpoint answers over <c>getvar</c> rather than the storage commands the shared read uses, and its
    /// read is split by boot stage. HDC has no information branch at all, and a Rockchip endpoint proven to be in mask
    /// ROM cannot answer the storage probes.
    /// <para>设备信息读取：提供给页面确实能读取其信息的每个协议，其中包含 fastboot——该端点用 <c>getvar</c> 而不是通用读取所用的
    /// 存储命令应答，其读取还按启动阶段拆分。HDC 完全没有信息分支，被证实处于 mask ROM 的瑞芯微端点无法应答存储探测。</para>
    /// </summary>
    internal static FlashControlGate InfoRead(string name, bool busyDisabled = false) => new(name,
        static context => context.Mode != FlashDeviceMode.None &&
            context.Kind is FlashDeviceKind.Unknown or FlashDeviceKind.Fastboot &&
            !(context.Mode == FlashDeviceMode.Rkdl && context.Stage == RockchipStage.Maskrom),
        busyDisabled);

    /// <summary>
    /// The partition-table read: offered only on a fastboot endpoint, because that is the one transport whose table is
    /// enumerated by the same variable dump this page already reads. Every other protocol reads its table through the
    /// action its own section owns, so the shared entry stays closed there instead of repeating that action; the
    /// SpacemiT BootROM download mode is closed too, because that stage carries no partition table at all.
    /// <para>分区表读取：只在 fastboot 端点上提供，因为只有该传输的分区表由本页已读取的同一份变量转储枚举。其它协议都经各自
    /// 分类拥有的动作读取分区表，因此该通用入口在那里保持关闭，而不是重复那个动作；进迭时空 BootROM 下载模式同样关闭，
    /// 因为该阶段根本没有分区表。</para>
    /// </summary>
    internal static FlashControlGate PartitionTableRead(string name, bool busyDisabled = false) => new(name,
        static context => context.Kind == FlashDeviceKind.Fastboot &&
            !(context.IsSpacemiTFastboot && context.FastbootStage == SpacemiTBootStage.BromFdl),
        busyDisabled);

    /// <summary>
    /// A shared control whose work is local to the package: offered to every selected device without a restricted kind,
    /// and kept on every Rockchip stage because it never touches device storage.
    /// <para>工作只发生在刷机包上的通用控件：提供给任何没有受限种类的已选设备，并在任何瑞芯微阶段都保留，因为它从不接触设备存储。</para>
    /// </summary>
    internal static FlashControlGate SharedAny(string name, bool busyDisabled = false) => new(name,
        static context => context.Mode != FlashDeviceMode.None && context.Kind == FlashDeviceKind.Unknown,
        busyDisabled);

    /// <summary>
    /// A/B slot switching: offered to every selected device with an unrestricted kind except a Rockchip endpoint,
    /// because it is a fastboot operation that neither a bootrom nor a loader can answer.
    /// <para>A/B 槽位切换：提供给除瑞芯微端点外任何没有受限种类的已选设备，因为它是 bootrom 与 loader 都无法应答的
    /// fastboot 操作。</para>
    /// </summary>
    internal static FlashControlGate SlotSwitch(string name, bool busyDisabled = false) => new(name,
        static context => context.Mode is not (FlashDeviceMode.None or FlashDeviceMode.Rkdl) &&
            context.Kind == FlashDeviceKind.Unknown,
        busyDisabled);

    /// <summary>
    /// The boot-loader download: offered to the modes whose devices can load one, plus the SpacemiT gadget while it is
    /// still in the BootROM download mode. Once that gadget runs the U-Boot stage there is no boot chain left to send —
    /// the vendor script's staging actions would be skipped — so the entry closes and the shared flash action opens.
    /// No other transport accepts it.
    /// <para>引导下载：提供给设备可加载引导的模式，外加仍处于 BootROM 下载模式的进迭时空 gadget。该 gadget 进入 U-Boot 阶段后
    /// 已没有引导链可发（厂商脚本的暂存动作会被跳过），因此该入口关闭、通用烧录动作打开。其余传输不接受该动作。</para>
    /// </summary>
    internal static FlashControlGate LoaderOnly(string name, bool busyDisabled = false) => new(name,
        static context => ((context.Mode is FlashDeviceMode.Edl or FlashDeviceMode.Mtk or FlashDeviceMode.Rkdl) &&
                context.Kind == FlashDeviceKind.Unknown) ||
            (context.Kind == FlashDeviceKind.Fastboot && context.IsSpacemiTFastboot &&
                context.FastbootStage != SpacemiTBootStage.Uboot),
        busyDisabled);

    /// <summary>
    /// A protocol section pane: offered only to its own mode, and the Rockchip pane additionally waits for a stage whose
    /// device can answer the storage commands, while a stage never proven to be mask ROM stays offered.
    /// <para>协议分类面板：只提供给其自身模式；瑞芯微面板还要求阶段的设备能应答存储命令，而从未被证实为 mask ROM 的阶段保持提供。</para>
    /// </summary>
    internal static FlashControlGate Section(string name, FlashDeviceMode owner, bool busyDisabled = false) =>
        new(name, context => context.Mode == owner &&
            (context.Kind == FlashDeviceKind.Unknown ||
                (owner == FlashDeviceMode.Sunxi && context.Kind == FlashDeviceKind.Sunxi)) &&
            (owner != FlashDeviceMode.Rkdl || context.Stage != RockchipStage.Maskrom),
            busyDisabled);

    /// <summary>
    /// The Allwinner section and its controls: offered only to an Allwinner endpoint, which owns the only action set
    /// that endpoint currently has.
    /// <para>全志分类及其控件：只提供给全志端点，该端点当前拥有的动作集合仅此一处。</para>
    /// </summary>
    internal static FlashControlGate Sunxi(string name, bool busyDisabled = false) => new(name,
        static context => context.Mode == FlashDeviceMode.Sunxi &&
            context.Kind is FlashDeviceKind.Unknown or FlashDeviceKind.Sunxi,
        busyDisabled);

    /// <summary>
    /// A control that no device restricts: it is offered whenever a device is selected or not, and only the page's
    /// operation state can turn it off.
    /// <para>不受设备限制的控件：无论是否选中设备都提供，只有页面的操作状态能关闭它。</para>
    /// </summary>
    internal static FlashControlGate BusyOnly(string name) => new(name, static _ => true, busyDisabled: true);

    /// <summary>
    /// A page action that additionally needs selected rows: offered only while at least one row is selected, and closed
    /// by the page operation state like every other action-pane entry.
    /// <para>还需要勾选行的页面动作：仅在至少勾选一行时提供，并与其他操作区条目一样在页面操作期间关闭。</para>
    /// </summary>
    internal static FlashControlGate SelectionRequired(string name) => new(name,
        static context => context.HasSelectedPartitions,
        busyDisabled: true);

    /// <summary>
    /// A protocol section header: the header names a section the page can always show, so it stays present and usable for
    /// every selected device and only the section body reports whether the device offers that protocol's actions.
    /// <para>协议分类表头：表头代表页面始终可以展示的分类，因此对任何已选设备都保持可见与可用，只有分类内容才表明设备是否
    /// 提供该协议的动作。</para>
    /// </summary>
    internal static FlashControlGate SectionHeader(string name) => new(name, static _ => true, busyDisabled: false);
}