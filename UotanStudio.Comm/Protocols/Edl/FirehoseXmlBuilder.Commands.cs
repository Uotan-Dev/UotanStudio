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
    /// <param name="value">The nop value (default "ping"). <para>nop 值（默认 "ping"）。</para></param>
    /// <returns>The nop XML string. <para>nop XML 字符串。</para></returns>
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
    /// <param name="option">The reboot option. <para>复位选项。</para></param>
    /// <returns>The power XML string. <para>电源 XML 字符串。</para></returns>
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
    /// <para>构建 &lt;patch&gt; XML 命令，包括通过 DISK 针对物理存储的补丁。</para>
    /// </summary>
    /// <param name="sectorSizeInBytes">The sector size in bytes. <para>扇区大小（字节）。</para></param>
    /// <param name="filename">The target filename. <para>目标文件名。</para></param>
    /// <param name="physicalPartitionNumber">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="byteOffset">The byte offset within the sector. <para>扇区内字节偏移。</para></param>
    /// <param name="value">The value to patch (hex string). <para>待写入的值（十六进制字符串）。</para></param>
    /// <param name="sizeInBytes">The size in bytes. <para>大小（字节）。</para></param>
    /// <param name="slot">Optional slot number. <para>可选槽位编号。</para></param>
    /// <returns>The patch XML string. <para>patch XML 字符串。</para></returns>
    public static string BuildPatch(long sectorSizeInBytes, string filename,
        int physicalPartitionNumber, long startSector,
        long byteOffset, string value, long sizeInBytes, int? slot = null)
        => BuildPatch(sectorSizeInBytes, filename, physicalPartitionNumber,
            new FirehoseSectorAddress(startSector, false), byteOffset, value, sizeInBytes, slot);

    internal static string BuildPatch(long sectorSizeInBytes, string filename,
        int physicalPartitionNumber, FirehoseSectorAddress startSector,
        long byteOffset, string value, long sizeInBytes, int? slot = null, string? publicKey = null, string? token = null)
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

        if (publicKey is not null && token is not null)
        {
            sb.Append($" pk=\"{EscapeXml(publicKey)}\" token=\"{EscapeXml(token)}\"");
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
    /// <param name="sectorSizeInBytes">The sector size in bytes. <para>扇区大小（字节）。</para></param>
    /// <param name="numPartitionSectors">The number of sectors to wipe. <para>待擦写扇区数。</para></param>
    /// <param name="physicalPartitionNumber">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="zeroFilename">The filename identifier for the zeros payload. <para>零字节负载的文件名标识。</para></param>
    /// <param name="slot">Optional slot number. <para>可选槽位编号。</para></param>
    /// <returns>The wipe (program-with-zeros) XML string. <para>擦写（写零编程）XML 字符串。</para></returns>
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
    /// <param name="value">The bootable storage drive number (UFS→1, eMMC→0). <para>可启动存储驱动器编号（UFS→1，eMMC→0）。</para></param>
    /// <returns>The setbootablestoragedrive XML string. <para>setbootablestoragedrive XML 字符串。</para></returns>
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
    /// <param name="physicalPartitionNumber">The physical partition number. <para>物理分区编号。</para></param>
    /// <returns>The getstorageinfo XML string. <para>getstorageinfo XML 字符串。</para></returns>
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
    /// <param name="slot">The slot identifier to set as active (e.g. "a" or "b"). <para>待设置为活跃的槽位标识（如 "a" 或 "b"）。</para></param>
    /// <returns>The setactivepartition XML string. <para>setactivepartition XML 字符串。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="slot"/> is null or empty.
    /// <para><paramref name="slot"/> 为 null 或空。</para>
    /// </exception>
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
    /// Builds the &lt;peek&gt; XML command for reading memory from the target device; some
    /// programmers spell the size attribute <c>SizeInBytes</c>, hence the case switch mirroring the
    /// poke builder.
    /// <para>构建用于从目标设备读取内存的 &lt;peek&gt; XML 命令；部分 programmer 将大小属性拼写成
    /// <c>SizeInBytes</c>，因此像 poke 构建器一样提供大小写切换。</para>
    /// </summary>
    /// <param name="sizeInBytes">The number of bytes to read. <para>待读取字节数。</para></param>
    /// <param name="address">The memory address to read from (hexadecimal string). <para>待读取的内存地址（十六进制字符串）。</para></param>
    /// <param name="uppercaseSize">Whether the size attribute uses the <c>SizeInBytes</c> spelling.
    /// <para>大小属性是否使用 <c>SizeInBytes</c> 拼写。</para></param>
    /// <returns>The peek XML string. <para>peek XML 字符串。</para></returns>
    public static string BuildPeek(long sizeInBytes, string address, bool uppercaseSize = false)
    {
        var sizeAttribute = uppercaseSize ? "SizeInBytes" : "size_in_bytes";
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<peek {sizeAttribute}=\"{sizeInBytes}\"");
        sb.Append($" address=\"{EscapeXml(address)}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

}