using System.Text;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Models;

/// <summary>
/// In-memory model of an Android boot image header: load addresses, section sizes and offsets,
/// name strings and version fields used when unpacking or repacking a boot image.
/// <para>Android boot image 头部的内存模型：加载地址、段大小与偏移、名称字符串和版本字段，用于解包或重打包 boot image。</para>
/// </summary>
public class BootImageHeader
{
    /// <summary>Header format version of the boot image.
    /// <para>boot image 的头部格式版本。</para></summary>
    /// <value>A <see cref="BootImageVersion"/> value; defaults to <see cref="BootImageVersion.V0"/>.
    /// <para>取值为 <see cref="BootImageVersion"/> 枚举成员；默认为 <see cref="BootImageVersion.V0"/>。</para></value>
    public BootImageVersion HeaderVersion { get; set; } = BootImageVersion.V0;

    /// <summary>Declared size of the header block in bytes.
    /// <para>头部块声明的字节大小。</para></summary>
    /// <value>Byte count; defaults to <c>1664</c>.
    /// <para>字节数；默认为 <c>1664</c>。</para></value>
    public uint HeaderSize { get; set; } = 1664;

    /// <summary>Physical load address for the kernel section.
    /// <para>内核段的物理加载地址。</para></summary>
    /// <value>64-bit target address; <c>0</c> when unset. <para>64 位目标地址；未设置时为 <c>0</c>。</para></value>
    public ulong KernelAddr { get; set; }

    /// <summary>Physical load address for the ramdisk section.
    /// <para>ramdisk 段的物理加载地址。</para></summary>
    /// <value>64-bit target address; <c>0</c> when unset. <para>64 位目标地址；未设置时为 <c>0</c>。</para></value>
    public ulong RamdiskAddr { get; set; }

    /// <summary>Physical load address for the second-stage bootloader section.
    /// <para>二级引导加载程序段的物理加载地址。</para></summary>
    /// <value>64-bit target address; <c>0</c> when unset. <para>64 位目标地址；未设置时为 <c>0</c>。</para></value>
    public ulong SecondAddr { get; set; }

    /// <summary>Address where the tagged board information is placed.
    /// <para>tag 板级信息块的放置地址。</para></summary>
    /// <value>64-bit target address; <c>0</c> when unset. <para>64 位目标地址；未设置时为 <c>0</c>。</para></value>
    public ulong TagsAddr { get; set; }

    /// <summary>Page alignment size of the image in bytes.
    /// <para>镜像的页对齐大小（字节）。</para></summary>
    /// <value>Page size such as 2048 or 4096; each section starts at a page boundary.
    /// <para>例如 2048 或 4096 的页大小；每个段都从页边界开始。</para></value>
    public uint PageSize { get; set; }

    /// <summary>Stored size of the kernel section in bytes.
    /// <para>内核段在镜像中存储的字节大小。</para></summary>
    /// <value>Byte count; <c>0</c> means no kernel section. <para>字节数；<c>0</c> 表示没有内核段。</para></value>
    public uint KernelSize { get; set; }

    /// <summary>Stored size of the ramdisk section in bytes.
    /// <para>ramdisk 段在镜像中存储的字节大小。</para></summary>
    /// <value>Byte count; <c>0</c> means no ramdisk section. <para>字节数；<c>0</c> 表示没有 ramdisk 段。</para></value>
    public uint RamdiskSize { get; set; }

    /// <summary>Stored size of the second-stage bootloader section in bytes.
    /// <para>二级引导加载程序段在镜像中存储的字节大小。</para></summary>
    /// <value>Byte count; <c>0</c> means no second-stage section. <para>字节数；<c>0</c> 表示没有二级引导段。</para></value>
    public uint SecondSize { get; set; }

    /// <summary>Stored size of the DTB section in bytes.
    /// <para>DTB 段在镜像中存储的字节大小。</para></summary>
    /// <value>Byte count; <c>0</c> means no DTB section. <para>字节数；<c>0</c> 表示没有 DTB 段。</para></value>
    public uint DtbSize { get; set; }

    /// <summary>64-bit kernel load address recorded in the header.
    /// <para>头部记录的 64 位内核加载地址。</para></summary>
    /// <value>Target address used by layouts that carry an explicit kernel load address field; <c>0</c> when unset.
    /// <para>由带显式内核加载地址字段的布局使用的目标地址；未设置时为 <c>0</c>。</para></value>
    public ulong KernelLoadAddr { get; set; }

    /// <summary>Stored size of the recovery DTBO section in bytes.
    /// <para>recovery DTBO 段存储的字节大小。</para></summary>
    /// <value>Byte count; <c>0</c> means no recovery DTBO section. <para>字节数；<c>0</c> 表示没有 recovery DTBO 段。</para></value>
    public uint RecoveryDtboSize { get; set; }

    /// <summary>Byte offset of the recovery DTBO section within the image.
    /// <para>recovery DTBO 段在镜像内的字节偏移。</para></summary>
    /// <value>Offset counted from the image start; meaningful together with <see cref="RecoveryDtboSize"/>.
    /// <para>从镜像起始计算的偏移；与 <see cref="RecoveryDtboSize"/> 配合使用。</para></value>
    public ulong RecoveryDtboOffset { get; set; }

    /// <summary>Stored size of the vendor signature block in bytes.
    /// <para>厂商签名块存储的字节大小。</para></summary>
    /// <value>Byte count; <c>0</c> means no signature block. <para>字节数；<c>0</c> 表示没有签名块。</para></value>
    public uint SignatureSize { get; set; }

    /// <summary>Kernel command line carried by the header.
    /// <para>头部携带的内核命令行。</para></summary>
    /// <value>Raw command-line string; empty by default. <para>原始命令行字符串；默认为空。</para></value>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>Product name recorded in the header.
    /// <para>头部记录的产品名称。</para></summary>
    /// <value>Product string; empty by default. <para>产品字符串；默认为空。</para></value>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Image name recorded in the header.
    /// <para>头部记录的镜像名称。</para></summary>
    /// <value>Name string; empty by default. <para>名称字符串；默认为空。</para></value>
    public string ImageName { get; set; } = string.Empty;

    /// <summary>Compression format of the stored kernel section.
    /// <para>存储的内核段的压缩格式。</para></summary>
    /// <value>A <see cref="CompressionType"/> value; <see cref="CompressionType.None"/> means uncompressed or undetected.
    /// <para>取值为 <see cref="CompressionType"/> 枚举成员；<see cref="CompressionType.None"/> 表示未压缩或未检测。</para></value>
    public CompressionType KernelCompression { get; set; }

    /// <summary>Compression format of the stored ramdisk section.
    /// <para>存储的 ramdisk 段的压缩格式。</para></summary>
    /// <value>A <see cref="CompressionType"/> value; <see cref="CompressionType.None"/> means uncompressed or undetected.
    /// <para>取值为 <see cref="CompressionType"/> 枚举成员；<see cref="CompressionType.None"/> 表示未压缩或未检测。</para></value>
    public CompressionType RamdiskCompression { get; set; }

    /// <summary>Byte offset of the kernel section within the image.
    /// <para>内核段在镜像内的字节偏移。</para></summary>
    /// <value>Offset counted from the image start. <para>从镜像起始计算的偏移。</para></value>
    public uint KernelOffset { get; set; }

    /// <summary>Byte offset of the ramdisk section within the image.
    /// <para>ramdisk 段在镜像内的字节偏移。</para></summary>
    /// <value>Offset counted from the image start. <para>从镜像起始计算的偏移。</para></value>
    public uint RamdiskOffset { get; set; }

    /// <summary>Byte offset of the second-stage bootloader section within the image.
    /// <para>二级引导加载程序段在镜像内的字节偏移。</para></summary>
    /// <value>Offset counted from the image start. <para>从镜像起始计算的偏移。</para></value>
    public uint SecondOffset { get; set; }

    /// <summary>Byte offset of the tagged board information block within the image.
    /// <para>tag 板级信息块在镜像内的字节偏移。</para></summary>
    /// <value>Offset counted from the image start. <para>从镜像起始计算的偏移。</para></value>
    public uint TagsOffset { get; set; }

    /// <summary>Header identifier/checksum words embedded in the boot image header.
    /// <para>嵌入 boot image 头部的标识/校验字。</para></summary>
    /// <value>Always eight unsigned 32-bit words; freshly created headers hold all zeros.
    /// <para>固定为八个无符号 32 位字；新建头部全为 0。</para></value>
    public uint[] Ids { get; set; } = new uint[8];

    /// <summary>Verbatim header block captured by the version 3/4 parser, from the magic through the padded tail that
    /// ends at the first page-aligned section offset.
    /// <para>版本 3/4 解析路径按原样捕获的头部块：从魔数开始，直到首个页对齐区段偏移处结束的含填充尾部。</para></summary>
    /// <value><see langword="null"/> for hand-built headers, version 0–2 parses, and any version 3/4 parse whose aligned
    /// header block exceeds the image or the 64 KiB capture bound. When present, the packer rebuilds the version 3/4
    /// header from a copy of these bytes, overwriting only the fields it owns (section sizes, header size, OS version,
    /// header version, command line and the V4 signature-size field), which keeps unmodified unpack → repack output
    /// byte-identical, including opaque reserved, hash and padding regions.
    /// <para>手工构造的头部、版本 0–2 解析，以及版本 3/4 解析中对齐后头部块超出镜像长度或 64 KiB 捕获上限时为
    /// <see langword="null"/>。存在时，打包器以这些字节的副本重建版本 3/4 头部，仅覆写其自有字段（区段大小、头部大小、
    /// OS 版本、头部版本、命令行与 V4 签名大小字段），从而保证未修改镜像的解包 → 重打包输出逐字节一致，包括不透明的
    /// reserved、hash 与填充区域。</para></value>
    public byte[]? HeaderBlob { get; set; }

    /// <summary>Packed Android OS version field.
    /// <para>打包后的 Android OS 版本字段。</para></summary>
    /// <value>Major version in bits 25-31, minor in bits 18-24 and patch in bits 11-17; see <see cref="OsVersionString"/>.
    /// <para>主版本位于第 25-31 位，次版本位于第 18-24 位，补丁号位于第 11-17 位；参见 <see cref="OsVersionString"/>。</para></value>
    public uint OsVersion { get; set; }

    /// <summary>Human-readable OS version decoded from <see cref="OsVersion"/>.
    /// <para>由 <see cref="OsVersion"/> 解码出的可读 OS 版本。</para></summary>
    /// <value><c>major.minor.patch</c> text; empty when all three packed components are zero.
    /// <para><c>major.minor.patch</c> 文本；三个打包分量全为 0 时返回空字符串。</para></value>
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

    /// <summary>Architecture code recorded in the header.
    /// <para>头部记录的架构编码。</para></summary>
    /// <value>Raw numeric architecture identifier as stored by the source image.
    /// <para>按源镜像原样存储的数值型架构标识。</para></value>
    public uint Architecture { get; set; }

    /// <summary>
    /// Converts to a string format similar to the original header output.
    /// <para>转换为字符串格式，输出头部信息。</para>
    /// </summary>
    /// <returns>Line-separated summary of version, sizes and addresses; addresses use 16-digit hexadecimal.
    /// <para>按行分隔的版本、大小与地址摘要；地址采用 16 位十六进制表示。</para></returns>
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
    /// <returns>One <c>key=value</c> line per field; addresses and 64-bit offsets use <c>0x</c>-prefixed
    /// 16-digit hexadecimal, sizes and numeric fields use decimal.
    /// <para>每个字段一行 <c>key=value</c>；地址与 64 位偏移使用带 <c>0x</c> 前缀的 16 位十六进制，
    /// 大小与数值字段使用十进制。</para></returns>
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
    /// <param name="content">
    /// Whole header-file text using <c>key=value</c> lines; blank lines and lines starting with <c>#</c>
    /// are skipped, and unrecognized keys are ignored. Hexadecimal values may carry a <c>0x</c> prefix.
    /// <para>完整的头部文件文本，使用 <c>key=value</c> 行；跳过空行和以 <c>#</c> 开头的行，未识别的键被忽略；
    /// 十六进制值可以带 <c>0x</c> 前缀。</para></param>
    /// <returns>A header populated from the recognized keys, with defaults kept for absent ones;
    /// <see langword="null"/> when the text is unparseable (a bad value, an unknown version name,
    /// or <see langword="null"/> content). The method catches all parse failures and never throws.
    /// <para>按已识别键填充的头部，缺省键保留默认值；文本无法解析（坏值、未知版本名或 <see langword="null"/> 内容）
    /// 时返回 <see langword="null"/>。该方法捕获所有解析失败且不抛出异常。</para></returns>
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