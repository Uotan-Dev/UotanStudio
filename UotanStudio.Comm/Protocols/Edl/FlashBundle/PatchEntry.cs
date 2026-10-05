namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Represents a single patch entry parsed from a patch XML file.
/// <para>表示从 patch XML 文件解析的单个 patch 条目。</para>
/// </summary>
internal sealed class PatchEntry
{
    /// <summary>
    /// Gets or sets the sector size in bytes for this patch entry.
    /// <para>获取或设置此 patch 条目的扇区大小（字节）。</para>
    /// </summary>
    public long SectorSizeInBytes { get; set; }

    /// <summary>
    /// Gets or sets the filename of the target image to patch.
    /// <para>获取或设置待修补目标镜像的文件名。</para>
    /// </summary>
    public string Filename { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the physical partition number on the target storage.
    /// <para>获取或设置目标存储的物理分区编号。</para>
    /// </summary>
    public int PhysicalPartitionNumber { get; set; }

    /// <summary>
    /// Gets or sets the start sector address, which can be numeric or a label string.
    /// <para>获取或设置起始扇区地址，可以是数字或标签字符串。</para>
    /// </summary>
    public string StartSector { get; set; } = "0";

    /// <summary>
    /// Gets or sets the byte offset within the sector where the patch should be applied.
    /// <para>获取或设置扇区内应用补丁的字节偏移。</para>
    /// </summary>
    public long ByteOffset { get; set; }

    /// <summary>
    /// Gets or sets the hex string value of the patch data.
    /// <para>获取或设置补丁数据的十六进制字符串值。</para>
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size in bytes of the patch data.
    /// <para>获取或设置补丁数据的大小（字节）。</para>
    /// </summary>
    public long SizeInBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of pages per block (for NAND storage).
    /// <para>获取或设置每块的页数（用于 NAND 存储）。</para>
    /// </summary>
    public int PagesPerBlock { get; set; }

    /// <summary>
    /// Gets or sets the slot number for dual-slot devices. Null indicates no slot specified.
    /// <para>获取或设置双槽位设备的槽位编号。Null 表示未指定槽位。</para>
    /// </summary>
    public int? Slot { get; set; }

    /// <summary>
    /// Gets or sets the partition label name associated with this patch entry.
    /// <para>获取或设置与此 patch 条目关联的分区标签名称。</para>
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Decodes the hex string <see cref="Value"/> property into a byte array.
    /// <para>将十六进制字符串 <see cref="Value"/> 属性解码为字节数组。</para>
    /// </summary>
    /// <returns>The decoded byte array from the hex string value. 从十六进制字符串值解码得到的字节数组。</returns>
    /// <exception cref="EdlException">Thrown when the hex string is invalid or empty. 十六进制字符串无效或为空时抛出。</exception>
    public byte[] GetDecodedValue()
    {
        var hex = (Value ?? string.Empty).Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        if (hex.Length == 0)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "Hex value string is empty. 十六进制值字符串为空。");
        }

        if (hex.Length % 2 != 0)
        {
            hex = "0" + hex;
        }

        for (var i = 0; i < hex.Length; i++)
        {
            if (char.IsAsciiHexDigit(hex[i]))
            {
                continue;
            }

            throw new EdlException(EdlErrorCode.InvalidParameter,
                $"Invalid hex string in patch value: '{hex.Substring(i - (i % 2), 2)}'. 补丁值中的十六进制字符串无效。");
        }

        return Convert.FromHexString(hex);
    }

    /// <summary>
    /// Returns a string representation of the patch entry.
    /// <para>返回 patch 条目的字符串表示。</para>
    /// </summary>
    /// <returns>A string describing the patch entry. 描述 patch 条目的字符串。</returns>
    public override string ToString() =>
        $"Patch[File={Filename}, StartSector={StartSector}, ByteOffset={ByteOffset}, Value={Value}]";
}