using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>
/// The protocol mode a piece of EDL device information was read in, so the UI can show which stage
/// answered for each row instead of implying every value came from one handshake.
/// <para>某条 EDL 设备信息是在哪个协议阶段读到的，使界面能逐条说明该行由哪个阶段应答，
/// 而不是暗示所有取值都来自同一次握手。</para>
/// </summary>
public enum EdlInfoStage
{
    /// <summary>The value was not read; it stays unavailable for this device.
    /// <para>未读到该值；本设备上不可用。</para></summary>
    Unread,

    /// <summary>The Sahara bootrom stage answered, before any programmer was loaded.
    /// <para>由 Sahara bootrom 阶段在加载任何 programmer 之前应答。</para></summary>
    Sahara,

    /// <summary>The Firehose programmer stage answered, after the loader was running.
    /// <para>由 Firehose programmer 阶段在加载器运行之后应答。</para></summary>
    Firehose,
}

/// <summary>
/// One labelled row of EDL device information: what it is, what the device reported, and which stage
/// reported it. Rows carry no localisation, because the caller labels them with the UI language.
/// <para>一条带标签的 EDL 设备信息：它是什么、设备上报的值，以及由哪个阶段上报。行本身不含本地化文本，
/// 由调用方按界面语言标注。</para>
/// </summary>
/// <param name="Key">Stable row key the UI maps to a localised label.
/// <para>稳定行键，界面据此映射本地化标签。</para></param>
/// <param name="Value">The reported value, already human readable.
/// <para>已格式化为人类可读的上报值。</para></param>
/// <param name="Stage">The stage that produced this row.
/// <para>产生该行的阶段。</para></param>
public sealed record EdlInfoRow(string Key, string Value, EdlInfoStage Stage);

/// <summary>
/// The A/B slot layout a partitioning scheme reports, so a single-slot device is stated as such instead of
/// looking like a failed slot read.
/// <para>分区方案报告出的 A/B 槽位布局，使单槽位设备被如实表述，而不是看起来像槽位读取失败。</para>
/// </summary>
public enum EdlSlotLayout
{
    /// <summary>The layout was not established; nothing can be claimed about slots.
    /// <para>布局未确定；无法对槽位作任何断言。</para></summary>
    Unknown,

    /// <summary>The partitioning carries no A/B slots at all, so no active slot exists.
    /// <para>分区方案完全没有 A/B 槽位，因此不存在活动槽位。</para></summary>
    Single,

    /// <summary>The partitioning carries A/B slots, so an active slot is meaningful.
    /// <para>分区方案包含 A/B 槽位，因此活动槽位有意义。</para></summary>
    Dual,
}
/// <summary>
/// The complete read-only information a connected EDL target reports, assembled from whichever protocol
/// stages actually answered. Every field is nullable because a device can refuse a query or stop at
/// Sahara without a programmer.
/// <para>已连接 EDL 目标上报的完整只读信息，由实际应答过的协议阶段汇总。所有字段均可为空，
/// 因为设备可能拒绝某条查询，或停在 Sahara 而没有 programmer。</para>
/// </summary>
public sealed record EdlInfoSnapshot
{
    /// <summary>Gets the protocol mode the live endpoint currently occupies.
    /// <para>获取该端点当前所处的协议模式。</para></summary>
    /// <value>The observed state; <see cref="EdlProtocolState.Unknown"/> when nothing was verified.
    /// <para>已观测状态；未校验到任何协议时为 <see cref="EdlProtocolState.Unknown"/>。</para></value>
    public EdlProtocolState Protocol { get; init; } = EdlProtocolState.Unknown;

    /// <summary>Gets the Sahara handshake facts the bootrom announced.
    /// <para>获取 bootrom 宣告的 Sahara 握手信息。</para></summary>
    /// <value>The HELLO facts, or <c>null</c> when no HELLO was captured.
    /// <para>HELLO 信息；未捕获 HELLO 时为 <c>null</c>。</para></value>
    public SaharaHandshakeInfo? Sahara { get; init; }

    /// <summary>Gets the Sahara command-mode identity the bootrom answered with.
    /// <para>获取 bootrom 以命令模式应答的 Sahara 身份。</para></summary>
    /// <value>The identity report, or <c>null</c> when the target refused every identity query.
    /// <para>身份报告；目标拒绝全部身份查询时为 <c>null</c>。</para></value>
    public SaharaDeviceIdentity? Identity { get; init; }

    /// <summary>Gets the Firehose capabilities the loaded programmer negotiated.
    /// <para>获取已加载 programmer 协商出的 Firehose 能力。</para></summary>
    /// <value>The capability snapshot, or <c>null</c> when no configure ran.
    /// <para>能力快照；未执行 configure 时为 <c>null</c>。</para></value>
    public FirehoseCapabilitySnapshot? Capabilities { get; init; }

    /// <summary>Gets the storage geometry the loaded programmer reported.
    /// <para>获取已加载 programmer 上报的存储几何信息。</para></summary>
    /// <value>The geometry, or <c>null</c> when the query was refused or not attempted.
    /// <para>几何信息；查询被拒绝或未尝试时为 <c>null</c>。</para></value>
    public FirehoseStorageGeometry? Geometry { get; init; }

    /// <summary>Gets the descriptive storage facts merged from storage-info logs.
    /// <para>获取由存储信息日志合并出的描述性存储信息。</para></summary>
    /// <value>The descriptive details, or <c>null</c> when the logs carried none.
    /// <para>描述性详情；日志未包含时为 <c>null</c>。</para></value>
    public FirehoseStorageDetails? Storage { get; init; }

    /// <summary>Gets the A/B slot the partitioning currently marks active.
    /// <para>获取分区表当前标记为活动的 A/B 槽位。</para></summary>
    /// <value>The slot letter, or <c>null</c> when the target reported no slot metadata.
    /// <para>槽位字母；目标未上报槽位元数据时为 <c>null</c>。</para></value>
    public string? ActiveSlot { get; init; }

    /// <summary>Gets the slot layout the partitioning scheme reports.
    /// <para>获取分区方案报告出的槽位布局。</para></summary>
    /// <value>The layout, which distinguishes a device that has no A/B slots at all from one whose slot
    /// metadata could not be read. <para>布局取值，用于区分“根本没有 A/B 槽位”的设备与“槽位元数据读取失败”
    /// 的设备。</para></value>
    public EdlSlotLayout SlotLayout { get; init; } = EdlSlotLayout.Unknown;

    /// <summary>Gets the COM port name when the endpoint runs over the QDLoader serial fallback.
    /// <para>获取端点经 QDLoader 串口回退运行时的 COM 端口名。</para></summary>
    /// <value>The port name, or <c>null</c> over a native USB transport.
    /// <para>端口名；原生 USB 传输时为 <c>null</c>。</para></value>
    public string? SerialPortName { get; init; }

    /// <summary>Gets the ordered, already formatted rows to display.
    /// <para>获取按序排列、已格式化的待显示行。</para></summary>
    /// <value>Rows in display order; empty when nothing was read.
    /// <para>按显示顺序排列的行；未读到任何信息时为空。</para></value>
    public IReadOnlyList<EdlInfoRow> Rows { get; init; } = [];
}