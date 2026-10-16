using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Builds the XML command documents exchanged with the Firehose programmer.
/// <para>构建与 Firehose programmer 交换的 XML 命令文档。</para>
/// Element names follow the Firehose XML command set and are lowercase where the
/// protocol uses lowercase (e.g. <c>&lt;nop/&gt;</c>), since Firehose XML element names
/// are case-sensitive.
/// <para>元素名遵循 Firehose XML 命令集；凡协议使用小写的地方均为小写
/// （如 <c>&lt;nop/&gt;</c>），因为 Firehose XML 元素名大小写敏感。</para>
/// </summary>
public static partial class FirehoseXmlBuilder
{
    /// <summary>
    /// Builds the &lt;configure&gt; XML command from the specified configuration.
    /// <para>根据指定配置构建 &lt;configure&gt; XML 命令。</para>
    /// </summary>
    /// <param name="config">The Firehose configuration. <para>Firehose 配置。</para></param>
    /// <returns>The configure XML string. <para>配置 XML 字符串。</para></returns>
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

        if (config.StorageType == FirehoseStorageType.Nand && config.NandPagesPerBlock > 0)
        {
            sb.Append($" PAGES_PER_BLOCK=\"{config.NandPagesPerBlock}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;program&gt; XML command.
    /// <para>构建 &lt;program&gt; XML 命令。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. <para>扇区大小（字节）。</para></param>
    /// <param name="numPartitionSectors">The number of sectors to program. <para>待编程扇区数。</para></param>
    /// <param name="filename">The target filename. <para>目标文件名。</para></param>
    /// <param name="physicalPartitionNumber">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="label">Optional partition label. <para>可选分区标签。</para></param>
    /// <param name="slot">Optional slot number. <para>可选槽位编号。</para></param>
    /// <returns>The program XML string. <para>编程 XML 字符串。</para></returns>
    public static string BuildProgram(long sectorSizeInBytes, long numPartitionSectors,
        string filename, int physicalPartitionNumber, long startSector,
        string? label = null, int? slot = null)
        => BuildProgram(sectorSizeInBytes, numPartitionSectors, filename, physicalPartitionNumber,
            new FirehoseSectorAddress(startSector, false), label, slot);

    internal static string BuildProgram(long sectorSizeInBytes, long numPartitionSectors,
        string filename, int physicalPartitionNumber, FirehoseSectorAddress startSector,
        string? label = null, int? slot = null, string? publicKey = null, string? token = null,
        long pagesPerBlock = 0)
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

        if (pagesPerBlock > 0)
        {
            sb.Append($" PAGES_PER_BLOCK=\"{pagesPerBlock}\"");
        }

        if (publicKey is not null && token is not null)
        {
            sb.Append($" pk=\"{EscapeXml(publicKey)}\" token=\"{EscapeXml(token)}\"");
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
    /// <param name="sectorSizeInBytes">The sector size in bytes. <para>扇区大小（字节）。</para></param>
    /// <param name="numPartitionSectors">The number of sectors to program. <para>待编程扇区数。</para></param>
    /// <param name="filename">The target filename. <para>目标文件名。</para></param>
    /// <param name="physicalPartitionNumber">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="digest">The hex string of the SHA256 digest. <para>SHA256 摘要的十六进制字符串。</para></param>
    /// <param name="digestSignature">The base64-encoded signature of the digest. <para>摘要的 Base64 编码签名。</para></param>
    /// <param name="signatureSize">The size of the signature in bytes. <para>签名大小（字节）。</para></param>
    /// <param name="label">Optional partition label. <para>可选分区标签。</para></param>
    /// <param name="slot">Optional slot number. <para>可选槽位编号。</para></param>
    /// <returns>The program XML string with digest attributes. <para>带摘要属性的编程 XML 字符串。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="digest"/> is not one SHA-256 width in hexadecimal, or <paramref name="signatureSize"/> does
    /// not equal the decoded length of <paramref name="digestSignature"/>.
    /// <para><paramref name="digest"/> 不是十六进制表示的单个 SHA-256 宽度，或 <paramref name="signatureSize"/>
    /// 不等于 <paramref name="digestSignature"/> 解码后的长度。</para></exception>
    /// <exception cref="FormatException"><paramref name="digestSignature"/> is not well-formed base64.
    /// <para><paramref name="digestSignature"/> 不是合法的 Base64。</para></exception>
    public static string BuildProgramWithDigest(long sectorSizeInBytes, long numPartitionSectors,
        string filename, int physicalPartitionNumber, long startSector,
        string digest, string digestSignature, int signatureSize,
        string? label = null, int? slot = null)
    {
        if (digest is not { Length: 64 } || digest.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentOutOfRangeException(nameof(digest),
                "The write digest must be 64 hexadecimal characters.");
        }

        var signatureBytes = Convert.FromBase64String(digestSignature);
        if (signatureBytes.Length != signatureSize)
        {
            throw new ArgumentOutOfRangeException(nameof(signatureSize), signatureSize,
                $"signature_size must equal the decoded digest_signature length, {signatureBytes.Length} bytes.");
        }

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
    /// Builds the &lt;read&gt; XML command.
    /// <para>构建 &lt;read&gt; XML 命令。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. <para>扇区大小（字节）。</para></param>
    /// <param name="numPartitionSectors">The number of sectors to read. <para>待读取扇区数。</para></param>
    /// <param name="filename">The target filename. <para>目标文件名。</para></param>
    /// <param name="physicalPartitionNumber">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="label">Optional partition label. <para>可选分区标签。</para></param>
    /// <param name="slot">Optional slot number. <para>可选槽位编号。</para></param>
    /// <param name="pagesPerBlock">The NAND pages-per-block hint, written as the <c>PAGES_PER_BLOCK</c>
    /// attribute only when positive; zero omits the attribute.
    /// <para>NAND 每块页数提示；仅当为正时以 <c>PAGES_PER_BLOCK</c> 属性写出，为零时省略该属性。</para></param>
    /// <returns>The read XML string. <para>读取 XML 字符串。</para></returns>
    public static string BuildRead(long sectorSizeInBytes, long numPartitionSectors,
        string filename, int physicalPartitionNumber, long startSector,
        string? label = null, int? slot = null, long pagesPerBlock = 0)
        => BuildRead(sectorSizeInBytes, numPartitionSectors, filename, physicalPartitionNumber,
            new FirehoseSectorAddress(startSector, false), label, slot, pagesPerBlock);

    internal static string BuildRead(long sectorSizeInBytes, long numPartitionSectors,
        string filename, int physicalPartitionNumber, FirehoseSectorAddress startSector,
        string? label = null, int? slot = null, long pagesPerBlock = 0)
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

        if (pagesPerBlock > 0)
        {
            sb.Append($" PAGES_PER_BLOCK=\"{pagesPerBlock}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;erase&gt; XML command.
    /// <para>构建 &lt;erase&gt; XML 命令。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. <para>扇区大小（字节）。</para></param>
    /// <param name="numPartitionSectors">The number of sectors to erase. <para>待擦除扇区数。</para></param>
    /// <param name="physicalPartitionNumber">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="slot">Optional slot number. <para>可选槽位编号。</para></param>
    /// <returns>The erase XML string. <para>擦除 XML 字符串。</para></returns>
    public static string BuildErase(long sectorSizeInBytes, long numPartitionSectors,
        int physicalPartitionNumber, long startSector, int? slot = null)
        => BuildErase(sectorSizeInBytes, numPartitionSectors, physicalPartitionNumber,
            new FirehoseSectorAddress(startSector, false), slot);

    internal static string BuildErase(long sectorSizeInBytes, long numPartitionSectors,
        int physicalPartitionNumber, FirehoseSectorAddress startSector, int? slot = null, long pagesPerBlock = 0)
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

        if (pagesPerBlock > 0)
        {
            sb.Append($" PAGES_PER_BLOCK=\"{pagesPerBlock}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Validates a program or wipe range and returns its total byte count. Every bound is checked before any
    /// command text is appended and the arithmetic is <see langword="checked"/>, so a refused range cannot
    /// leave a partially built document behind.
    /// <para>校验编程或擦写范围并返回其字节总量。所有边界都在追加任何命令文本之前完成检查，且算术为
    /// <see langword="checked"/>，因此被拒绝的范围不会留下构造到一半的文档。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes, 1 through 65536 inclusive.
    /// <para>扇区大小（字节），介于 1 与 65536（含）。</para></param>
    /// <param name="numPartitionSectors">The number of sectors, which must be positive.
    /// <para>扇区数，必须为正。</para></param>
    /// <param name="physicalPartitionNumber">The physical partition number or LUN, which must be non-negative.
    /// <para>物理分区编号或 LUN，必须非负。</para></param>
    /// <param name="startSector">The resolved absolute starting sector, which must be non-negative.
    /// <para>已解析的绝对起始扇区，必须非负。</para></param>
    /// <param name="slot">The optional slot number, which must be non-negative when it has a value.
    /// <para>可选槽位编号；有值时必须非负。</para></param>
    /// <returns>The total byte count of the range, <paramref name="numPartitionSectors"/> times
    /// <paramref name="sectorSizeInBytes"/>.
    /// <para>范围的字节总量，即 <paramref name="numPartitionSectors"/> 乘以
    /// <paramref name="sectorSizeInBytes"/>。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sectorSizeInBytes"/> is outside 1 through
    /// 65536, <paramref name="numPartitionSectors"/> is not positive, or <paramref name="physicalPartitionNumber"/>,
    /// <paramref name="startSector"/> or <paramref name="slot"/> is negative.
    /// <para><paramref name="sectorSizeInBytes"/> 不在 1 至 65536 之间，<paramref name="numPartitionSectors"/>
    /// 非正，或 <paramref name="physicalPartitionNumber"/>、<paramref name="startSector"/> 或
    /// <paramref name="slot"/> 为负。</para></exception>
    /// <exception cref="OverflowException">The end sector or the total byte count overflows <see cref="long"/>.
    /// <para>结束扇区或字节总量超出 <see cref="long"/> 范围。</para></exception>
    internal static long ValidateProgramRange(long sectorSizeInBytes, long numPartitionSectors,
        int physicalPartitionNumber, long startSector, int? slot)
    {
        if (sectorSizeInBytes is < 1 or > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSizeInBytes),
                "The sector size must be between 1 and 65536 bytes.");
        }

        if (numPartitionSectors <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numPartitionSectors),
                "The number of sectors must be positive.");
        }

        if (physicalPartitionNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalPartitionNumber),
                "The physical partition number must be non-negative.");
        }

        if (startSector < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSector),
                "The starting sector must be non-negative.");
        }

        if (slot is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot),
                "The slot must be non-negative.");
        }

        _ = checked(startSector + numPartitionSectors);
        return checked(numPartitionSectors * sectorSizeInBytes);
    }

    /// <summary>
    /// Builds the &lt;getsha256digest&gt; XML command asking the target to hash a flash range
    ///.
    /// <para>构建 &lt;getsha256digest&gt; XML 命令，请求目标对一段闪存范围计算哈希
    /// 。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. <para>扇区大小（字节）。</para></param>
    /// <param name="numPartitionSectors">The number of sectors to hash. <para>待计算哈希的扇区数。</para></param>
    /// <param name="physicalPartitionNumber">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="slot">Optional slot number emitted as the <c>slot</c> attribute when non-zero
    ///.
    /// <para>可选槽位编号；非零时以 <c>slot</c> 属性输出。</para></param>
    /// <returns>The getsha256digest XML string. <para>getsha256digest XML 字符串。</para></returns>
    public static string BuildGetSha256Digest(long sectorSizeInBytes, long numPartitionSectors,
        int physicalPartitionNumber, long startSector, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<getsha256digest SECTOR_SIZE_IN_BYTES=\"{sectorSizeInBytes}\"");
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
    /// Builds the &lt;poke&gt; XML command writing a bounded big-endian hex literal to target memory
    ///.
    /// <para>构建 &lt;poke&gt; XML 命令，向目标内存写入有界的大端十六进制字面量
    /// 。</para>
    /// </summary>
    /// <param name="address">The target memory address. <para>目标内存地址。</para></param>
    /// <param name="sizeInBytes">The number of bytes to write (1-8). <para>待写入字节数（1-8）。</para></param>
    /// <param name="valueHex">The big-endian hex literal with the <c>0x</c> prefix.
    /// <para>带 <c>0x</c> 前缀的大端十六进制字面量。</para></param>
    /// <param name="uppercaseSize">Whether the size attribute uses the <c>SizeInBytes</c> spelling.
    /// <para>大小属性是否使用 <c>SizeInBytes</c> 拼写。</para></param>
    /// <returns>The poke XML string. <para>poke XML 字符串。</para></returns>
    public static string BuildPoke(long address, int sizeInBytes, string valueHex, bool uppercaseSize = false)
    {
        var sizeAttribute = uppercaseSize ? "SizeInBytes" : "size_in_bytes";
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<poke address64=\"{address}\" {sizeAttribute}=\"{sizeInBytes}\" value64=\"{EscapeXml(valueHex)}\"");
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
    /// <summary>Gets or sets the number of logical units. <para>逻辑单元数量。</para></summary>
    /// <value>Corresponds to the <c>bNumberLU</c> attribute of the common &lt;ufs&gt; command.
    /// <para>对应公共 &lt;ufs&gt; 命令的 <c>bNumberLU</c> 属性。</para></value>
    public int BNumberLU { get; set; }
    /// <summary>Gets or sets the boot enable flag. <para>启动使能标志。</para></summary>
    /// <value>Corresponds to the <c>bBootEnable</c> attribute of the common &lt;ufs&gt; command.
    /// <para>对应公共 &lt;ufs&gt; 命令的 <c>bBootEnable</c> 属性。</para></value>
    public int BBootEnable { get; set; }
    /// <summary>Gets or sets the descriptor access enable flag. <para>描述符访问使能标志。</para></summary>
    /// <value>Corresponds to the <c>bDescrAccessEn</c> attribute of the common &lt;ufs&gt; command.
    /// <para>对应公共 &lt;ufs&gt; 命令的 <c>bDescrAccessEn</c> 属性。</para></value>
    public int BDescrAccessEn { get; set; }
    /// <summary>Gets or sets the initial power mode. <para>初始电源模式。</para></summary>
    /// <value>Corresponds to the <c>bInitPowerMode</c> attribute of the common &lt;ufs&gt; command.
    /// <para>对应公共 &lt;ufs&gt; 命令的 <c>bInitPowerMode</c> 属性。</para></value>
    public int BInitPowerMode { get; set; }
    /// <summary>Gets or sets the high priority LUN. <para>高优先级 LUN。</para></summary>
    /// <value>Corresponds to the <c>bHighPriorityLUN</c> attribute of the common &lt;ufs&gt; command.
    /// <para>对应公共 &lt;ufs&gt; 命令的 <c>bHighPriorityLUN</c> 属性。</para></value>
    public int BHighPriorityLUN { get; set; }
    /// <summary>Gets or sets the secure removal type. <para>安全擦除类型。</para></summary>
    /// <value>Corresponds to the <c>bSecureRemovalType</c> attribute of the common &lt;ufs&gt; command.
    /// <para>对应公共 &lt;ufs&gt; 命令的 <c>bSecureRemovalType</c> 属性。</para></value>
    public int BSecureRemovalType { get; set; }
    /// <summary>Gets or sets the initial active ICC level. <para>初始活跃 ICC 等级。</para></summary>
    /// <value>Corresponds to the <c>bInitActiveICCLevel</c> attribute of the common &lt;ufs&gt; command.
    /// <para>对应公共 &lt;ufs&gt; 命令的 <c>bInitActiveICCLevel</c> 属性。</para></value>
    public int BInitActiveICCLevel { get; set; }
    /// <summary>Gets or sets the periodic RTC update. <para>周期性 RTC 更新。</para></summary>
    /// <value>Corresponds to the <c>wPeriodicRTCUpdate</c> attribute of the common &lt;ufs&gt; command.
    /// <para>对应公共 &lt;ufs&gt; 命令的 <c>wPeriodicRTCUpdate</c> 属性。</para></value>
    public int WPeriodicRTCUpdate { get; set; }
    /// <summary>Gets or sets the configuration descriptor lock. <para>配置描述符锁定。</para></summary>
    /// <value>
    /// Corresponds to the <c>bConfigDescrLock</c> attribute of the common &lt;ufs&gt; command,
    /// which is written only when non-zero.
    /// <para>对应公共 &lt;ufs&gt; 命令的 <c>bConfigDescrLock</c> 属性，仅当非零时写出。</para>
    /// </value>
    public int BConfigDescrLock { get; set; }
}

/// <summary>
/// UFS provisioning per-LUN body parameters.
/// <para>UFS provisioning 的每 LUN 主体参数。</para>
/// </summary>
public sealed class UfsBody
{
    /// <summary>Gets or sets the LUN number. <para>LUN 编号。</para></summary>
    /// <value>Corresponds to the <c>LUNum</c> attribute of the per-LUN &lt;ufs&gt; command.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>LUNum</c> 属性。</para></value>
    public int LUNum { get; set; }
    /// <summary>Gets or sets the LU enable flag. <para>LU 使能标志。</para></summary>
    /// <value>Corresponds to the <c>bLUEnable</c> attribute of the per-LUN &lt;ufs&gt; command.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>bLUEnable</c> 属性。</para></value>
    public int BLUEnable { get; set; }
    /// <summary>Gets or sets the boot LUN id. <para>启动 LUN 编号。</para></summary>
    /// <value>Corresponds to the <c>bBootLunID</c> attribute of the per-LUN &lt;ufs&gt; command.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>bBootLunID</c> 属性。</para></value>
    public int BBootLUNID { get; set; }
    /// <summary>Gets or sets the LUN size in kilobytes. <para>LUN 大小（KB）。</para></summary>
    /// <value>Corresponds to the <c>size_in_kb</c> attribute of the per-LUN &lt;ufs&gt; command.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>size_in_kb</c> 属性。</para></value>
    public long SizeInKb { get; set; }
    /// <summary>Gets or sets the data reliability. <para>数据可靠性。</para></summary>
    /// <value>Corresponds to the <c>bDataReliability</c> attribute of the per-LUN &lt;ufs&gt; command.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>bDataReliability</c> 属性。</para></value>
    public int BDataReliability { get; set; }
    /// <summary>Gets or sets the LU write protect. <para>LU 写保护。</para></summary>
    /// <value>Corresponds to the <c>bLUWriteProtect</c> attribute of the per-LUN &lt;ufs&gt; command.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>bLUWriteProtect</c> 属性。</para></value>
    public int BLUWriteProtect { get; set; }
    /// <summary>Gets or sets the memory type. <para>内存类型。</para></summary>
    /// <value>Corresponds to the <c>bMemoryType</c> attribute of the per-LUN &lt;ufs&gt; command.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>bMemoryType</c> 属性。</para></value>
    public int BMemoryType { get; set; }
    /// <summary>Gets or sets the logical block size. <para>逻辑块大小。</para></summary>
    /// <value>Corresponds to the <c>bLogicalBlockSize</c> attribute of the per-LUN &lt;ufs&gt; command.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>bLogicalBlockSize</c> 属性。</para></value>
    public int BLogicalBlockSize { get; set; }
    /// <summary>Gets or sets the provisioning type. <para>供应类型。</para></summary>
    /// <value>Corresponds to the <c>bProvisioningType</c> attribute of the per-LUN &lt;ufs&gt; command.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>bProvisioningType</c> 属性。</para></value>
    public int BProvisioningType { get; set; }
    /// <summary>Gets or sets the context capacities. <para>上下文容量。</para></summary>
    /// <value>Corresponds to the <c>wContextCapabilities</c> attribute of the per-LUN
    /// &lt;ufs&gt; command.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>wContextCapabilities</c> 属性。</para></value>
    public int WContextCapacities { get; set; }
    /// <summary>Gets or sets the optional description. <para>可选描述。</para></summary>
    /// <value>
    /// Corresponds to the <c>desc</c> attribute of the per-LUN &lt;ufs&gt; command, written
    /// XML-escaped and only when non-empty.
    /// <para>对应每 LUN &lt;ufs&gt; 命令的 <c>desc</c> 属性，仅当非空时以 XML 转义后写出。</para>
    /// </value>
    public string? Desc { get; set; }
}

/// <summary>
/// UFS provisioning epilogue parameters.
/// <para>UFS provisioning 结语参数。</para>
/// </summary>
public sealed class UfsEpilogue
{
    /// <summary>Gets or sets the LUN to grow (serialized as LUNtoGrow). <para>待扩容的 LUN（序列化为 LUNtoGrow）。</para></summary>
    /// <value>Corresponds to the <c>LUNtoGrow</c> attribute of the epilogue &lt;ufs&gt; command;
    /// <c>BuildUfsReset</c> always writes 0.
    /// <para>对应结语 &lt;ufs&gt; 命令的 <c>LUNtoGrow</c> 属性；<c>BuildUfsReset</c> 固定写 0。</para></value>
    public int LUNumToGrow { get; set; }
}