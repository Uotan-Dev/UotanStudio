using System.Buffers.Binary;
using UotanStudio.Firmware.Payload;

namespace UotanStudio.Firmware.BootImage;

public static class PayloadTool
{
    private const uint PayloadMagic = 0x43524155;

    /// <summary>
    /// Checks whether the data is an Android OTA payload.
    /// <para>检查数据是否为 Android OTA Payload 格式。</para>
    /// </summary>
    public static bool IsPayload(byte[] data) => data != null && data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == PayloadMagic;

    /// <summary>
    /// Checks whether the file is an Android OTA payload.
    /// <para>检查文件是否为 Android OTA Payload 格式。</para>
    /// </summary>
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
    /// Extracts the named image from a payload.
    /// <para>从 payload 中提取指定镜像。</para>
    /// </summary>
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


            }
        }
    }

    /// <summary>
    /// Extracts the named image from a payload file.
    /// <para>从 payload 文件中提取指定镜像。</para>
    /// </summary>
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


            }
        }
    }
}
