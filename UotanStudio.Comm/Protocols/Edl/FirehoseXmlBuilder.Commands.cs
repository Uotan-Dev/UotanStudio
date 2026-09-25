using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

public static partial class FirehoseXmlBuilder
{
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
    /// Builds the &lt;patch&gt; XML command, including patches targeting physical storage via DISK.
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="byteOffset">The byte offset within the sector. 扇区内字节偏移。</param>
    /// <param name="value">The value to patch (hex string). 待写入的值（十六进制字符串）。</param>
    /// <param name="sizeInBytes">The size in bytes. 大小（字节）。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The patch XML string.</returns>
    public static string BuildPatch(long sectorSizeInBytes, string filename,
        int physicalPartitionNumber, long startSector,
        long byteOffset, string value, long sizeInBytes, int? slot = null)
        => BuildPatch(sectorSizeInBytes, filename, physicalPartitionNumber,
            new FirehoseSectorAddress(startSector, false), byteOffset, value, sizeInBytes, slot);

    internal static string BuildPatch(long sectorSizeInBytes, string filename,
        int physicalPartitionNumber, FirehoseSectorAddress startSector,
        long byteOffset, string value, long sizeInBytes, int? slot = null)
    {
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

}