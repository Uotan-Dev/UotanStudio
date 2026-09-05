using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// ChromeOS 引导镜像工具。
/// <para>
/// ChromeOS boot image tool.</para>
/// <para>
/// 精简移植：仅实现格式检测；内核提取与签名处理未内联。</para>
/// <para>
/// Simplified port: only format detection is implemented; kernel extraction and
/// signature handling are not inlined.</para>
/// </summary>
public static class ChromeOsTool
{
    private const string ChromeOsMagic = "CHROMEOS";

    /// <summary>
    /// ChromeOS 镜像类型。
    /// <para>ChromeOS image type.</para>
    /// </summary>
    public enum ChromeOsImageType
    {
        /// <summary>
        /// 未知。
        /// <para>Unknown.</para>
        /// </summary>
        Unknown,

        /// <summary>
        /// 内核镜像。
        /// <para>Kernel image.</para>
        /// </summary>
        Kernel,

        /// <summary>
        /// 其他。
        /// <para>Other.</para>
        /// </summary>
        Other
    }

    /// <summary>
    /// ChromeOS 检测结果。
    /// <para>ChromeOS detection result.</para>
    /// </summary>
    public class ChromeOsDetectionResult
    {
        /// <summary>
        /// 是否为 ChromeOS 镜像。
        /// <para>Whether it is a ChromeOS image.</para>
        /// </summary>
        public bool IsChromeOs { get; set; }

        /// <summary>
        /// 镜像类型。
        /// <para>The image type.</para>
        /// </summary>
        public ChromeOsImageType ImageType { get; set; }

        /// <summary>
        /// 描述。
        /// <para>The description.</para>
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 错误消息。
        /// <para>Error message.</para>
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 检测数据是否为 ChromeOS 镜像。
    /// <para>Checks whether the data is a ChromeOS image.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to check.</para></param>
    /// <returns>如果是 ChromeOS 镜像则为 true。<para>True if a ChromeOS image.</para></returns>
    public static bool IsChromeOs(byte[] data) => data != null && data.Length >= 64 && Encoding.ASCII.GetString(data, 0, 8) == ChromeOsMagic;

    /// <summary>
    /// 检测 ChromeOS 镜像。
    /// <para>Detects a ChromeOS image.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to check.</para></param>
    /// <returns>检测结果。<para>The detection result.</para></returns>
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
    /// 提取内核数据。
    /// <para>Extracts the kernel data.</para>
    /// </summary>
    /// <param name="data">镜像数据。<para>The image data.</para></param>
    /// <returns>内核数据。<para>The kernel data.</para></returns>
    public static byte[]? ExtractKernel(byte[] data)
    {
        throw new NotSupportedException(
            "ChromeOS kernel extraction is not inlined in this simplified port.");
    }

    /// <summary>
    /// 验证签名。
    /// <para>Verifies the signature.</para>
    /// </summary>
    /// <param name="data">镜像数据。<para>The image data.</para></param>
    /// <returns>签名是否有效。<para>Whether the signature is valid.</para></returns>
    public static bool VerifySignature(byte[] data)
    {
        throw new NotSupportedException(
            "ChromeOS signature verification is not inlined in this simplified port.");
    }
}