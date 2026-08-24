using System.Buffers.Binary;
using UotanStudio.Firmware.Payload;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Payload 分区镜像工具。
/// <para>
/// Payload partition image tool.</para>
/// <para>
/// 提供 magic 检测，并复用 <c>UotanStudio.Firmware.Payload.PayloadParser</c> 完成镜像提取。</para>
/// <para>
/// Provides magic detection and reuses <c>UotanStudio.Firmware.Payload.PayloadParser</c> for image extraction.</para>
/// </summary>
public static class PayloadTool
{
    private const uint PayloadMagic = 0x43524155; // "CrAU"

    /// <summary>
    /// 检查数据是否为 Android OTA Payload 格式。
    /// <para>Checks whether the data is an Android OTA payload.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to check.</para></param>
    /// <returns>如果是 payload 则为 true。<para>True if a payload.</para></returns>
    public static bool IsPayload(byte[] data) => data != null && data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == PayloadMagic;

    /// <summary>
    /// 检查文件是否为 Android OTA Payload 格式。
    /// <para>Checks whether the file is an Android OTA payload.</para>
    /// </summary>
    /// <param name="path">文件路径。<para>The file path.</para></param>
    /// <returns>如果是 payload 则为 true。<para>True if a payload.</para></returns>
    public static bool IsPayload(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var header = new byte[4];
        using var fs = File.OpenRead(path);
        return fs.Read(header, 0, 4) >= 4 && BinaryPrimitives.ReadUInt32BigEndian(header) == PayloadMagic;
    }

    /// <summary>
    /// 从 payload 中提取指定镜像。
    /// <para>Extracts the named image from a payload.</para>
    /// </summary>
    /// <param name="data">payload 数据。<para>The payload data.</para></param>
    /// <param name="imageName">镜像名称。<para>The image name.</para></param>
    /// <returns>镜像数据；格式无效或不存在该分区时返回 null。<para>The image data, or null when the payload is invalid or the partition does not exist.</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> 为 <c>null</c>。<para><paramref name="data"/> is <c>null</c>.</para></exception>
    /// <exception cref="ArgumentException"><paramref name="imageName"/> 为空白。<para><paramref name="imageName"/> is blank.</para></exception>
    public static byte[]? ExtractImage(byte[] data, string imageName)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(imageName))
        {
            throw new ArgumentException("Image name cannot be empty or whitespace.", nameof(imageName));
        }

        if (data.Length == 0 || !IsPayload(data))
        {
            return null;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"uotan_payload_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(tempPath, data);
            return ExtractImage(tempPath, imageName);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                // 清理失败不影响已返回/抛出的结果。
                // Cleanup failure does not affect the returned or thrown result.
            }
        }
    }

    /// <summary>
    /// 从 payload 文件中提取指定镜像。
    /// <para>Extracts the named image from a payload file.</para>
    /// </summary>
    /// <param name="path">payload 文件路径。<para>The payload file path.</para></param>
    /// <param name="imageName">镜像名称。<para>The image name.</para></param>
    /// <returns>镜像数据；非 payload 或不存在该分区时返回 null。<para>The image data, or null when the file is not a payload or the partition does not exist.</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> 或 <paramref name="imageName"/> 为空白。<para><paramref name="path"/> or <paramref name="imageName"/> is blank.</para></exception>
    /// <exception cref="FileNotFoundException">payload 文件不存在。<para>The payload file does not exist.</para></exception>
    public static byte[]? ExtractImage(string path, string imageName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Payload path cannot be empty or whitespace.", nameof(path));
        }

        if (string.IsNullOrWhiteSpace(imageName))
        {
            throw new ArgumentException("Image name cannot be empty or whitespace.", nameof(imageName));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Payload file not found.", path);
        }

        if (!IsPayload(path))
        {
            return null;
        }

        var metadata = PayloadParser.GetPayloadMetadata(path);
        var actualName = metadata.PartitionNames.FirstOrDefault(
            name => string.Equals(name, imageName, StringComparison.OrdinalIgnoreCase));
        if (actualName is null)
        {
            return null;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"uotan_payload_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            PayloadParser.Extract(path, [actualName], tempDir);
            var outputPath = Path.Combine(tempDir, $"{actualName}.img");
            return File.Exists(outputPath) ? File.ReadAllBytes(outputPath) : null;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响已返回/抛出的结果。
                // Cleanup failure does not affect the returned or thrown result.
            }
        }
    }
}