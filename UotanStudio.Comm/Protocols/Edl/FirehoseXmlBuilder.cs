using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Builds the XML command documents exchanged with the Firehose programmer.
/// <para>构建与 Firehose programmer 交换的 XML 命令文档。</para>
/// Modeled on the Qualcomm fh_loader/qdl command set; element names are lowercase where the
/// reference tools use lowercase (e.g. <c>&lt;nop/&gt;</c>), since Firehose XML element names
/// are case-sensitive.
/// <para>参照 Qualcomm fh_loader/qdl 命令集；凡参考工具使用小写的地方元素名均为小写
/// （如 <c>&lt;nop/&gt;</c>），因为 Firehose XML 元素名大小写敏感。</para>
/// </summary>
public static partial class FirehoseXmlBuilder
{
    /// <summary>
    /// Builds the &lt;configure&gt; XML command from the specified configuration.
    /// <para>根据指定配置构建 &lt;configure&gt; XML 命令。</para>
    /// </summary>
    /// <param name="config">The Firehose configuration. Firehose 配置。</param>
    /// <returns>The configure XML string. 配置 XML 字符串。</returns>
    public static string BuildConfigure(FirehoseConfiguration config)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<configure MemoryName=\"{config.StorageTypeString}\"");
        sb.Append($" Verbose=\"{config.Verbose}\"");
        sb.Append($" AlwaysValidate=\"{config.AlwaysValidate}\"");
        sb.Append($" MaxDigestTableSizeInBytes=\"{config.MaxDigestTableSizeInBytes}\"");
        sb.Append($" MaxPayloadSizeToTargetInBytes=\"{config.MaxPayloadSizeToTarget}\"");
        sb.Append($" MaxPayloadSizeToTargetInBytesSupported=\"{config.MaxPayloadSizeToTargetSupported}\"");
        sb.Append($" MaxPayloadSizeFromTargetInBytes=\"{config.MaxPayloadSizeFromTarget}\"");
        sb.Append($" ZlpAwareHost=\"{(config.ZlpAwareHost ? 1 : 0)}\"");
        if (config.SkipStorageInit != 0)
        {
            sb.Append($" SkipStorageInit=\"{config.SkipStorageInit}\"");
        }

        if (config.SkipWrite != 0)
        {
            sb.Append($" SkipWrite=\"{config.SkipWrite}\"");
        }

        if (config.SkipSettingMinClocks != 0)
        {
            sb.Append($" SkipSettingMinClocks=\"{config.SkipSettingMinClocks}\"");
        }

        if (config.SkipSettingMaxClocks != 0)
        {
            sb.Append($" SkipSettingMaxClocks=\"{config.SkipSettingMaxClocks}\"");
        }

        if (!string.IsNullOrEmpty(config.TargetName))
        {
            sb.Append($" TargetName=\"{EscapeXml(config.TargetName)}\"");
        }

        if (config.Slot != 0)
        {
            sb.Append($" slot=\"{config.Slot}\"");
        }

        if (config.StorageSlot != 0)
        {
            sb.Append($" StorageSlot=\"{config.StorageSlot}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;program&gt; XML command.
    /// <para>构建 &lt;program&gt; XML 命令。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="numPartitionSectors">The number of sectors to program. 待编程扇区数。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="label">Optional partition label. 可选分区标签。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The program XML string. 编程 XML 字符串。</returns>
    public static string BuildProgram(long sectorSizeInBytes, long numPartitionSectors,
        string filename, int physicalPartitionNumber, long startSector,
        string? label = null, int? slot = null)
        => BuildProgram(sectorSizeInBytes, numPartitionSectors, filename, physicalPartitionNumber,
            new FirehoseSectorAddress(startSector, false), label, slot);

    internal static string BuildProgram(long sectorSizeInBytes, long numPartitionSectors,
        string filename, int physicalPartitionNumber, FirehoseSectorAddress startSector,
        string? label = null, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<program SECTOR_SIZE_IN_BYTES=\"{sectorSizeInBytes}\"");
        sb.Append($" num_partition_sectors=\"{numPartitionSectors}\"");
        sb.Append($" filename=\"{EscapeXml(filename)}\"");
        sb.Append($" physical_partition_number=\"{physicalPartitionNumber}\"");
        sb.Append($" start_sector=\"{startSector}\"");
        if (!string.IsNullOrEmpty(label))
        {
            sb.Append($" label=\"{EscapeXml(label)}\"");
        }

        if (slot.HasValue && slot.Value != 0)
        {
            sb.Append($" slot=\"{slot.Value}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds a &lt;program&gt; XML command with digest table attributes for Verified Image
    /// Programming (VIP).
    /// <para>构建带摘要表属性的 &lt;program&gt; XML 命令，用于验证镜像编程（VIP）。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="numPartitionSectors">The number of sectors to program. 待编程扇区数。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="digest">The hex string of the SHA256 digest. SHA256 摘要的十六进制字符串。</param>
    /// <param name="digestSignature">The base64-encoded signature of the digest. 摘要的 Base64 编码签名。</param>
    /// <param name="signatureSize">The size of the signature in bytes. 签名大小（字节）。</param>
    /// <param name="label">Optional partition label. 可选分区标签。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The program XML string with digest attributes. 带摘要属性的编程 XML 字符串。</returns>
    public static string BuildProgramWithDigest(long sectorSizeInBytes, long numPartitionSectors,
        string filename, int physicalPartitionNumber, long startSector,
        string digest, string digestSignature, int signatureSize,
        string? label = null, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<program SECTOR_SIZE_IN_BYTES=\"{sectorSizeInBytes}\"");
        sb.Append($" num_partition_sectors=\"{numPartitionSectors}\"");
        sb.Append($" filename=\"{EscapeXml(filename)}\"");
        sb.Append($" physical_partition_number=\"{physicalPartitionNumber}\"");
        sb.Append($" start_sector=\"{startSector}\"");
        if (!string.IsNullOrEmpty(label))
        {
            sb.Append($" label=\"{EscapeXml(label)}\"");
        }

        if (slot.HasValue && slot.Value != 0)
        {
            sb.Append($" slot=\"{slot.Value}\"");
        }

        sb.Append($" digest=\"{EscapeXml(digest)}\"");
        sb.Append($" digest_signature=\"{EscapeXml(digestSignature)}\"");
        sb.Append($" signature_size=\"{signatureSize}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds a &lt;getsha256digest&gt; XML command to retrieve the SHA256 hash of a partition region
    /// from the target.
    /// <para>构建 &lt;getsha256digest&gt; XML 命令，从目标设备获取分区区域的 SHA256 哈希值。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="numPartitionSectors">The number of sectors to hash. 待哈希扇区数。</param>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <returns>The getsha256digest XML string. getsha256digest XML 字符串。</returns>
    public static string BuildGetSha256(long sectorSizeInBytes, long numPartitionSectors,
        int physicalPartitionNumber, long startSector)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<getsha256digest SECTOR_SIZE_IN_BYTES=\"{sectorSizeInBytes}\"");
        sb.Append($" num_partition_sectors=\"{numPartitionSectors}\"");
        sb.Append($" physical_partition_number=\"{physicalPartitionNumber}\"");
        sb.Append($" start_sector=\"{startSector}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;read&gt; XML command.
    /// <para>构建 &lt;read&gt; XML 命令。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="numPartitionSectors">The number of sectors to read. 待读取扇区数。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="label">Optional partition label. 可选分区标签。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The read XML string. 读取 XML 字符串。</returns>
    public static string BuildRead(long sectorSizeInBytes, long numPartitionSectors,
        string filename, int physicalPartitionNumber, long startSector,
        string? label = null, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<read SECTOR_SIZE_IN_BYTES=\"{sectorSizeInBytes}\"");
        sb.Append($" num_partition_sectors=\"{numPartitionSectors}\"");
        sb.Append($" filename=\"{EscapeXml(filename)}\"");
        sb.Append($" physical_partition_number=\"{physicalPartitionNumber}\"");
        sb.Append($" start_sector=\"{startSector}\"");
        if (!string.IsNullOrEmpty(label))
        {
            sb.Append($" label=\"{EscapeXml(label)}\"");
        }

        if (slot.HasValue && slot.Value != 0)
        {
            sb.Append($" slot=\"{slot.Value}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;erase&gt; XML command.
    /// <para>构建 &lt;erase&gt; XML 命令。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="numPartitionSectors">The number of sectors to erase. 待擦除扇区数。</param>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The erase XML string. 擦除 XML 字符串。</returns>
    public static string BuildErase(long sectorSizeInBytes, long numPartitionSectors,
        int physicalPartitionNumber, long startSector, int? slot = null)
        => BuildErase(sectorSizeInBytes, numPartitionSectors, physicalPartitionNumber,
            new FirehoseSectorAddress(startSector, false), slot);

    internal static string BuildErase(long sectorSizeInBytes, long numPartitionSectors,
        int physicalPartitionNumber, FirehoseSectorAddress startSector, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<erase SECTOR_SIZE_IN_BYTES=\"{sectorSizeInBytes}\"");
        sb.Append($" num_partition_sectors=\"{numPartitionSectors}\"");
        sb.Append($" physical_partition_number=\"{physicalPartitionNumber}\"");
        sb.Append($" start_sector=\"{startSector}\"");
        if (slot.HasValue && slot.Value != 0)
        {
            sb.Append($" slot=\"{slot.Value}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Escapes XML special characters in the specified value.
    /// <para>转义指定值中的 XML 特殊字符。</para>
    /// </summary>
    /// <param name="value">The value to escape. 待转义的值。</param>
    /// <returns>The escaped value. 转义后的值。</returns>
    private static string EscapeXml(string? value) => value == null ? string.Empty : System.Security.SecurityElement.Escape(value) ?? value;
}

/// <summary>
/// UFS provisioning common (configuration descriptor) parameters.
/// <para>UFS provisioning 公共（配置描述符）参数。</para>
/// </summary>
public sealed class UfsCommon
{
    /// <summary>Gets or sets the number of logical units. 逻辑单元数量。</summary>
    public int BNumberLU { get; set; }
    /// <summary>Gets or sets the boot enable flag. 启动使能标志。</summary>
    public int BBootEnable { get; set; }
    /// <summary>Gets or sets the descriptor access enable flag. 描述符访问使能标志。</summary>
    public int BDescrAccessEn { get; set; }
    /// <summary>Gets or sets the initial power mode. 初始电源模式。</summary>
    public int BInitPowerMode { get; set; }
    /// <summary>Gets or sets the high priority LUN. 高优先级 LUN。</summary>
    public int BHighPriorityLUN { get; set; }
    /// <summary>Gets or sets the secure removal type. 安全擦除类型。</summary>
    public int BSecureRemovalType { get; set; }
    /// <summary>Gets or sets the initial active ICC level. 初始活跃 ICC 等级。</summary>
    public int BInitActiveICCLevel { get; set; }
    /// <summary>Gets or sets the periodic RTC update. 周期性 RTC 更新。</summary>
    public int WPeriodicRTCUpdate { get; set; }
    /// <summary>Gets or sets the configuration descriptor lock. 配置描述符锁定。</summary>
    public int BConfigDescrLock { get; set; }
}

/// <summary>
/// UFS provisioning per-LUN body parameters.
/// <para>UFS provisioning 的每 LUN 主体参数。</para>
/// </summary>
public sealed class UfsBody
{
    /// <summary>Gets or sets the LUN number. LUN 编号。</summary>
    public int LUNum { get; set; }
    /// <summary>Gets or sets the LU enable flag. LU 使能标志。</summary>
    public int BLUEnable { get; set; }
    /// <summary>Gets or sets the boot LUN id. 启动 LUN 编号。</summary>
    public int BBootLUNID { get; set; }
    /// <summary>Gets or sets the LUN size in kilobytes. LUN 大小（KB）。</summary>
    public long SizeInKb { get; set; }
    /// <summary>Gets or sets the data reliability. 数据可靠性。</summary>
    public int BDataReliability { get; set; }
    /// <summary>Gets or sets the LU write protect. LU 写保护。</summary>
    public int BLUWriteProtect { get; set; }
    /// <summary>Gets or sets the memory type. 内存类型。</summary>
    public int BMemoryType { get; set; }
    /// <summary>Gets or sets the logical block size. 逻辑块大小。</summary>
    public int BLogicalBlockSize { get; set; }
    /// <summary>Gets or sets the provisioning type. 供应类型。</summary>
    public int BProvisioningType { get; set; }
    /// <summary>Gets or sets the context capacities. 上下文容量。</summary>
    public int WContextCapacities { get; set; }
    /// <summary>Gets or sets the optional description. 可选描述。</summary>
    public string? Desc { get; set; }
}

/// <summary>
/// UFS provisioning epilogue parameters.
/// <para>UFS provisioning 结语参数。</para>
/// </summary>
public sealed class UfsEpilogue
{
    /// <summary>Gets or sets the LUN to grow (serialized as LUNtoGrow). 待扩容的 LUN（序列化为 LUNtoGrow）。</summary>
    public int LUNumToGrow { get; set; }
}