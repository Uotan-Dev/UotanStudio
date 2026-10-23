using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.Mbr;

namespace UotanStudio.Firmware.BlockImage;

/// <summary>
/// Detects which partition-table layout a header or whole image carries.
/// <para>识别头部或整镜像所携带的分区表布局。</para>
/// </summary>
public static class PartitionTableProbe
{
    /// <summary>
    /// Detects the partition-table kind from header bytes without validating CRCs. The checks run in
    /// GPT, Amlogic and MBR order so a protective MBR or an Amlogic name table cannot mask the outer format.
    /// <para>仅凭头部字节识别分区表类型，不校验 CRC。检测按 GPT、Amlogic、MBR 的顺序进行，避免保护性 MBR 或
    /// Amlogic 名称表掩盖外层格式。</para>
    /// </summary>
    /// <param name="header">Leading bytes of the image; every check is skipped when the bytes are too short. <para>镜像的起始字节；长度不足时跳过相应检查。</para></param>
    /// <param name="kind">The detected kind, or <see cref="PartitionTableKind.Unknown"/> on failure. <para>识别出的类型，失败时为 <see cref="PartitionTableKind.Unknown"/>。</para></param>
    /// <returns><see langword="true"/> when a layout was recognised. <para>识别出布局时为 <see langword="true"/>。</para></returns>
    public static bool TryDetect(ReadOnlySpan<byte> header, out PartitionTableKind kind)
    {
        if (GptImageEditor.HasSignature(header))
        {
            kind = PartitionTableKind.Gpt;
            return true;
        }

        if (header.Length >= PartitionTableSignatures.AmlogicMagic.Length
            && header[..PartitionTableSignatures.AmlogicMagic.Length].SequenceEqual(PartitionTableSignatures.AmlogicMagic))
        {
            kind = PartitionTableKind.Amlogic;
            return true;
        }

        if (MbrTable.HasPartitionTable(header))
        {
            kind = PartitionTableKind.Mbr;
            return true;
        }

        kind = PartitionTableKind.Unknown;
        return false;
    }

    /// <summary>
    /// Detects and validates the partition table of a whole image. An Amlogic image is recognised by its
    /// magic and validated through its MBR-style slot and EBR chain; invalid or truncated images are
    /// reported as <see cref="PartitionTableKind.Unknown"/> instead of throwing.
    /// <para>识别并校验整镜像的分区表。Amlogic 镜像按魔数识别，并通过其 MBR 风格槽位与 EBR 链校验；
    /// 无效或截断的镜像返回 <see cref="PartitionTableKind.Unknown"/> 而不抛出异常。</para>
    /// </summary>
    /// <param name="image">The whole image bytes. <para>整镜像字节。</para></param>
    /// <param name="kind">The detected kind, or <see cref="PartitionTableKind.Unknown"/> on failure. <para>识别出的类型，失败时为 <see cref="PartitionTableKind.Unknown"/>。</para></param>
    /// <returns><see langword="true"/> when the image carries a parseable partition table. <para>镜像携带可解析的分区表时为 <see langword="true"/>。</para></returns>
    public static bool TryParse(byte[] image, out PartitionTableKind kind)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (GptImageEditor.TryRead(image, out _))
        {
            kind = PartitionTableKind.Gpt;
            return true;
        }

        try
        {
            if (image.Length >= PartitionTableSignatures.AmlogicMagic.Length
                && image.AsSpan(0, PartitionTableSignatures.AmlogicMagic.Length).SequenceEqual(PartitionTableSignatures.AmlogicMagic))
            {
                _ = MbrTable.Parse(image);
                kind = PartitionTableKind.Amlogic;
                return true;
            }

            if (MbrTable.HasPartitionTable(image))
            {
                _ = MbrTable.Parse(image);
                kind = PartitionTableKind.Mbr;
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException)
        {
            kind = PartitionTableKind.Unknown;
            return false;
        }

        kind = PartitionTableKind.Unknown;
        return false;
    }
}