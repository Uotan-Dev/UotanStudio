namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Represents the type of a UFS provisioning entry.
/// <para>表示 UFS 配置条目的类型。</para>
/// </summary>
public enum UfsProvisionEntryType
{
    /// <summary>
    /// Common UFS provisioning configuration.
    /// <para>通用 UFS 配置信息。</para>
    /// </summary>
    Common,

    /// <summary>
    /// Per-LUN body entry in UFS provisioning.
    /// <para>UFS 配置中每个 LUN 的 body 条目。</para>
    /// </summary>
    Body,

    /// <summary>
    /// Epilogue entry in UFS provisioning.
    /// <para>UFS 配置中的 epilogue 条目。</para>
    /// </summary>
    Epilogue
}

/// <summary>
/// Base class for UFS provisioning entries parsed from UFS provisioning XML files.
/// <para>从 UFS 配置 XML 文件解析的 UFS 配置条目的基类。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="UfsProvisionEntry"/> class.
/// <para>初始化 <see cref="UfsProvisionEntry"/> 类的新实例。</para>
/// </remarks>
/// <param name="entryType">The entry type. 条目类型。</param>
public abstract class UfsProvisionEntry(UfsProvisionEntryType entryType)
{
    /// <summary>
    /// Gets the type of this UFS provisioning entry.
    /// <para>获取此 UFS 配置条目的类型。</para>
    /// </summary>
    public UfsProvisionEntryType EntryType { get; } = entryType;

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
public sealed class UfsProvisionCommon : UfsProvisionEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UfsProvisionCommon"/> class.
    /// <para>初始化 <see cref="UfsProvisionCommon"/> 类的新实例。</para>
    /// </summary>
    public UfsProvisionCommon() : base(UfsProvisionEntryType.Common) { }

    /// <summary>Gets or sets the number of logical units (LUNs). 逻辑单元（LUN）的数量。</summary>
    public int BNumberLU { get; set; }

    /// <summary>Gets or sets a value indicating whether boot is enabled. 是否启用启动。</summary>
    public int BBootEnable { get; set; }

    /// <summary>Gets or sets the descriptor access enable value. 描述符访问使能值。</summary>
    public int BDescrAccessEn { get; set; }

    /// <summary>Gets or sets the initial power mode. 初始电源模式。</summary>
    public int BInitPowerMode { get; set; }

    /// <summary>Gets or sets the high priority LUN index. 高优先级 LUN 索引。</summary>
    public int BHighPriorityLUN { get; set; }

    /// <summary>Gets or sets the secure removal type. 安全擦除类型。</summary>
    public int BSecureRemovalType { get; set; }

    /// <summary>Gets or sets the initial active ICC level. 初始活跃 ICC 等级。</summary>
    public int BInitActiveICCLevel { get; set; }

    /// <summary>Gets or sets the periodic RTC update value. 周期性 RTC 更新值。</summary>
    public int WPeriodicRTCUpdate { get; set; }

    /// <summary>Gets or sets the configuration descriptor lock value. 配置描述符锁定值。</summary>
    public int BConfigDescrLock { get; set; }
}

/// <summary>
/// Represents a per-LUN body entry in UFS provisioning.
/// <para>表示 UFS 配置中每个 LUN 的 body 条目。</para>
/// </summary>
public sealed class UfsProvisionBody : UfsProvisionEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UfsProvisionBody"/> class.
    /// <para>初始化 <see cref="UfsProvisionBody"/> 类的新实例。</para>
    /// </summary>
    public UfsProvisionBody() : base(UfsProvisionEntryType.Body) { }

    /// <summary>Gets or sets the LUN number for this body entry. 此 body 条目的 LUN 编号。</summary>
    public int LUNum { get; set; }

    /// <summary>Gets or sets a value indicating whether the LUN is enabled. LUN 是否启用。</summary>
    public int BLUEnable { get; set; }

    /// <summary>Gets or sets the boot LUN ID. 启动 LUN ID。</summary>
    public int BBootLUNID { get; set; }

    /// <summary>Gets or sets the size of the LUN in kilobytes. LUN 大小（千字节）。</summary>
    public long SizeInKb { get; set; }

    /// <summary>Gets or sets the data reliability value. 数据可靠性值。</summary>
    public int BDataReliability { get; set; }

    /// <summary>Gets or sets the LU write protect value. LU 写保护值。</summary>
    public int BLUWriteProtect { get; set; }

    /// <summary>Gets or sets the memory type. 内存类型。</summary>
    public int BMemoryType { get; set; }

    /// <summary>Gets or sets the logical block size. 逻辑块大小。</summary>
    public int BLogicalBlockSize { get; set; }

    /// <summary>Gets or sets the provisioning type. 供应类型。</summary>
    public int BProvisioningType { get; set; }

    /// <summary>Gets or sets the context capacities value. 上下文容量值。</summary>
    public int WContextCapacities { get; set; }

    /// <summary>Gets or sets the description of this LUN. 此 LUN 的描述。</summary>
    public string Desc { get; set; } = string.Empty;
}

/// <summary>
/// Represents the epilogue entry in UFS provisioning.
/// <para>表示 UFS 配置中的 epilogue 条目。</para>
/// </summary>
public sealed class UfsProvisionEpilogue : UfsProvisionEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UfsProvisionEpilogue"/> class.
    /// <para>初始化 <see cref="UfsProvisionEpilogue"/> 类的新实例。</para>
    /// </summary>
    public UfsProvisionEpilogue() : base(UfsProvisionEntryType.Epilogue) { }

    /// <summary>Gets or sets the number of LUNs to grow. 待扩容的 LUN 数量。</summary>
    public int LUNumToGrow { get; set; }

    /// <summary>Gets or sets a value indicating whether to commit the provisioning changes. 是否提交配置更改。</summary>
    public int Commit { get; set; }
}

/// <summary>
/// Represents the complete contents of a UFS provisioning XML file, containing an ordered
/// list of common, body, and epilogue entries.
/// <para>表示 UFS 配置 XML 文件的完整内容，包含有序的 common、body 和 epilogue 条目列表。</para>
/// </summary>
public sealed class UfsProvisionFile
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
    public UfsProvisionCommon? Common => Entries.OfType<UfsProvisionCommon>().FirstOrDefault();

    /// <summary>
    /// Gets all body entries from the provisioning file.
    /// <para>获取配置文件中的所有 body 条目。</para>
    /// </summary>
    public IEnumerable<UfsProvisionBody> Bodies => Entries.OfType<UfsProvisionBody>();

    /// <summary>
    /// Gets the epilogue entry from the provisioning file, if present.
    /// <para>获取配置文件中的 epilogue 条目（如果存在）。</para>
    /// </summary>
    public UfsProvisionEpilogue? Epilogue => Entries.OfType<UfsProvisionEpilogue>().FirstOrDefault();
}