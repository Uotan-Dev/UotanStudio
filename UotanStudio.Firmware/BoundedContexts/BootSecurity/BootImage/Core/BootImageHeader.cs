using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

public class BootImageHeader
{
    public BootImageVersion HeaderVersion { get; set; } = BootImageVersion.V0;

    public uint HeaderSize { get; set; } = 1664;

    public ulong KernelAddr { get; set; }

    public ulong RamdiskAddr { get; set; }

    public ulong SecondAddr { get; set; }

    public ulong TagsAddr { get; set; }

    public uint PageSize { get; set; }

    public uint KernelSize { get; set; }

    public uint RamdiskSize { get; set; }

    public uint SecondSize { get; set; }

    public uint DtbSize { get; set; }

    public ulong KernelLoadAddr { get; set; }

    public uint RecoveryDtboSize { get; set; }

    public ulong RecoveryDtboOffset { get; set; }

    public uint SignatureSize { get; set; }

    public string CommandLine { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;

    public string ImageName { get; set; } = string.Empty;

    public CompressionType KernelCompression { get; set; }

    public CompressionType RamdiskCompression { get; set; }

    public uint KernelOffset { get; set; }

    public uint RamdiskOffset { get; set; }

    public uint SecondOffset { get; set; }

    public uint TagsOffset { get; set; }

    public uint[] Ids { get; set; } = new uint[8];

    public uint OsVersion { get; set; }

    public string OsVersionString
    {
        get
        {
            var major = (OsVersion >> 25) & 0x7Fu;
            var minor = (OsVersion >> 18) & 0x7Fu;
            var patch = (OsVersion >> 11) & 0x7Fu;
            if (major == 0 && minor == 0 && patch == 0)
            {
                return string.Empty;
            }

            return $"{major}.{minor}.{patch}";
        }
    }

    public uint Architecture { get; set; }

    /// <summary>
    /// Converts to a string format similar to the original header output.
    /// <para>转换为字符串格式，输出头部信息。</para>
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Header Version: {HeaderVersion}");
        sb.AppendLine($"Header Size: {HeaderSize}");
        sb.AppendLine($"Kernel Address: 0x{KernelAddr:X16}");
        sb.AppendLine($"Ramdisk Address: 0x{RamdiskAddr:X16}");
        sb.AppendLine($"Second Address: 0x{SecondAddr:X16}");
        sb.AppendLine($"Tags Address: 0x{TagsAddr:X16}");
        sb.AppendLine($"Page Size: {PageSize}");
        sb.AppendLine($"Kernel Size: {KernelSize}");
        sb.AppendLine($"Ramdisk Size: {RamdiskSize}");
        sb.AppendLine($"Second Size: {SecondSize}");
        sb.AppendLine($"Dtb Size: {DtbSize}");
        sb.AppendLine($"Kernel Load Address: 0x{KernelLoadAddr:X16}");
        sb.AppendLine($"Recovery DTBO Size: {RecoveryDtboSize}");
        sb.AppendLine($"Recovery DTBO Offset: 0x{RecoveryDtboOffset:X16}");
        sb.AppendLine($"Command Line: {CommandLine}");
        sb.AppendLine($"Product Name: {ProductName}");
        sb.AppendLine($"Image Name: {ImageName}");
        return sb.ToString();
    }

    /// <summary>
    /// Serializes to text format for saving and restoring.
    /// <para>序列化为文本格式，用于保存和恢复。</para>
    /// </summary>
    public string ToHeaderFile()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"header_version={HeaderVersion}");
        sb.AppendLine($"header_size={HeaderSize}");
        sb.AppendLine($"kernel_address=0x{KernelAddr:X16}");
        sb.AppendLine($"ramdisk_address=0x{RamdiskAddr:X16}");
        sb.AppendLine($"second_address=0x{SecondAddr:X16}");
        sb.AppendLine($"tags_address=0x{TagsAddr:X16}");
        sb.AppendLine($"pagesize={PageSize}");
        sb.AppendLine($"kernel_size={KernelSize}");
        sb.AppendLine($"ramdisk_size={RamdiskSize}");
        sb.AppendLine($"second_size={SecondSize}");
        sb.AppendLine($"dtb_size={DtbSize}");
        sb.AppendLine($"kernel_offset=0x{KernelOffset:X16}");
        sb.AppendLine($"ramdisk_offset=0x{RamdiskOffset:X16}");
        sb.AppendLine($"second_offset=0x{SecondOffset:X16}");
        sb.AppendLine($"tags_offset=0x{TagsOffset:X16}");
        sb.AppendLine($"os_version={OsVersion}");
        sb.AppendLine($"architecture={Architecture}");
        sb.AppendLine($"commandline={CommandLine}");
        sb.AppendLine($"name={ImageName}");
        sb.AppendLine($"product={ProductName}");
        sb.AppendLine($"recovery_dtbo_size={RecoveryDtboSize}");
        sb.AppendLine($"recovery_dtbo_offset=0x{RecoveryDtboOffset:X16}");
        sb.AppendLine($"signature_size={SignatureSize}");
        return sb.ToString();
    }

    /// <summary>
    /// Parses header information from text format.
    /// <para>从文本格式解析头部信息。</para>
    /// </summary>
    public static BootImageHeader? ParseHeaderFile(string content)
    {
        try
        {
            var header = new BootImageHeader();
            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var parts = trimmed.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                var key = parts[0].Trim();
                var value = parts[1].Trim();

                switch (key)
                {
                    case "header_version":

                        header.HeaderVersion = (BootImageVersion)Enum.Parse(
                            typeof(BootImageVersion), value, ignoreCase: true);
                        break;
                    case "header_size":
                        header.HeaderSize = uint.Parse(value);
                        break;
                    case "kernel_address":
                        header.KernelAddr = ParseHexUInt64(value);
                        break;
                    case "ramdisk_address":
                        header.RamdiskAddr = ParseHexUInt64(value);
                        break;
                    case "second_address":
                        header.SecondAddr = ParseHexUInt64(value);
                        break;
                    case "tags_address":
                        header.TagsAddr = ParseHexUInt64(value);
                        break;
                    case "pagesize":
                        header.PageSize = uint.Parse(value);
                        break;
                    case "kernel_size":
                        header.KernelSize = uint.Parse(value);
                        break;
                    case "ramdisk_size":
                        header.RamdiskSize = uint.Parse(value);
                        break;
                    case "second_size":
                        header.SecondSize = uint.Parse(value);
                        break;
                    case "dtb_size":
                        header.DtbSize = uint.Parse(value);
                        break;
                    case "kernel_offset":
                        header.KernelOffset = ParseHexUInt32(value);
                        break;
                    case "ramdisk_offset":
                        header.RamdiskOffset = ParseHexUInt32(value);
                        break;
                    case "second_offset":
                        header.SecondOffset = ParseHexUInt32(value);
                        break;
                    case "tags_offset":
                        header.TagsOffset = ParseHexUInt32(value);
                        break;
                    case "os_version":
                        header.OsVersion = uint.Parse(value);
                        break;
                    case "architecture":
                        header.Architecture = uint.Parse(value);
                        break;
                    case "commandline":
                        header.CommandLine = value;
                        break;
                    case "name":
                        header.ImageName = value;
                        break;
                    case "product":
                        header.ProductName = value;
                        break;
                    case "recovery_dtbo_size":
                        header.RecoveryDtboSize = uint.Parse(value);
                        break;
                    case "recovery_dtbo_offset":
                        header.RecoveryDtboOffset = ParseHexUInt64(value);
                        break;
                    case "signature_size":
                        header.SignatureSize = uint.Parse(value);
                        break;
                    default:
                        break;
                }
            }
            return header;
        }
        catch
        {
            return null;
        }
    }

    private static string StripHexPrefix(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

    private static ulong ParseHexUInt64(string value) => Convert.ToUInt64(StripHexPrefix(value), 16);

    private static uint ParseHexUInt32(string value) => Convert.ToUInt32(StripHexPrefix(value), 16);
}
