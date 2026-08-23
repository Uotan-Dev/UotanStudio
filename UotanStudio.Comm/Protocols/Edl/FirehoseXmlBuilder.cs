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
public static class FirehoseXmlBuilder
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
    /// Builds a &lt;getsha256&gt; XML command to retrieve the SHA256 hash of a partition region
    /// from the target.
    /// <para>构建 &lt;getsha256&gt; XML 命令，从目标设备获取分区区域的 SHA256 哈希值。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="numPartitionSectors">The number of sectors to hash. 待哈希扇区数。</param>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <returns>The getsha256 XML string. getsha256 XML 字符串。</returns>
    public static string BuildGetSha256(long sectorSizeInBytes, long numPartitionSectors,
        int physicalPartitionNumber, long startSector)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<getsha256 SECTOR_SIZE_IN_BYTES=\"{sectorSizeInBytes}\"");
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
    /// Builds the &lt;nop&gt; XML health-probe command. The lowercase element name matters:
    /// Firehose XML element names are case-sensitive and an uppercase &lt;NOP&gt; may be
    /// rejected by strict programmers.
    /// <para>构建 &lt;nop&gt; XML 健康探测命令。元素名必须小写：Firehose XML 元素名大小写
    /// 敏感，大写的 &lt;NOP&gt; 可能被严格解析的 programmer 拒绝。</para>
    /// </summary>
    /// <param name="value">The nop value (default "ping"). nop 值（默认 "ping"）。</param>
    /// <returns>The nop XML string. nop XML 字符串。</returns>
    public static string BuildNop(string value = "ping")
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<nop value=\"{EscapeXml(value)}\" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;power&gt; XML command. No DelayInSeconds is emitted: on real hardware the
    /// delay attribute made devices hang after "Calling handler for power" without resetting.
    /// <para>构建 &lt;power&gt; XML 命令。不输出 DelayInSeconds：真机上带 delay 属性时设备
    /// 只回 "Calling handler for power" 后挂起、复位永不执行。</para>
    /// </summary>
    /// <param name="option">The reboot option. 复位选项。</param>
    /// <returns>The power XML string. 电源 XML 字符串。</returns>
    public static string BuildPower(RebootOption option)
    {
        var valueStr = option switch
        {
            RebootOption.Reset => "reset",
            RebootOption.PowerOff => "off",
            RebootOption.ResetToEdl => "reset_to_edl",
            _ => "reset"
        };

        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<power value=\"{valueStr}\" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;patch&gt; XML command. Returns an empty string for filename="DISK"
    /// (the patch applies to no physical partition and must be skipped).
    /// <para>构建 &lt;patch&gt; XML 命令。filename="DISK" 时返回空字符串（该补丁不针对任何
    /// 物理分区，必须跳过）。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="byteOffset">The byte offset within the sector. 扇区内字节偏移。</param>
    /// <param name="value">The value to patch (hex string). 待写入的值（十六进制字符串）。</param>
    /// <param name="sizeInBytes">The size in bytes. 大小（字节）。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The patch XML string, or empty when the patch targets DISK. patch XML 字符串；目标为 DISK 时为空。</returns>
    public static string BuildPatch(long sectorSizeInBytes, string filename,
        int physicalPartitionNumber, long startSector,
        long byteOffset, string value, long sizeInBytes, int? slot = null)
    {
        if (string.Equals(filename, "DISK", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<patch SECTOR_SIZE_IN_BYTES=\"{sectorSizeInBytes}\"");
        sb.Append($" filename=\"{EscapeXml(filename)}\"");
        sb.Append($" physical_partition_number=\"{physicalPartitionNumber}\"");
        sb.Append($" start_sector=\"{startSector}\"");
        sb.Append($" byte_offset=\"{byteOffset}\"");
        sb.Append($" value=\"{EscapeXml(value)}\"");
        sb.Append($" size_in_bytes=\"{sizeInBytes}\"");
        if (slot.HasValue && slot.Value != 0)
        {
            sb.Append($" slot=\"{slot.Value}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds a wipe pseudo-command: a &lt;program&gt; that writes zeros to the first N sectors
    /// of a partition (mirrors fh_loader --wipefirst).
    /// <para>构建擦写伪命令：通过 &lt;program&gt; 向分区首部写入零字节（对齐 fh_loader
    /// --wipefirst）。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="numPartitionSectors">The number of sectors to wipe. 待擦写扇区数。</param>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="zeroFilename">The filename identifier for the zeros payload. 零字节负载的文件名标识。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The wipe (program-with-zeros) XML string. 擦写（写零编程）XML 字符串。</returns>
    public static string BuildWipe(long sectorSizeInBytes, long numPartitionSectors,
        int physicalPartitionNumber, long startSector,
        string zeroFilename = "Zeros16KB.bin", int? slot = null)
    {
        if (string.IsNullOrEmpty(zeroFilename))
        {
            zeroFilename = "Zeros16KB.bin";
        }

        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<program SECTOR_SIZE_IN_BYTES=\"{sectorSizeInBytes}\"");
        sb.Append($" num_partition_sectors=\"{numPartitionSectors}\"");
        sb.Append($" filename=\"{EscapeXml(zeroFilename)}\"");
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
    /// Builds the &lt;setbootablestoragedrive&gt; XML command.
    /// <para>构建 &lt;setbootablestoragedrive&gt; XML 命令。</para>
    /// </summary>
    /// <param name="value">The bootable storage drive number (UFS→1, eMMC→0). 可启动存储驱动器编号（UFS→1，eMMC→0）。</param>
    /// <returns>The setbootablestoragedrive XML string. setbootablestoragedrive XML 字符串。</returns>
    public static string BuildSetBootableStorageDrive(int value)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<setbootablestoragedrive value=\"{value}\" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;getstorageinfo&gt; XML command.
    /// <para>构建 &lt;getstorageinfo&gt; XML 命令。</para>
    /// </summary>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <returns>The getstorageinfo XML string. getstorageinfo XML 字符串。</returns>
    public static string BuildGetStorageInfo(int physicalPartitionNumber)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<getstorageinfo physical_partition_number=\"{physicalPartitionNumber}\" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;setactivepartition&gt; XML command for setting the active A/B slot.
    /// <para>构建用于设置活跃 A/B 槽位的 &lt;setactivepartition&gt; XML 命令。</para>
    /// </summary>
    /// <param name="slot">The slot identifier to set as active (e.g. "a" or "b"). 待设置为活跃的槽位标识（如 "a" 或 "b"）。</param>
    /// <returns>The setactivepartition XML string. setactivepartition XML 字符串。</returns>
    public static string BuildSetActivePartition(string slot)
    {
        if (string.IsNullOrEmpty(slot))
        {
            throw new ArgumentException("Slot cannot be null or empty.", nameof(slot));
        }

        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<setactivepartition slot=\"{EscapeXml(slot)}\" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;peek&gt; XML command for reading memory from the target device.
    /// <para>构建用于从目标设备读取内存的 &lt;peek&gt; XML 命令。</para>
    /// </summary>
    /// <param name="sizeInBytes">The number of bytes to read. 待读取字节数。</param>
    /// <param name="address">The memory address to read from (hexadecimal string). 待读取的内存地址（十六进制字符串）。</param>
    /// <returns>The peek XML string. peek XML 字符串。</returns>
    public static string BuildPeek(long sizeInBytes, string address)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<peek size_in_bytes=\"{sizeInBytes}\"");
        sb.Append($" address=\"{EscapeXml(address)}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;poke&gt; XML command for writing memory on the target device.
    /// <para>构建用于向目标设备写入内存的 &lt;poke&gt; XML 命令。</para>
    /// </summary>
    /// <param name="sizeInBytes">The number of bytes to write. 待写入字节数。</param>
    /// <param name="address">The memory address to write to (hexadecimal string). 待写入的内存地址（十六进制字符串）。</param>
    /// <param name="value">The value to write (hexadecimal string). 待写入的值（十六进制字符串）。</param>
    /// <returns>The poke XML string. poke XML 字符串。</returns>
    public static string BuildPoke(long sizeInBytes, string address, string value)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<poke size_in_bytes=\"{sizeInBytes}\"");
        sb.Append($" address=\"{EscapeXml(address)}\"");
        sb.Append($" value=\"{EscapeXml(value)}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;pokedword&gt; XML command for writing a 32-bit word to the target device.
    /// <para>构建用于向目标设备写入 32 位字的 &lt;pokedword&gt; XML 命令。</para>
    /// </summary>
    /// <param name="address">The target address (hexadecimal string). 目标地址（十六进制字符串）。</param>
    /// <param name="value">The 32-bit value to write (hexadecimal string). 待写入的 32 位值（十六进制字符串）。</param>
    /// <returns>The pokedword XML string. pokedword XML 字符串。</returns>
    public static string BuildPokeDword(string address, string value)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<pokedword address=\"{EscapeXml(address)}\"");
        sb.Append($" value=\"{EscapeXml(value)}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the &lt;memcpy&gt; XML command for performing memory copy on the target device.
    /// <para>构建用于在目标设备执行内存拷贝的 &lt;memcpy&gt; XML 命令。</para>
    /// </summary>
    /// <param name="dstOffset">Destination address (hexadecimal string). 目标地址（十六进制字符串）。</param>
    /// <param name="srcOffset">Source address (hexadecimal string). 源地址（十六进制字符串）。</param>
    /// <param name="size">Copy size in bytes. 拷贝大小（字节）。</param>
    /// <returns>The memcpy XML string. memcpy XML 字符串。</returns>
    public static string BuildMemcpy(string dstOffset, string srcOffset, long size)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<memcpy dst_offset=\"{EscapeXml(dstOffset)}\"");
        sb.Append($" src_offset=\"{EscapeXml(srcOffset)}\"");
        sb.Append($" size=\"{size}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the UFS provisioning common &lt;ufs&gt; XML command.
    /// <para>构建 UFS provisioning 公共 &lt;ufs&gt; XML 命令。</para>
    /// </summary>
    /// <param name="ufs">The common configuration. 公共配置。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The UFS common XML string. UFS 公共 XML 字符串。</returns>
    public static string BuildUfsCommon(UfsCommon ufs, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append("<ufs");
        sb.Append($" BNumberLU=\"{ufs.BNumberLU}\"");
        sb.Append($" BBootEnable=\"{ufs.BBootEnable}\"");
        sb.Append($" BDescrAccessEn=\"{ufs.BDescrAccessEn}\"");
        sb.Append($" BInitPowerMode=\"{ufs.BInitPowerMode}\"");
        sb.Append($" BHighPriorityLUN=\"{ufs.BHighPriorityLUN}\"");
        sb.Append($" BSecureRemovalType=\"{ufs.BSecureRemovalType}\"");
        sb.Append($" BInitActiveICCLevel=\"{ufs.BInitActiveICCLevel}\"");
        sb.Append($" WPeriodicRTCUpdate=\"{ufs.WPeriodicRTCUpdate}\"");
        if (ufs.BConfigDescrLock != 0)
        {
            sb.Append($" BConfigDescrLock=\"{ufs.BConfigDescrLock}\"");
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
    /// Builds the UFS provisioning per-LUN body &lt;ufs&gt; XML command.
    /// <para>构建 UFS provisioning 的每 LUN 主体 &lt;ufs&gt; XML 命令。</para>
    /// </summary>
    /// <param name="ufs">The LUN body configuration. LUN 主体配置。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The UFS body XML string. UFS 主体 XML 字符串。</returns>
    public static string BuildUfsBody(UfsBody ufs, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append("<ufs");
        sb.Append($" LUNum=\"{ufs.LUNum}\"");
        sb.Append($" BLUEnable=\"{ufs.BLUEnable}\"");
        sb.Append($" BBootLUNID=\"{ufs.BBootLUNID}\"");
        sb.Append($" size_in_kb=\"{ufs.SizeInKb}\"");
        sb.Append($" BDataReliability=\"{ufs.BDataReliability}\"");
        sb.Append($" BLUWriteProtect=\"{ufs.BLUWriteProtect}\"");
        sb.Append($" BMemoryType=\"{ufs.BMemoryType}\"");
        sb.Append($" BLogicalBlockSize=\"{ufs.BLogicalBlockSize}\"");
        sb.Append($" BProvisioningType=\"{ufs.BProvisioningType}\"");
        // Attribute key mirrors qdl firehose.c "wContextCapabilities" (plural -Capabilities).
        // 属性键对齐 qdl firehose.c 的 "wContextCapabilities"（复数 -Capabilities）。
        sb.Append($" wContextCapabilities=\"{ufs.WContextCapacities}\"");
        if (!string.IsNullOrEmpty(ufs.Desc))
        {
            sb.Append($" desc=\"{EscapeXml(ufs.Desc)}\"");
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
    /// Builds the UFS provisioning epilogue &lt;ufs&gt; XML command (LUNtoGrow + commit).
    /// <para>构建 UFS provisioning 结语 &lt;ufs&gt; XML 命令（LUNtoGrow + commit）。</para>
    /// </summary>
    /// <param name="ufs">The epilogue configuration. 结语配置。</param>
    /// <param name="commit">Whether to commit the configuration. 是否提交配置。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The UFS epilogue XML string. UFS 结语 XML 字符串。</returns>
    public static string BuildUfsEpilogue(UfsEpilogue ufs, bool commit, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append("<ufs");
        // Attribute key mirrors qdl firehose.c "LUNtoGrow" (capitalization: LUNtoGrow).
        // 属性键对齐 qdl firehose.c 的 "LUNtoGrow"（注意大小写：LUNtoGrow）。
        sb.Append($" LUNtoGrow=\"{ufs.LUNumToGrow}\"");
        sb.Append($" commit=\"{(commit ? 1 : 0)}\"");
        if (slot.HasValue && slot.Value != 0)
        {
            sb.Append($" slot=\"{slot.Value}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds a UFS reset pseudo-command: a &lt;ufs&gt; epilogue with commit="0" that discards
    /// any pending UFS provisioning configuration on the target without persisting.
    /// <para>构建 UFS 复位伪命令：通过 commit="0" 的 &lt;ufs&gt; 结语丢弃目标端待生效的 UFS
    /// provisioning 配置而不持久化。</para>
    /// </summary>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The UFS reset XML string. UFS 复位 XML 字符串。</returns>
    public static string BuildUfsReset(int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append("<ufs");
        sb.Append(" LUNtoGrow=\"0\"");
        sb.Append(" commit=\"0\"");
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