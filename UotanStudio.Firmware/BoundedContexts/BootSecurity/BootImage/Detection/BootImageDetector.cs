using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Formats;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Detection;

public static class BootImageDetector
{
    public enum ImageFormat
    {
        Unknown = 0,

        AospBoot = 1,

        VendorBoot = 2,

        MtkBoot = 3,

        SamsungPxa = 4,

        DhtbBoot = 5,

        ChromeOs = 6,

        TegraBlob = 7,

        Payload = 8,

        ZImage = 9
    }

    public class DetectionResult
    {
        public ImageFormat Format { get; set; }

        public string? FilePath { get; set; }

        public string Description { get; set; } = string.Empty;

        public int Confidence { get; set; }

        public Dictionary<string, string> Metadata { get; set; } = [];
    }

    public class UnifiedResult
    {
        public bool Success { get; set; }

        public ImageFormat Format { get; set; }

        public byte[] Kernel { get; set; } = [];

        public byte[] Ramdisk { get; set; } = [];

        public byte[] Second { get; set; } = [];

        public byte[] Dtb { get; set; } = [];

        public BootImageHeader? Header { get; set; }

        public string CommandLine { get; set; } = string.Empty;

        public CompressionType KernelCompression { get; set; }

        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Detects the data format.
    /// <para>检测数据格式。</para>
    /// </summary>
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
    public static UnifiedResult ParseFile(string filePath)
    {


        var data = File.ReadAllBytes(filePath);
        return Parse(data);
    }

    /// <summary>
    /// Gets all supported formats.
    /// <para>获取所有支持的格式。</para>
    /// </summary>
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