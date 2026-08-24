using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Boot 镜像格式检测和统一解析工具。
/// <para>
/// 提供自动检测 boot 镜像格式并统一解析的接口。</para>
/// <para>
/// Provides automatic detection and unified parsing of boot image formats.</para>
/// </summary>
public static class BootImageDetector
{
    /// <summary>
    /// Boot 镜像格式类型。
    /// <para>Boot image format type.</para>
    /// </summary>
    public enum ImageFormat
    {
        /// <summary>
        /// 未知格式。
        /// <para>Unknown format.</para>
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// 标准 AOSP Boot 镜像。
        /// <para>Standard AOSP Boot Image.</para>
        /// </summary>
        AospBoot = 1,

        /// <summary>
        /// Android Vendor Boot 镜像。
        /// <para>Android Vendor Boot Image.</para>
        /// </summary>
        VendorBoot = 2,

        /// <summary>
        /// MTK Boot 镜像。
        /// <para>MTK Boot Image.</para>
        /// </summary>
        MtkBoot = 3,

        /// <summary>
        /// Samsung PXA Boot 镜像。
        /// <para>Samsung PXA Boot Image.</para>
        /// </summary>
        SamsungPxa = 4,

        /// <summary>
        /// DHTB Boot 镜像。
        /// <para>DHTB Boot Image.</para>
        /// </summary>
        DhtbBoot = 5,

        /// <summary>
        /// ChromeOS 镜像。
        /// <para>ChromeOS Image.</para>
        /// </summary>
        ChromeOs = 6,

        /// <summary>
        /// TEGRA Blob。
        /// <para>TEGRA Blob.</para>
        /// </summary>
        TegraBlob = 7,

        /// <summary>
        /// Payload 分区镜像。
        /// <para>Payload Partition Image.</para>
        /// </summary>
        Payload = 8
    }

    /// <summary>
    /// 格式检测结果。
    /// <para>Format detection result.</para>
    /// </summary>
    public class DetectionResult
    {
        /// <summary>
        /// 检测到的格式。
        /// <para>The detected format.</para>
        /// </summary>
        public ImageFormat Format { get; set; }

        /// <summary>
        /// 文件路径（如果适用）。
        /// <para>The file path (if applicable).</para>
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// 格式描述。
        /// <para>The format description.</para>
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 置信度（0-100）。
        /// <para>Confidence (0-100).</para>
        /// </summary>
        public int Confidence { get; set; }

        /// <summary>
        /// 额外信息。
        /// <para>Additional information.</para>
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = [];
    }

    /// <summary>
    /// 统一解析结果。
    /// <para>Unified parsing result.</para>
    /// </summary>
    public class UnifiedResult
    {
        /// <summary>
        /// 是否成功。
        /// <para>Whether successful.</para>
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 检测到的格式。
        /// <para>The detected format.</para>
        /// </summary>
        public ImageFormat Format { get; set; }

        /// <summary>
        /// 内核数据。
        /// <para>The kernel data.</para>
        /// </summary>
        public byte[] Kernel { get; set; } = [];

        /// <summary>
        /// Ramdisk 数据。
        /// <para>The ramdisk data.</para>
        /// </summary>
        public byte[] Ramdisk { get; set; } = [];

        /// <summary>
        /// 第二阶段数据。
        /// <para>The second stage data.</para>
        /// </summary>
        public byte[] Second { get; set; } = [];

        /// <summary>
        /// DTB 数据。
        /// <para>The DTB data.</para>
        /// </summary>
        public byte[] Dtb { get; set; } = [];

        /// <summary>
        /// 头部信息。
        /// <para>The header information.</para>
        /// </summary>
        public BootImageHeader? Header { get; set; }

        /// <summary>
        /// 命令行。
        /// <para>The command line.</para>
        /// </summary>
        public string CommandLine { get; set; } = string.Empty;

        /// <summary>
        /// 内核压缩类型。
        /// <para>The kernel compression type.</para>
        /// </summary>
        public CompressionType KernelCompression { get; set; }

        /// <summary>
        /// 错误消息。
        /// <para>Error message.</para>
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 检测数据格式。
    /// <para>Detects the data format.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to check.</para></param>
    /// <returns>检测结果。<para>The detection result.</para></returns>
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
    /// 检测文件格式。
    /// <para>Detects the file format.</para>
    /// </summary>
    /// <param name="filePath">文件路径。<para>The file path.</para></param>
    /// <returns>检测结果。<para>The detection result.</para></returns>
    public static DetectionResult DetectFile(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        var result = Detect(data);
        result.FilePath = filePath;
        return result;
    }

    /// <summary>
    /// 统一解析 boot 镜像。
    /// <para>Unified parsing of boot image.</para>
    /// </summary>
    /// <param name="data">镜像数据。<para>The image data.</para></param>
    /// <returns>解析结果。<para>The parsing result.</para></returns>
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
                    break;
                case ImageFormat.Payload:
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
    /// 统一解析 boot 镜像文件。
    /// <para>Unified parsing of boot image file.</para>
    /// </summary>
    /// <param name="filePath">文件路径。<para>The file path.</para></param>
    /// <returns>解析结果。<para>The parsing result.</para></returns>
    public static UnifiedResult ParseFile(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        return Parse(data);
    }

    /// <summary>
    /// 获取所有支持的格式。
    /// <para>Gets all supported formats.</para>
    /// </summary>
    /// <returns>格式列表。<para>The format list.</para></returns>
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
            "Payload.bin"
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

    private static bool IsPayload(byte[] data) => data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 0x5041594C;

    /// <summary>
    /// Samsung PXA 使用标准 ANDROID! 魔数，但头部偏移 40 处存放 extra 段大小（通常大于 4）。
    /// <para>Samsung PXA uses the standard ANDROID! magic, but offset 40 holds the extra
    /// section size (typically &gt; 4) instead of a header version.</para>
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
}