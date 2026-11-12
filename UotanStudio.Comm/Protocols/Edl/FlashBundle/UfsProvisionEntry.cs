namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// The mutable per-entry state every <see cref="UfsProvisionEntry"/> case carries: the slot number
/// its <c>slot</c> attribute declared.
/// <para>每个 <see cref="UfsProvisionEntry"/> 分支都携带的可变条目状态：其 <c>slot</c> 属性声明的槽位编号。</para>
/// This base type exists only so the shared slot is declared once for all three cases; the closed
/// case set is <see cref="UfsProvisionEntry"/> itself, not this type.
/// <para>本基类型的存在只是为了让三个分支共用的槽位只声明一次；闭合的分支集合是
/// <see cref="UfsProvisionEntry"/> 本身，而不是本类型。</para>
/// </summary>
internal abstract class UfsProvisionEntryState
{
    /// <summary>
    /// Gets or sets the slot number for dual-slot devices.
    /// <para>获取或设置双槽位设备的槽位编号。</para>
    /// </summary>
    public int Slot { get; set; }
}

/// <summary>
/// Represents a common UFS provisioning entry with device-level configuration.
/// <para>表示包含设备级配置的通用 UFS 配置条目。</para>
/// </summary>
internal sealed class UfsProvisionCommon : UfsProvisionEntryState
{
    /// <summary>
    /// Gets or sets the number of logical units (LUNs).
    /// <para>逻辑单元（LUN）的数量。</para>
    /// </summary>
    public int BNumberLU { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether boot is enabled.
    /// <para>是否启用启动。</para>
    /// </summary>
    public int BBootEnable { get; set; }

    /// <summary>
    /// Gets or sets the descriptor access enable value.
    /// <para>描述符访问使能值。</para>
    /// </summary>
    public int BDescrAccessEn { get; set; }

    /// <summary>
    /// Gets or sets the initial power mode.
    /// <para>初始电源模式。</para>
    /// </summary>
    public int BInitPowerMode { get; set; }

    /// <summary>
    /// Gets or sets the high priority LUN index.
    /// <para>高优先级 LUN 索引。</para>
    /// </summary>
    public int BHighPriorityLUN { get; set; }

    /// <summary>
    /// Gets or sets the secure removal type.
    /// <para>安全擦除类型。</para>
    /// </summary>
    public int BSecureRemovalType { get; set; }

    /// <summary>
    /// Gets or sets the initial active ICC level.
    /// <para>初始活跃 ICC 等级。</para>
    /// </summary>
    public int BInitActiveICCLevel { get; set; }

    /// <summary>
    /// Gets or sets the periodic RTC update value.
    /// <para>周期性 RTC 更新值。</para>
    /// </summary>
    public int WPeriodicRTCUpdate { get; set; }

    /// <summary>
    /// Gets or sets the configuration descriptor lock value.
    /// <para>配置描述符锁定值。</para>
    /// </summary>
    public int BConfigDescrLock { get; set; }
}

/// <summary>
/// Represents a per-LUN body entry in UFS provisioning.
/// <para>表示 UFS 配置中每个 LUN 的 body 条目。</para>
/// </summary>
internal sealed class UfsProvisionBody : UfsProvisionEntryState
{
    /// <summary>
    /// Gets or sets the LUN number for this body entry.
    /// <para>此 body 条目的 LUN 编号。</para>
    /// </summary>
    public int LUNum { get; set; }

    /// <summary>Gets or sets a value indicating whether the LUN is enabled. LUN 是否启用。</summary>
    public int BLUEnable { get; set; }

    /// <summary>
    /// Gets or sets the boot LUN ID.
    /// <para>启动 LUN ID。</para>
    /// </summary>
    public int BBootLUNID { get; set; }

    /// <summary>Gets or sets the size of the LUN in kilobytes. LUN 大小（千字节）。</summary>
    public long SizeInKb { get; set; }

    /// <summary>
    /// Gets or sets the data reliability value.
    /// <para>数据可靠性值。</para>
    /// </summary>
    public int BDataReliability { get; set; }

    /// <summary>Gets or sets the LU write protect value. LU 写保护值。</summary>
    public int BLUWriteProtect { get; set; }

    /// <summary>
    /// Gets or sets the memory type.
    /// <para>内存类型。</para>
    /// </summary>
    public int BMemoryType { get; set; }

    /// <summary>
    /// Gets or sets the logical block size.
    /// <para>逻辑块大小。</para>
    /// </summary>
    public int BLogicalBlockSize { get; set; }

    /// <summary>
    /// Gets or sets the provisioning type.
    /// <para>供应类型。</para>
    /// </summary>
    public int BProvisioningType { get; set; }

    /// <summary>
    /// Gets or sets the context capacities value.
    /// <para>上下文容量值。</para>
    /// </summary>
    public int WContextCapacities { get; set; }

    /// <summary>
    /// Gets or sets the description of this LUN.
    /// <para>此 LUN 的描述。</para>
    /// </summary>
    public string Desc { get; set; } = string.Empty;
}

/// <summary>
/// Represents the epilogue entry in UFS provisioning.
/// <para>表示 UFS 配置中的 epilogue 条目。</para>
/// </summary>
internal sealed class UfsProvisionEpilogue : UfsProvisionEntryState
{
    /// <summary>
    /// Gets or sets the number of LUNs to grow.
    /// <para>待扩容的 LUN 数量。</para>
    /// </summary>
    public int LUNumToGrow { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to commit the provisioning changes.
    /// <para>是否提交配置更改。</para>
    /// </summary>
    public int Commit { get; set; }
}

/// <summary>
/// A UFS provisioning entry parsed from a UFS provisioning XML file. The case set is exactly
/// <see cref="UfsProvisionCommon"/>, <see cref="UfsProvisionBody"/> and
/// <see cref="UfsProvisionEpilogue"/>, so a <c>switch</c> over the union is exhaustive and a case
/// can only be introduced by changing this file.
/// <para>从 UFS 配置 XML 文件解析出的 UFS 配置条目。分支集合恰为
/// <see cref="UfsProvisionCommon"/>、<see cref="UfsProvisionBody"/> 与 <see cref="UfsProvisionEpilogue"/>，
/// 因此对联合的 <c>switch</c> 已穷尽，新增分支只能通过修改本文件完成。</para>
/// All three cases are reference types, so holding one boxes nothing. The default value carries no
/// entry at all, which is why every read either pattern-matches the case or falls back explicitly.
/// <para>三个分支都是引用类型，因此持有它们不会装箱。默认值不携带任何条目，
/// 所以每次读取要么对分支做模式匹配，要么显式回退。</para>
/// </summary>
internal union UfsProvisionEntry(UfsProvisionCommon, UfsProvisionBody, UfsProvisionEpilogue)
{
    /// <summary>
    /// Gets the slot number the entry's <c>slot</c> attribute declared, or zero when the held case
    /// carries no slot state.
    /// <para>获取条目 <c>slot</c> 属性声明的槽位编号；所持分支不带槽位状态时为零。</para>
    /// </summary>
    /// <value>The declared slot number, or zero when the case carries no slot state.
    /// <para>声明的槽位编号；分支不带槽位状态时为零。</para></value>
    internal int Slot => Value is UfsProvisionEntryState state ? state.Slot : 0;
}

/// <summary>
/// Represents the complete contents of a UFS provisioning XML file, containing an ordered
/// list of common, body, and epilogue entries.
/// <para>表示 UFS 配置 XML 文件的完整内容，包含有序的 common、body 和 epilogue 条目列表。</para>
/// </summary>
internal sealed class UfsProvisionFile
{
    /// <summary>
    /// Gets the ordered list of UFS provisioning entries.
    /// <para>获取 UFS 配置条目的有序列表。</para>
    /// </summary>
    public List<UfsProvisionEntry> Entries { get; } = [];

    /// <summary>
    /// Gets the common entry from the provisioning file, if present.
    /// <para>获取配置文件中的 common 条目（如果存在）。</para>
    /// </summary>
    /// <value>The first common entry, or <see langword="null"/> when the file has none.
    /// <para>第一个 common 条目；文件中没有时返回 <see langword="null"/>。</para></value>
    public UfsProvisionCommon? Common
    {
        get
        {
            foreach (var entry in Entries)
            {
                if (entry.Value is UfsProvisionCommon common)
                {
                    return common;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Gets all body entries from the provisioning file, in document order.
    /// <para>按文档顺序获取配置文件中的所有 body 条目。</para>
    /// </summary>
    /// <value>A lazy sequence over the live entry list. <para>对实时条目列表的惰性序列。</para></value>
    public IEnumerable<UfsProvisionBody> Bodies
    {
        get
        {
            foreach (var entry in Entries)
            {
                if (entry.Value is UfsProvisionBody body)
                {
                    yield return body;
                }
            }
        }
    }

    /// <summary>
    /// Gets the epilogue entry from the provisioning file, if present.
    /// <para>获取配置文件中的 epilogue 条目（如果存在）。</para>
    /// </summary>
    /// <value>The first epilogue entry, or <see langword="null"/> when the file has none.
    /// <para>第一个 epilogue 条目；文件中没有时返回 <see langword="null"/>。</para></value>
    public UfsProvisionEpilogue? Epilogue
    {
        get
        {
            foreach (var entry in Entries)
            {
                if (entry.Value is UfsProvisionEpilogue epilogue)
                {
                    return epilogue;
                }
            }

            return null;
        }
    }
}