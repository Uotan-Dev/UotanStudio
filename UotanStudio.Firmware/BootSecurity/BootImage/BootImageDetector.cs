using System.Text;
using UotanStudio.Firmware.BootSecurity.BootImage.Formats;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;

namespace UotanStudio.Firmware.BootSecurity.BootImage;

/// <summary>
/// Identifies boot-related firmware images from leading signature bytes and parses recognized
/// containers into one unified result model.
/// <para>通过起始签名字节识别 boot 相关固件镜像，并将可解析的容器归一化为统一结果模型。</para>
/// </summary>
public static class BootImageDetector
{
    /// <summary>
    /// Image families recognized by the signature probes in this detector.
    /// <para>本检测器的签名探测所能识别的镜像家族。</para>
    /// </summary>
    public enum ImageFormat
    {
        /// <summary>
        /// No signature matched; null or shorter-than-8-byte input is also reported as unknown.
        /// <para>未匹配任何签名；空数据或不足 8 字节的输入同样报告为未知。</para>
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Android generic boot image whose first 8 bytes are the ASCII magic ANDROID!.
        /// <para>前 8 字节为 ASCII 魔数 ANDROID! 的 Android 通用 boot 镜像。</para>
        /// </summary>
        AospBoot = 1,

        /// <summary>
        /// Vendor boot image whose first 8 bytes are the ASCII magic VENDORBOOT.
        /// <para>前 8 字节为 ASCII 魔数 VENDORBOOT 的 vendor boot 镜像。</para>
        /// </summary>
        VendorBoot = 2,

        /// <summary>
        /// MediaTek boot image identified by the little-endian word 0x58881688 at offset 0.
        /// <para>偏移 0 处小端字为 0x58881688 的联发科 boot 镜像。</para>
        /// </summary>
        MtkBoot = 3,

        /// <summary>
        /// Samsung boot image carrying the ANDROID! magic whose offset-40 little-endian word is
        /// greater than 4; probed before the plain AOSP check.
        /// <para>带 ANDROID! 魔数且偏移 40 处小端字大于 4 的三星镜像；探测先于普通 AOSP 判定。</para>
        /// </summary>
        SamsungPxa = 4,

        /// <summary>
        /// DHTB image identified by the little-endian word 0x42484444 at offset 0.
        /// <para>偏移 0 处小端字为 0x42484444 的 DHTB 镜像。</para>
        /// </summary>
        DhtbBoot = 5,

        /// <summary>
        /// ChromeOS image whose first 8 bytes are the ASCII magic CHROMEOS; probed first.
        /// <para>前 8 字节为 ASCII 魔数 CHROMEOS 的 ChromeOS 镜像；最先探测。</para>
        /// </summary>
        ChromeOs = 6,

        /// <summary>
        /// NVIDIA Tegra blob whose first 4 bytes are the ASCII magic NVDA.
        /// <para>前 4 字节为 ASCII 魔数 NVDA 的 NVIDIA Tegra blob。</para>
        /// </summary>
        TegraBlob = 7,

        /// <summary>
        /// Android OTA payload whose first 4 bytes are the ASCII magic CrAU.
        /// <para>前 4 字节为 ASCII 魔数 CrAU 的 Android OTA payload。</para>
        /// </summary>
        Payload = 8,

        /// <summary>
        /// ARM Linux compressed kernel image accepted by the zImage signature probe.
        /// <para>通过 zImage 签名探测的 ARM Linux 压缩内核镜像。</para>
        /// </summary>
        ZImage = 9
    }

    /// <summary>
    /// Outcome of a signature probe over raw bytes or a file prefix.
    /// <para>对原始字节或文件前缀进行签名探测的结果。</para>
    /// </summary>
    public class DetectionResult
    {
        /// <summary>
        /// Matched image family; <see cref="ImageFormat.Unknown"/> when no signature matched.
        /// <para>匹配到的镜像家族；未匹配时为 <see cref="ImageFormat.Unknown"/>。</para>
        /// </summary>
        /// <value>The detected family. <para>检测到的镜像家族。</para></value>
        public ImageFormat Format { get; set; }

        /// <summary>
        /// Path stamped by <see cref="DetectFile"/>; null for results from in-memory detection.
        /// <para>由 <see cref="DetectFile"/> 写入的文件路径；内存检测结果为 null。</para>
        /// </summary>
        /// <value>The source file path, or null. <para>源文件路径，或 null。</para></value>
        public string? FilePath { get; set; }

        /// <summary>
        /// Human-readable English label for the outcome, such as <c>Data too small</c> or
        /// <c>Unknown format</c>.
        /// <para>描述结果的可读英文标签，例如 <c>Data too small</c> 或 <c>Unknown format</c>。</para>
        /// </summary>
        /// <value>The outcome label. <para>结果标签文本。</para></value>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Confidence on a 0-to-100 scale; a matched signature reports 100 and every miss reports 0.
        /// <para>0 到 100 的置信度；命中签名一律为 100，未命中为 0。</para>
        /// </summary>
        /// <value>The confidence score. <para>置信度分值。</para></value>
        public int Confidence { get; set; }

        /// <summary>
        /// Annotations recorded during detection: a <c>type</c> key for every outcome, plus a
        /// <c>version</c> key for vendor boot images and for AOSP images of at least 44 bytes.
        /// <para>检测期间记录的注释：所有结果均含 <c>type</c> 键；vendor boot 以及
        /// 不少于 44 字节的 AOSP 镜像还含 <c>version</c> 键。</para>
        /// </summary>
        /// <value>The detection annotations; empty before any probe runs. <para>检测注释；未探测时为空。</para></value>
        public Dictionary<string, string> Metadata { get; set; } = [];
    }

    /// <summary>
    /// Payload and header content extracted by <see cref="Parse"/>, normalized across boot image
    /// families.
    /// <para><see cref="Parse"/> 提取的载荷与头部内容，在各 boot 镜像家族间归一化。</para>
    /// </summary>
    public class UnifiedResult
    {
        /// <summary>
        /// True when the dispatch finished without a parse error; formats that are detected but not
        /// parsed (Unknown, ChromeOS, Payload) also succeed with empty sections.
        /// <para>分发解析未出错时为 true；仅识别不解析的格式（Unknown、ChromeOS、Payload）
        /// 同样成功，但各段为空。</para>
        /// </summary>
        /// <value>True on a clean dispatch. <para>分发无错误时为 true。</para></value>
        public bool Success { get; set; }

        /// <summary>
        /// Image family assigned by detection before parsing.
        /// <para>解析前由检测赋值的镜像家族。</para>
        /// </summary>
        /// <value>The detected family. <para>检测到的镜像家族。</para></value>
        public ImageFormat Format { get; set; }

        /// <summary>
        /// Kernel section bytes; for zImage this is the compressed payload, and the array is empty
        /// when the format contributes no kernel.
        /// <para>内核段字节；zImage 时为压缩载荷，格式未提供内核时为空数组。</para>
        /// </summary>
        /// <value>The kernel bytes, possibly empty. <para>内核字节，可能为空。</para></value>
        public byte[] Kernel { get; set; } = [];

        /// <summary>
        /// Ramdisk section as produced by the active parser; empty when the format contributes none.
        /// <para>由当前解析器产出的 ramdisk 段；格式未提供时为空数组。</para>
        /// </summary>
        /// <value>The ramdisk bytes, possibly empty. <para>ramdisk 字节，可能为空。</para></value>
        public byte[] Ramdisk { get; set; } = [];

        /// <summary>
        /// AOSP secondary section carried by AOSP, MTK, Samsung and DHTB images; empty otherwise.
        /// <para>AOSP、MTK、三星与 DHTB 镜像携带的 secondary 段；其余格式为空数组。</para>
        /// </summary>
        /// <value>The secondary bytes, possibly empty. <para>secondary 段字节，可能为空。</para></value>
        public byte[] Second { get; set; } = [];

        /// <summary>
        /// Device tree blob extracted from AOSP, vendor boot, MTK and Samsung images; empty
        /// otherwise.
        /// <para>从 AOSP、vendor boot、MTK、三星镜像提取的设备树 blob；其余格式为空数组。</para>
        /// </summary>
        /// <value>The DTB bytes, possibly empty. <para>DTB 字节，可能为空。</para></value>
        public byte[] Dtb { get; set; } = [];

        /// <summary>
        /// Parsed boot image header; only AOSP parsing populates it, so it stays null for every
        /// other format.
        /// <para>已解析的 boot 镜像头部；仅 AOSP 解析会填充，其余格式保持 null。</para>
        /// </summary>
        /// <value>The header, or null. <para>头部，或 null。</para></value>
        public BootImageHeader? Header { get; set; }

        /// <summary>
        /// Kernel command line copied from the AOSP header; empty string for other formats.
        /// <para>复制自 AOSP 头部的内核命令行；其余格式为空字符串。</para>
        /// </summary>
        /// <value>The command line text. <para>命令行文本。</para></value>
        public string CommandLine { get; set; } = string.Empty;

        /// <summary>
        /// Kernel compression reported by AOSP, Tegra and zImage parsing; other formats keep the
        /// default value.
        /// <para>AOSP、Tegra、zImage 解析上报的内核压缩方式；其余格式保持默认值。</para>
        /// </summary>
        /// <value>The inferred kernel compression. <para>推断出的内核压缩方式。</para></value>
        public CompressionType KernelCompression { get; set; }

        /// <summary>
        /// Message of the exception caught during parsing, or null when parsing succeeded.
        /// <para>解析期间被捕获异常的消息；成功时为 null。</para>
        /// </summary>
        /// <value>The failure message, or null. <para>失败消息，或 null。</para></value>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Detects the data format.
    /// <para>检测数据格式。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate image bytes; null or fewer than 8 bytes is reported as unknown with a
    /// <c>Data too small</c> description.
    /// <para>待检测的镜像字节；空数据或不足 8 字节按未知返回，并带 <c>Data too small</c> 描述。</para>
    /// </param>
    /// <returns>
    /// Detection outcome with format, confidence, description and metadata populated.
    /// <para>已填充格式、置信度、描述与元数据的检测结果。</para>
    /// </returns>
    public static DetectionResult Detect(byte[] data)
    {
        var result = new DetectionResult
        {
            Format = ImageFormat.Unknown,
            Confidence = 0
        };

        if (data == null || data.Length < 8)
        {
            result.Description = "Data too small";
            return result;
        }

        if (IsChromeOs(data))
        {
            result.Format = ImageFormat.ChromeOs;
            result.Confidence = 100;
            result.Description = "ChromeOS Boot Image";
            result.Metadata["type"] = "chromeos";
            return result;
        }

        if (IsVendorBoot(data))
        {
            result.Format = ImageFormat.VendorBoot;
            result.Confidence = 100;
            result.Description = "Android Vendor Boot Image";
            result.Metadata["type"] = "vendor_boot";
            result.Metadata["version"] = data[8].ToString();
            return result;
        }

        if (IsMtkFormat(data))
        {
            result.Format = ImageFormat.MtkBoot;
            result.Confidence = 100;
            result.Description = "MTK Boot Image";
            result.Metadata["type"] = "mtk";
            return result;
        }

        if (IsDhtbFormat(data))
        {
            result.Format = ImageFormat.DhtbBoot;
            result.Confidence = 100;
            result.Description = "DHTB Boot Image";
            result.Metadata["type"] = "dhtb";
            return result;
        }

        if (IsTegraBlob(data))
        {
            result.Format = ImageFormat.TegraBlob;
            result.Confidence = 100;
            result.Description = "NVIDIA TEGRA Blob";
            result.Metadata["type"] = "tegra";
            return result;
        }

        if (IsPayload(data))
        {
            result.Format = ImageFormat.Payload;
            result.Confidence = 100;
            result.Description = "Android OTA Payload";
            result.Metadata["type"] = "payload";
            return result;
        }

        if (ZImageTool.IsZImage(data))
        {
            result.Format = ImageFormat.ZImage;
            result.Confidence = 100;
            result.Description = "ARM Linux zImage";
            result.Metadata["type"] = "zimage";
            return result;
        }

        if (IsSamsungPxa(data))
        {
            result.Format = ImageFormat.SamsungPxa;
            result.Confidence = 100;
            result.Description = "Samsung PXA Boot Image";
            result.Metadata["type"] = "samsung_pxa";
            return result;
        }

        if (IsAospBoot(data))
        {
            result.Format = ImageFormat.AospBoot;
            result.Confidence = 100;
            result.Description = "Standard AOSP Boot Image";

            if (data.Length >= 44)
            {
                var headerVersion = BitConverter.ToUInt32(data, 40);
                result.Metadata["type"] = "aosp";
                result.Metadata["version"] = headerVersion.ToString();
            }

            return result;
        }

        result.Description = "Unknown format";
        result.Metadata["type"] = "unknown";

        return result;
    }

    /// <summary>
    /// Detects the file format.
    /// <para>检测文件格式。</para>
    /// </summary>
    /// <param name="filePath">
    /// Existing file to probe; only the leading bytes, at most the first 4096, are read.
    /// <para>待探测的现有文件；仅读取起始字节，至多前 4096 个。</para>
    /// </param>
    /// <returns>
    /// Detection outcome whose <see cref="DetectionResult.FilePath"/> property is set to
    /// <paramref name="filePath"/>.
    /// <para>检测结果，其 <see cref="DetectionResult.FilePath"/> 属性被设置为 <paramref name="filePath"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is empty. <para><paramref name="filePath"/> 为空。</para></exception>
    /// <exception cref="FileNotFoundException">The file or its directory does not exist. <para>文件或所在目录不存在。</para></exception>
    /// <exception cref="UnauthorizedAccessException">The caller lacks read permission. <para>调用方没有读取权限。</para></exception>
    /// <exception cref="IOException">The file could not be opened or read. <para>文件无法打开或读取。</para></exception>
    public static DetectionResult DetectFile(string filePath)
    {


        var data = ReadDetectionPrefix(filePath);
        var result = Detect(data);
        result.FilePath = filePath;
        return result;
    }

    /// <summary>
    /// Unified parsing of boot image.
    /// <para>统一解析 boot 镜像。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate image bytes; the format dispatch follows <see cref="Detect"/>.
    /// <para>待解析的镜像字节；格式分发依据 <see cref="Detect"/>。</para>
    /// </param>
    /// <returns>
    /// Result carrying the detected format. AOSP, vendor boot, MTK, Samsung, DHTB, Tegra and zImage
    /// sections are filled when parsing succeeds; Unknown, ChromeOS and Payload report success with
    /// empty sections, and a parse failure leaves <see cref="UnifiedResult.Success"/> false with the
    /// exception message in <see cref="UnifiedResult.ErrorMessage"/>.
    /// <para>返回携带检测格式的结果。解析成功时填充 AOSP、vendor boot、MTK、三星、DHTB、Tegra
    /// 与 zImage 各段；Unknown、ChromeOS、Payload 视为成功但各段为空；解析失败时
    /// <see cref="UnifiedResult.Success"/> 为 false，异常消息写入 <see cref="UnifiedResult.ErrorMessage"/>。</para>
    /// </returns>
    /// <example>
    /// <code>
    /// var result = BootImageDetector.Parse(File.ReadAllBytes("boot.img"));
    /// if (result.Success &amp;&amp; result.Format == BootImageDetector.ImageFormat.AospBoot)
    /// {
    ///     File.WriteAllBytes("kernel", result.Kernel);
    /// }
    /// </code>
    /// </example>
    public static UnifiedResult Parse(byte[] data)
    {
        var result = new UnifiedResult { Success = false };
        var detection = Detect(data);

        result.Format = detection.Format;

        try
        {
            switch (detection.Format)
            {
                case ImageFormat.AospBoot:
                    ParseAospBoot(data, result);
                    break;

                case ImageFormat.VendorBoot:
                    ParseVendorBoot(data, result);
                    break;

                case ImageFormat.MtkBoot:
                    ParseMtkBoot(data, result);
                    break;

                case ImageFormat.SamsungPxa:
                    ParseSamsungPxa(data, result);
                    break;

                case ImageFormat.DhtbBoot:
                    ParseDhtbBoot(data, result);
                    break;
                case ImageFormat.Unknown:
                    break;
                case ImageFormat.ChromeOs:
                    break;
                case ImageFormat.TegraBlob:
                    ParseTegraBlob(data, result);
                    break;
                case ImageFormat.Payload:
                    break;
                case ImageFormat.ZImage:
                    ParseZImage(data, result);
                    break;
                default:
                    result.ErrorMessage = $"Format {detection.Format} is not parsed by the simplified detector.";
                    return result;
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Unified parsing of boot image file.
    /// <para>统一解析 boot 镜像文件。</para>
    /// </summary>
    /// <param name="filePath">
    /// File to read completely into memory before parsing; unlike <see cref="DetectFile"/> the whole
    /// content is loaded.
    /// <para>解析前完整读入内存的文件；与 <see cref="DetectFile"/> 不同，会加载全部内容。</para>
    /// </param>
    /// <returns>
    /// Unified result for the file content, following the same success and error semantics as
    /// <see cref="Parse"/>.
    /// <para>针对文件内容的统一结果，成功与错误语义与 <see cref="Parse"/> 一致。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is empty. <para><paramref name="filePath"/> 为空。</para></exception>
    /// <exception cref="NotSupportedException">The path is in an unsupported form. <para>路径形式不受支持。</para></exception>
    /// <exception cref="FileNotFoundException">The file or its directory does not exist. <para>文件或所在目录不存在。</para></exception>
    /// <exception cref="UnauthorizedAccessException">The caller lacks read permission. <para>调用方没有读取权限。</para></exception>
    /// <exception cref="IOException">The file could not be opened or read. <para>文件无法打开或读取。</para></exception>
    public static UnifiedResult ParseFile(string filePath)
    {


        var data = File.ReadAllBytes(filePath);
        return Parse(data);
    }

    /// <summary>
    /// Gets all supported formats.
    /// <para>获取所有支持的格式。</para>
    /// </summary>
    /// <returns>
    /// Display names of the image families the detector recognizes, in a new mutable list per call.
    /// <para>检测器可识别镜像家族的展示名称；每次调用返回新的可修改列表。</para>
    /// </returns>
    public static List<string> GetSupportedFormats()
    {
        return
        [
            "AOSP Boot (v0-v4)",
            "Vendor Boot (v3-v4)",
            "MTK Boot",
            "Samsung PXA",
            "DHTB",
            "ChromeOS",
            "TEGRA Blob",
            "Payload.bin",
            "ARM Linux zImage"
        ];
    }

    private static bool IsChromeOs(byte[] data)
    {
        return data.Length >= 64 && data[0] == 0x43 && data[1] == 0x48 && data[2] == 0x52 && data[3] == 0x4f &&
               data[4] == 0x4d && data[5] == 0x45 && data[6] == 0x4f && data[7] == 0x53;
    }

    private static bool IsVendorBoot(byte[] data)
    {
        return data.Length >= 8 && data[0] == 0x56 && data[1] == 0x4e && data[2] == 0x44 && data[3] == 0x52 &&
               data[4] == 0x42 && data[5] == 0x4f && data[6] == 0x4f && data[7] == 0x54;
    }

    private static bool IsMtkFormat(byte[] data) => data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 0x58881688;

    private static bool IsDhtbFormat(byte[] data) => data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 0x42484444;

    private static bool IsTegraBlob(byte[] data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        var magic = Encoding.ASCII.GetString(data, 0, 4);
        return magic == "NVDA";
    }

    private static bool IsPayload(byte[] data) => data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 0x55417243u;

    /// <summary>
    /// Samsung PXA uses the standard ANDROID! magic, but offset 40 holds the extra section size (typically &gt; 4) instead of a header version.
    /// <para>Samsung PXA 使用标准 ANDROID! 魔数，但头部偏移 40 处存放 extra 段大小（通常大于 4）。</para>
    /// </summary>
    private static bool IsSamsungPxa(byte[] data)
    {
        if (data.Length < 44)
        {
            return false;
        }

        var magic = Encoding.ASCII.GetString(data, 0, 8);
        if (magic != "ANDROID!")
        {
            return false;
        }

        var headerOrExtra = BitConverter.ToUInt32(data, 40);
        return headerOrExtra > 4;
    }

    private static bool IsAospBoot(byte[] data)
    {
        if (data.Length < 8)
        {
            return false;
        }

        var magic = Encoding.ASCII.GetString(data, 0, 8);
        return magic == "ANDROID!";
    }

    private static void ParseAospBoot(byte[] data, UnifiedResult result)
    {
        using var parser = new BootImageParser();
        parser.Parse(data);

        result.Kernel = parser.Kernel;
        result.Ramdisk = parser.Ramdisk;
        result.Second = parser.Second;
        result.Dtb = parser.Dtb;
        result.Header = parser.Header;
        result.CommandLine = parser.Header.CommandLine;
        result.KernelCompression = parser.Header.KernelCompression;
    }

    private static void ParseVendorBoot(byte[] data, UnifiedResult result)
    {
        using var parser = new VendorBootParser();
        parser.Parse(data);

        result.Kernel = [];
        result.Ramdisk = parser.VendorRamdisk;
        result.Second = [];
        result.Dtb = parser.Dtb;
    }

    private static void ParseMtkBoot(byte[] data, UnifiedResult result)
    {
        using var parser = new MtkBootParser();
        parser.Parse(data);

        result.Kernel = parser.Kernel;
        result.Ramdisk = parser.Ramdisk;
        result.Second = parser.Second;
        result.Dtb = parser.Dtb;
    }

    private static void ParseSamsungPxa(byte[] data, UnifiedResult result)
    {
        using var parser = new SamsungBootParser();
        parser.Parse(data);

        result.Kernel = parser.Kernel;
        result.Ramdisk = parser.Ramdisk;
        result.Second = parser.Second;
        result.Dtb = parser.Dtb;
    }

    private static void ParseDhtbBoot(byte[] data, UnifiedResult result)
    {
        var parseResult = DhtbTool.Parse(data);
        if (!parseResult.IsValid)
        {
            throw new ImageParseException("Failed to parse DHTB image");
        }

        result.Kernel = parseResult.Kernel;
        result.Ramdisk = parseResult.Ramdisk;
        result.Second = parseResult.Second;
    }

    private static void ParseTegraBlob(byte[] data, UnifiedResult result)
    {
        var parsed = TegraBlobTool.Parse(data);
        if (!parsed.IsValid)
        {
            throw new ImageParseException(parsed.ErrorMessage ?? "Failed to parse Tegra blob");
        }

        result.Kernel = parsed.Kernel;
        result.Ramdisk = parsed.Ramdisk;
        result.KernelCompression = parsed.Header!.CompressionType switch
        {
            1 => CompressionType.Gzip,
            2 => CompressionType.Lz4,
            3 => CompressionType.Lz4Legacy,
            4 => CompressionType.Lzma,
            _ => CompressionType.None
        };
    }

    private static void ParseZImage(byte[] data, UnifiedResult result)
    {
        var parsed = ZImageTool.Parse(data);
        if (!parsed.IsValid)
        {
            throw new ImageParseException(parsed.ErrorMessage ?? "Failed to parse zImage");
        }

        result.Kernel = parsed.CompressedPayload;
        result.KernelCompression = parsed.CompressionType;
    }

    private const int DetectionPrefixBytes = 4096;

    /// <summary>
    /// Bounded read of the file head prefix, truncated to the actual length for smaller files, so detection semantics stay identical to a full read.
    /// <para>有界读取文件头部前缀；文件不足前缀长度时按实际长度截断，与整读检测语义一致。</para>
    /// </summary>
    private static byte[] ReadDetectionPrefix(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.SequentialScan);
        var length = (int)Math.Min(fs.Length, DetectionPrefixBytes);
        var buffer = new byte[length];
        fs.ReadExactly(buffer);
        return buffer;
    }
}