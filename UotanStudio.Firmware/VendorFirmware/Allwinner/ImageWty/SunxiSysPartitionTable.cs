namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>
/// One partition definition from the vendor <c>sys_partition.fex</c> file list.
/// <para>厂商 <c>sys_partition.fex</c> 文件清单中的一条分区定义。</para>
/// </summary>
/// <param name="Name">Partition name. <para>分区名。</para></param>
/// <param name="DownloadFile">Image file the partition definition names, or an empty string when it names none.
/// <para>分区定义点名的镜像文件；未点名时为空字符串。</para></param>
/// <param name="SizeKilobytes">Declared partition size in KiB, or zero when the definition does not declare one.
/// <para>声明的分区大小（KiB）；未声明时为 0。</para></param>
public sealed record SunxiPartitionDefinition(string Name, string DownloadFile, long SizeKilobytes);

/// <summary>
/// Reads the INI-like vendor partition list stored as <c>sys_partition.fex</c>. Only the partition name,
/// download file and optional size are retained; every other key is ignored.
/// <para>读取以 <c>sys_partition.fex</c> 存储的 INI 形式厂商分区清单。仅保留分区名、下载文件与可选大小，
/// 其余键忽略。</para>
/// </summary>
public static class SunxiSysPartitionTable
{
    /// <summary>Largest file list the parser accepts. <para>解析器接受的最大文件清单字节数。</para></summary>
    public const int MaximumBytes = 1024 * 1024;

    /// <summary>
    /// Parses a partition file list into its partition definitions.
    /// <para>把分区文件清单解析为分区定义。</para>
    /// </summary>
    /// <param name="content">File list bytes, normally ASCII text. <para>文件清单字节，通常为 ASCII 文本。</para></param>
    /// <returns>Definitions in file order; empty when no <c>[partition]</c> block carries a name.
    /// <para>按文件顺序排列的定义；没有携带名称的 <c>[partition]</c> 块时为空。</para></returns>
    /// <exception cref="InvalidDataException">The content exceeds <see cref="MaximumBytes"/>.
    /// <para>内容超过 <see cref="MaximumBytes"/>。</para></exception>
    public static IReadOnlyList<SunxiPartitionDefinition> Parse(ReadOnlySpan<byte> content)
    {
        if (content.Length > MaximumBytes)
        {
            throw new InvalidDataException(
                $"The partition file list is {content.Length} bytes, above the {MaximumBytes}-byte limit.");
        }

        var partitions = new List<SunxiPartitionDefinition>();
        var inPartitionSection = false;
        var name = string.Empty;
        var downloadFile = string.Empty;
        var size = 0L;

        foreach (var rawLine in System.Text.Encoding.ASCII.GetString(content).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Equals("[partition_start]", StringComparison.Ordinal))
            {
                inPartitionSection = true;
                continue;
            }

            if (line.Equals("[partition]", StringComparison.Ordinal))
            {
                AddPartition(partitions, name, downloadFile, size);
                name = string.Empty;
                downloadFile = string.Empty;
                size = 0;
                inPartitionSection = true;
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                AddPartition(partitions, name, downloadFile, size);
                name = string.Empty;
                downloadFile = string.Empty;
                size = 0;
                inPartitionSection = line.Equals("[mbr]", StringComparison.Ordinal);
                continue;
            }

            if (!inPartitionSection)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (key.Equals("downloadfile", StringComparison.OrdinalIgnoreCase))
            {
                downloadFile = value;
            }
            else if (key.Equals("size", StringComparison.OrdinalIgnoreCase))
            {
                size = ParseSizeKilobytes(value);
            }
        }

        AddPartition(partitions, name, downloadFile, size);
        return partitions;
    }

    private static void AddPartition(List<SunxiPartitionDefinition> partitions, string name,
        string downloadFile, long size)
    {
        if (name.Length > 0)
        {
            partitions.Add(new SunxiPartitionDefinition(name, downloadFile, size));
        }
    }

    private static long ParseSizeKilobytes(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var hex)
                ? hex
                : 0;
        }

        return long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}