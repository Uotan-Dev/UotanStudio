using System.Buffers.Binary;
using UotanStudio.Firmware.OtaDelta.Payload;

namespace UotanStudio.Firmware.OtaDelta.Payload;

/// <summary>
/// Convenience entry points for detecting Android OTA payload inputs and extracting one partition image into memory.
/// <para>识别 Android OTA payload 输入并将单个分区镜像提取到内存的便捷入口。</para>
/// </summary>
public static class PayloadTool
{
    private const uint PayloadMagic = 0x43724155;

    /// <summary>
    /// Checks whether the data is an Android OTA payload.
    /// <para>检查数据是否为 Android OTA Payload 格式。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate payload bytes; <see langword="null"/> or fewer than four bytes are reported as not a payload.
    /// <para>候选 payload 字节；为 <see langword="null"/> 或少于四个字节时判定为非 payload。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the first four big-endian bytes equal the <c>CrAU</c> magic; otherwise <see langword="false"/>.
    /// <para>前四个字节按大端序等于 <c>CrAU</c> 魔数时返回 <see langword="true"/>，否则返回 <see langword="false"/>。</para>
    /// </returns>
    public static bool IsPayload(byte[] data) => data != null && data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == PayloadMagic;

    /// <summary>
    /// Checks whether the file is an Android OTA payload.
    /// <para>检查文件是否为 Android OTA Payload 格式。</para>
    /// </summary>
    /// <param name="path">
    /// Candidate payload file path; blank or non-existent paths are reported as not a payload.
    /// <para>候选 payload 文件路径；空白路径或文件不存在时判定为非 payload。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the file exists and its first four big-endian bytes equal the <c>CrAU</c>
    /// magic; otherwise <see langword="false"/>. The file is opened read-only and closed before returning,
    /// and a short file is reported as not a payload.
    /// <para>文件存在且前四个字节按大端序等于 <c>CrAU</c> 魔数时返回 <see langword="true"/>，否则返回
    /// <see langword="false"/>。返回前以只读方式打开并关闭文件；过短的文件判定为非 payload。</para>
    /// </returns>
    /// <exception cref="IOException">
    /// The path exists but cannot be opened for reading.
    /// <para>路径存在但无法以读取方式打开。</para>
    /// </exception>
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
    /// <param name="data">
    /// Complete payload bytes; the array is copied to a temporary file for extraction and the copy is
    /// deleted before returning (a failed cleanup is ignored).
    /// <para>完整的 payload 字节；数组会被复制到临时文件进行提取，返回前删除该副本（清理失败会被忽略）。</para>
    /// </param>
    /// <param name="imageName">
    /// Partition name to extract, matched case-insensitively against the manifest; blank is rejected.
    /// <para>要提取的分区名称，与 manifest 按不区分大小写匹配；空白值会被拒绝。</para>
    /// </param>
    /// <returns>
    /// The whole partition image materialized in memory, or <see langword="null"/> when the data is empty,
    /// lacks the payload magic, or declares no matching partition.
    /// <para>完整载入内存的分区镜像；当数据为空、缺少 payload 魔数，或没有匹配的分区声明时返回
    /// <see langword="null"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="imageName"/> is empty or whitespace.
    /// <para><paramref name="imageName"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para>
    /// </exception>
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
    /// <param name="path">
    /// Payload file path; it must exist and carry the payload magic.
    /// <para>payload 文件路径；必须存在且带有 payload 魔数。</para>
    /// </param>
    /// <param name="imageName">
    /// Partition name to extract, matched case-insensitively against the manifest; blank is rejected.
    /// <para>要提取的分区名称，与 manifest 按不区分大小写匹配；空白值会被拒绝。</para>
    /// </param>
    /// <returns>
    /// The whole image of the matched partition materialized in memory from <c>{name}.img</c> written to a
    /// temporary directory, or <see langword="null"/> when the file lacks the payload magic or the manifest
    /// declares no matching partition. The temporary directory is deleted before returning and a failed
    /// cleanup is ignored.
    /// <para>从写入临时目录的 <c>{name}.img</c> 完整载入内存的匹配分区镜像；当文件缺少 payload 魔数，
    /// 或 manifest 中没有匹配分区时返回 <see langword="null"/>。返回前删除临时目录，清理失败会被忽略。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> or <paramref name="imageName"/> is empty or whitespace.
    /// <para><paramref name="path"/> 或 <paramref name="imageName"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="path"/> does not exist.
    /// <para><paramref name="path"/> 不存在。</para>
    /// </exception>
    /// <example>
    /// <code>
    /// byte[]? boot = PayloadTool.ExtractImage(@"C:\ota\payload.bin", "boot");
    /// </code>
    /// </example>
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