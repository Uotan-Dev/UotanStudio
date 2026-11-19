using System.Globalization;
using System.Text;

namespace UotanStudio.DD.Images;

/// <summary>
/// Validates that an image is a plausible ISO-9660 primary volume image before optical recording.
/// <para>在光盘刻录前校验镜像是否是合理的 ISO-9660 主卷镜像。</para>
/// </summary>
/// <remarks>
/// Optical recorders accept a 2048-byte-per-sector ISO image, not an arbitrary raw disk image. The validator
/// reads only the primary volume descriptor at logical block 16 and never decodes the filesystem payload.
/// <para>
/// 光驱刻录机接收的是每扇区 2048 字节的 ISO 镜像，而不是任意裸磁盘镜像。校验器只读取逻辑块 16
/// 上的主卷描述符，不解码文件系统负载。
/// </para>
/// </remarks>
internal static class IsoImageValidator
{
    /// <summary>
    /// The ISO-9660 logical block size in bytes.
    /// <para>ISO-9660 逻辑块大小（字节）。</para>
    /// </summary>
    internal const int SectorSize = 2048;

    /// <summary>
    /// The logical block number of the primary volume descriptor.
    /// <para>主卷描述符所在的逻辑块号。</para>
    /// </summary>
    internal const int PrimaryVolumeDescriptorBlock = 16;

    private const int DescriptorIdentifierOffset = 1;
    private const int DescriptorTypeOffset = 0;
    private const string DescriptorIdentifier = "CD001";
    private const byte PrimaryVolumeDescriptorType = 1;

    /// <summary>
    /// Validates the image at <paramref name="path"/> and throws when it is not a plausible ISO image.
    /// <para>校验 <paramref name="path"/> 处的镜像；不是合理的 ISO 镜像时抛出异常。</para>
    /// </summary>
    /// <param name="path">Absolute image path.<para>镜像绝对路径。</para></param>
    /// <exception cref="FlashException">The image is missing, too short, not sector-aligned or its
    /// primary volume descriptor is invalid.<para>镜像不存在、过短、未按扇区对齐，或主卷描述符无效。</para></exception>
    internal static void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FlashException("The image file for optical recording does not exist.");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FlashException("The optical image cannot be opened: " + exception.Message, exception);
        }

        using (stream)
        {
            var length = stream.Length;
            var minimum = (long)(PrimaryVolumeDescriptorBlock + 1) * SectorSize;
            if (length < minimum)
            {
                throw new FlashException(
                    "The image is too short to be an ISO-9660 image ("
                    + length.ToString(CultureInfo.InvariantCulture) + " bytes; at least "
                    + minimum.ToString(CultureInfo.InvariantCulture) + " bytes required).");
            }

            if (length % SectorSize != 0)
            {
                throw new FlashException(
                    "The image length " + length.ToString(CultureInfo.InvariantCulture)
                    + " is not a multiple of the " + SectorSize.ToString(CultureInfo.InvariantCulture)
                    + "-byte ISO sector size.");
            }

            var descriptor = new byte[SectorSize];
            stream.Position = (long)PrimaryVolumeDescriptorBlock * SectorSize;
            var read = stream.ReadAtLeast(descriptor, SectorSize, throwOnEndOfStream: false);
            if (read != SectorSize
                || descriptor[DescriptorTypeOffset] != PrimaryVolumeDescriptorType
                || Encoding.ASCII.GetString(descriptor, DescriptorIdentifierOffset, DescriptorIdentifier.Length)
                    != DescriptorIdentifier)
            {
                throw new FlashException(
                    "The image has no ISO-9660 primary volume descriptor (CD001 at logical block 16).");
            }
        }
    }
}