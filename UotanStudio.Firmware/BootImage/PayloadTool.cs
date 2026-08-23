using System.Buffers.Binary;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Payload 分区镜像工具。
/// <para>
/// Payload partition image tool.</para>
/// <para>
/// 精简移植：仅实现 magic 检测。完整的 payload 解析/提取请使用本项目的
/// <c>UotanStudio.Firmware.Payload</c> 命名空间下的 PayloadParser / PayloadFormatHandler。</para>
/// <para>
/// Simplified port: only magic detection is implemented. Full payload parsing/extraction
/// is provided by <c>UotanStudio.Firmware.Payload</c> in this project.</para>
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
    /// <returns>镜像数据。<para>The image data.</para></returns>
    public static byte[]? ExtractImage(byte[] data, string imageName)
    {
        throw new NotSupportedException(
            "PayloadTool is a simplified stub. Use UotanStudio.Firmware.Payload.PayloadParser / " +
            "PayloadFormatHandler for full payload extraction.");
    }

    /// <summary>
    /// 从 payload 文件中提取指定镜像。
    /// <para>Extracts the named image from a payload file.</para>
    /// </summary>
    /// <param name="path">payload 文件路径。<para>The payload file path.</para></param>
    /// <param name="imageName">镜像名称。<para>The image name.</para></param>
    /// <returns>镜像数据。<para>The image data.</para></returns>
    public static byte[]? ExtractImage(string path, string imageName)
    {
        throw new NotSupportedException(
            "PayloadTool is a simplified stub. Use UotanStudio.Firmware.Payload.PayloadParser / " +
            "PayloadFormatHandler for full payload extraction.");
    }
}