using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

public static class ChromeOsTool
{
    private const string ChromeOsMagic = "CHROMEOS";

    public enum ChromeOsImageType
    {
        Unknown,

        Kernel,

        Other
    }

    public class ChromeOsDetectionResult
    {
        public bool IsChromeOs { get; set; }

        public ChromeOsImageType ImageType { get; set; }

        public string Description { get; set; } = string.Empty;

        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Checks whether the data is a ChromeOS image.
    /// <para>检测数据是否为 ChromeOS 镜像。</para>
    /// </summary>
    public static bool IsChromeOs(byte[] data) => data != null && data.Length >= 64 && Encoding.ASCII.GetString(data, 0, 8) == ChromeOsMagic;

    /// <summary>
    /// Detects a ChromeOS image.
    /// <para>检测 ChromeOS 镜像。</para>
    /// </summary>
    public static ChromeOsDetectionResult Detect(byte[] data)
    {
        return !IsChromeOs(data)
            ? new ChromeOsDetectionResult
            {
                IsChromeOs = false,
                ImageType = ChromeOsImageType.Unknown,
                Description = "Not a ChromeOS image"
            }
            : new ChromeOsDetectionResult
            {
                IsChromeOs = true,
                ImageType = ChromeOsImageType.Kernel,
                Description = "ChromeOS Kernel Image"
            };
    }

    /// <summary>
    /// Extracts the kernel data.
    /// <para>提取内核数据。</para>
    /// </summary>
    public static byte[]? ExtractKernel(byte[] data)
    {
        throw new NotSupportedException(
            "ChromeOS kernel extraction is not inlined in this simplified port.");
    }

    /// <summary>
    /// Verifies the signature.
    /// <para>验证签名。</para>
    /// </summary>
    public static bool VerifySignature(byte[] data)
    {
        throw new NotSupportedException(
            "ChromeOS signature verification is not inlined in this simplified port.");
    }
}