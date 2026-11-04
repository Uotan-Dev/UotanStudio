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
    bool HasSelectedPartitions = false);

/// <summary>
/// One control's own availability condition: the control holds this object, and the page asks that control whether
/// the selected device offers it instead of looking the control up in a page-wide table.
/// <para>单个控件自身的可用性条件：控件持有该对象，页面直接询问该控件是否被所选设备提供，而不再到页面级表里查控件。</para>
/// </summary>
/// <remarks>
/// Each factory below states one policy in the place the control is declared, so a control's requirement travels with
/// the control rather than with a name-keyed table. The condition receives only the device snapshot, which keeps it
/// independent of the page state that owns the enable intent.
/// <para>下面的每个工厂都在声明控件的地方给出对应策略，使控件的要求随控件本身而不是随按名称索引的表走。条件只接收设备快照，
/// 因此与持有启用意图的页面状态保持独立。</para>
/// </remarks>
internal sealed class FlashControlGate
{
    private FlashControlGate(string name, Func<FlashGateContext, bool> isOffered, bool busyDisabled,
        Func<FlashGateContext, bool>? isVisible = null)
    {
        Name = name;
        IsOffered = isOffered;
        BusyDisabled = busyDisabled;
        IsVisible = isVisible;
    }

    /// <summary>Name of the XAML control this gate belongs to. <para>该门控所属的 XAML 控件名。</para></summary>
    internal string Name { get; }

    /// <summary>The condition the selected device has to satisfy. <para>所选设备必须满足的条件。</para></summary>
    internal Func<FlashGateContext, bool> IsOffered { get; }

    /// <summary>Whether the page operation turns this control off. <para>页面操作运行期间是否关闭该控件。</para></summary>
    internal bool BusyDisabled { get; }

    /// <summary>
    /// The visibility condition, when the control should disappear rather than stay disabled; <see langword="null"/>
    /// keeps view-owned visibility untouched.
    /// <para>控件应隐藏而不是仅置灰时的可见性条件；<see langword="null"/> 表示不改动视图自身的可见性。</para>
    /// </summary>
    internal Func<FlashGateContext, bool>? IsVisible { get; }

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
            (context.Kind != FlashDeviceKind.Adb || context.HasAdbSu),
        busyDisabled);

    /// <summary>
    /// The shared read, read-back and erase actions: offered only to protocols without their own restricted kind, and a
    /// Rockchip endpoint proven to be in mask ROM cannot answer the storage commands they use.
    /// <para>通用读取、回读与擦除动作：只提供给没有受限种类的协议；被证实处于 mask ROM 的瑞芯微端点无法应答它们使用的存储命令。</para>
    /// </summary>
    internal static FlashControlGate SharedStorage(string name, bool busyDisabled = false) => new(name,
        static context => context.Mode != FlashDeviceMode.None &&
            context.Kind == FlashDeviceKind.Unknown &&
            !(context.Mode == FlashDeviceMode.Rkdl && context.Stage == RockchipStage.Maskrom),
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
    /// The boot-loader download: offered only to the modes whose devices can load one, since no other transport accepts it.
    /// <para>引导下载：只提供给设备可加载引导的模式，其余传输不接受该动作。</para>
    /// </summary>
    internal static FlashControlGate LoaderOnly(string name, bool busyDisabled = false) => new(name,
        static context => (context.Mode is FlashDeviceMode.Edl or FlashDeviceMode.Mtk or FlashDeviceMode.Rkdl) &&
            context.Kind == FlashDeviceKind.Unknown,
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
    /// The SpacemiT section and its controls: offered only to the SpacemiT fastboot gadget itself, regardless of mode,
    /// because no other endpoint answers that package's script.
    /// <para>进迭时空分类及其控件：只提供给进迭时空 fastboot gadget 本身，与模式无关，因为其他端点不应答该包的脚本。</para>
    /// </summary>
    internal static FlashControlGate SpacemiT(string name, bool busyDisabled = false) => new(name,
        static context => context.Kind == FlashDeviceKind.Fastboot && context.IsSpacemiTFastboot,
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
    /// A protocol section header: the header stays usable, and it disappears once a device of another protocol is
    /// selected; with no device selected it stays visible so the section can be prepared before plugging one in.
    /// <para>协议分类表头：表头始终可用，并在选中其他协议设备后隐藏；未选中设备时保持可见，使分类可以在接入设备前先行准备。</para>
    /// </summary>
    internal static FlashControlGate SectionHeader(string name, FlashDeviceMode owner) => new(name,
        static _ => true,
        busyDisabled: false,
        isVisible: context => context.Mode == FlashDeviceMode.None || context.Mode == owner);

    /// <summary>
    /// The image-operations section header: visible while no device is selected and for protocols without a restricted
    /// kind, hidden for ADB, fastboot, HDC and Allwinner endpoints whose image plan is unavailable.
    /// <para>镜像操作分类表头：未选中设备与没有受限种类的协议下可见，对镜像计划不可用的 ADB、fastboot、HDC 与全志端点隐藏。</para>
    /// </summary>
    internal static FlashControlGate ImageSectionHeader(string name) => new(name,
        static _ => true,
        busyDisabled: false,
        isVisible: static context => context.Mode == FlashDeviceMode.None ||
            context.Kind == FlashDeviceKind.Unknown);

    /// <summary>
    /// The SpacemiT section header: visible while no device is selected and for the SpacemiT gadget itself, hidden for
    /// every other endpoint because no other device answers that package's script.
    /// <para>进迭时空分类表头：未选中设备与进迭时空 gadget 本身时可见，其他端点隐藏，因为其他设备不应答该包的脚本。</para>
    /// </summary>
    internal static FlashControlGate SpacemiTSectionHeader(string name) => new(name,
        static _ => true,
        busyDisabled: false,
        isVisible: static context => context.Mode == FlashDeviceMode.None ||
            (context.Kind == FlashDeviceKind.Fastboot && context.IsSpacemiTFastboot));
}
