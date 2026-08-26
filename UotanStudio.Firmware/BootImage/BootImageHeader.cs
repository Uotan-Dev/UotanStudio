using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Android 启动镜像头部信息。
/// <para>Android boot image header information.</para>
/// <para>存储解析后的启动镜像元数据，包括各个组件的位置、大小和压缩格式等信息。</para>
/// <para>Stores parsed boot image metadata, including the position, size, and compression format of each component.</para>
/// </summary>
public class BootImageHeader
{
    /// <summary>
    /// 头部版本。
    /// <para>The header version.</para>
    /// </summary>
    public BootImageVersion HeaderVersion { get; set; } = BootImageVersion.V0;

    /// <summary>
    /// 头部大小（字节数）。
    /// <para>The header size in bytes.</para>
    /// </summary>
    public uint HeaderSize { get; set; } = 1664;

    /// <summary>
    /// 内核的起始地址。
    /// <para>The kernel load address.</para>
    /// </summary>
    public ulong KernelAddr { get; set; }

    /// <summary>
    /// RAMDisk 的起始地址。
    /// <para>The ramdisk load address.</para>
    /// </summary>
    public ulong RamdiskAddr { get; set; }

    /// <summary>
    /// 第二阶段引导程序的起始地址。
    /// <para>The second stage loader address.</para>
    /// </summary>
    public ulong SecondAddr { get; set; }

    /// <summary>
    /// 标签区的起始地址。
    /// <para>The tags address.</para>
    /// </summary>
    public ulong TagsAddr { get; set; }

    /// <summary>
    /// 页面大小（通常为 2048 或 4096 字节）。
    /// <para>The page size (typically 2048 or 4096 bytes).</para>
    /// </summary>
    public uint PageSize { get; set; }

    /// <summary>
    /// 内核镜像的大小。
    /// <para>The kernel image size in bytes.</para>
    /// </summary>
    public uint KernelSize { get; set; }

    /// <summary>
    /// RAMDisk 镜像的大小。
    /// <para>The ramdisk image size in bytes.</para>
    /// </summary>
    public uint RamdiskSize { get; set; }

    /// <summary>
    /// 第二阶段引导程序的大小。
    /// <para>The second stage loader size in bytes.</para>
    /// </summary>
    public uint SecondSize { get; set; }

    /// <summary>
    /// 设备树的大小。
    /// <para>The DTB size in bytes.</para>
    /// </summary>
    public uint DtbSize { get; set; }

    /// <summary>
    /// 内核的加载地址（V2 中为 DTB 地址）。
    /// <para>The kernel load address (DTB addr in v2).</para>
    /// </summary>
    public ulong KernelLoadAddr { get; set; }

    /// <summary>
    /// Recovery DTBO 的大小。
    /// <para>The size of the recovery DTBO.</para>
    /// </summary>
    public uint RecoveryDtboSize { get; set; }

    /// <summary>
    /// Recovery DTBO 的偏移量。
    /// <para>The offset of the recovery DTBO.</para>
    /// </summary>
    public ulong RecoveryDtboOffset { get; set; }

    /// <summary>
    /// 签名大小（V4）。
    /// <para>The signature size (V4).</para>
    /// </summary>
    public uint SignatureSize { get; set; }

    /// <summary>
    /// 命令行参数。
    /// <para>The kernel command line.</para>
    /// </summary>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>
    /// 产品名称。
    /// <para>The product name.</para>
    /// </summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>
    /// 镜像名称。
    /// <para>The image name.</para>
    /// </summary>
    public string ImageName { get; set; } = string.Empty;

    /// <summary>
    /// 内核的压缩格式。
    /// <para>The kernel compression type.</para>
    /// </summary>
    public CompressionType KernelCompression { get; set; }

    /// <summary>
    /// RAMDisk 的压缩格式。
    /// <para>The ramdisk compression type.</para>
    /// </summary>
    public CompressionType RamdiskCompression { get; set; }

    /// <summary>
    /// 内核偏移量。
    /// <para>The kernel offset.</para>
    /// </summary>
    public uint KernelOffset { get; set; }

    /// <summary>
    /// RAMDisk 偏移量。
    /// <para>The ramdisk offset.</para>
    /// </summary>
    public uint RamdiskOffset { get; set; }

    /// <summary>
    /// 第二阶段引导程序偏移量。
    /// <para>The second stage loader offset.</para>
    /// </summary>
    public uint SecondOffset { get; set; }

    /// <summary>
    /// 标签区偏移量。
    /// <para>The tags offset.</para>
    /// </summary>
    public uint TagsOffset { get; set; }

    /// <summary>
    /// ID 字段（用于存储额外信息）。
    /// <para>The ID field (used for storing additional information).</para>
    /// </summary>
    public uint[] Ids { get; set; } = new uint[8];

    /// <summary>
    /// 操作系统版本。
    /// <para>The OS version.</para>
    /// </summary>
    public uint OsVersion { get; set; }

    /// <summary>
    /// 解析后的操作系统版本字符串（如 <c>12.1.2</c>）。Android boot image 的
    /// <c>os_version</c> 字段按 7/7/7/11 位拆分为 major/minor/patch/patch_level，
    /// 直接以 <see cref="OsVersion"/> 原始整数显示会得到错误的数字串。
    /// <para>The decoded OS version string (e.g. <c>12.1.2</c>). The Android boot image
    /// <c>os_version</c> field packs major/minor/patch/patch_level into 7/7/7/11 bits;
    /// showing the raw <see cref="OsVersion"/> integer would produce an incorrect string.</para>
    /// </summary>
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

    /// <summary>
    /// 架构类型（0=arm, 1=arm64, 2=x86, 3=x64）。
    /// <para>The architecture type (0=arm, 1=arm64, 2=x86, 3=x64).</para>
    /// </summary>
    public uint Architecture { get; set; }

    /// <summary>
    /// 转换为字符串格式，输出头部信息。
    /// <para>Converts to a string format similar to the original header output.</para>
    /// </summary>
    /// <returns>头部信息的字符串表示。<para>The string representation of the header information.</para></returns>
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
    /// 序列化为文本格式，用于保存和恢复。
    /// <para>Serializes to text format for saving and restoring.</para>
    /// </summary>
    /// <returns>序列化后的头部文本内容。<para>The serialized header text content.</para></returns>
    public string ToHeaderFile()
    {
        var sb = new StringBuilder();
        // 序列化枚举名而非强转整数，确保 ParseHeaderFile 可校验往返闭合
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
    /// 从文本格式解析头部信息。
    /// <para>Parses header information from text format.</para>
    /// </summary>
    /// <param name="content">头部文本内容。<para>The header text content.</para></param>
    /// <returns>解析后的 <see cref="BootImageHeader"/> 实例；如果解析失败则为 null。
    /// <para>The parsed <see cref="BootImageHeader"/> instance, or null if parsing fails.</para></returns>
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
                        // 用 Enum.Parse 校验枚举名，避免未知整数强转产生非法枚举值
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