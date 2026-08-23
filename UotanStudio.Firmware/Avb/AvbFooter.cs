using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Avb;

/// <summary>
/// AVB 1.0 footer structure ("AVBf"), ported from MagiskBoot's AvbTool.
/// <para>AVB 1.0 尾部结构("AVBf"),移植自 MagiskBoot 的 AvbTool。</para>
/// </summary>
public readonly struct AvbFooter
{
    /// <summary>
    /// Gets the footer magic ("AVBf").
    /// <para>获取尾部魔数("AVBf")。</para>
    /// </summary>
    public string Magic { get; init; }

    /// <summary>
    /// Gets the footer version.
    /// <para>获取尾部版本号。</para>
    /// </summary>
    public uint Version { get; init; }

    /// <summary>
    /// Gets the reserved field.
    /// <para>获取保留字段。</para>
    /// </summary>
    public uint Reserved { get; init; }

    /// <summary>
    /// Gets the footer size.
    /// <para>获取尾部大小。</para>
    /// </summary>
    public ulong FooterSize { get; init; }

    /// <summary>
    /// Gets the authentication data offset within the image.
    /// <para>获取镜像中认证数据的偏移。</para>
    /// </summary>
    public ulong AuthDataOffset { get; init; }

    /// <summary>
    /// Gets the authentication data size.
    /// <para>获取认证数据大小。</para>
    /// </summary>
    public ulong AuthDataSize { get; init; }

    /// <summary>
    /// Gets the verification data offset within the image.
    /// <para>获取镜像中校验数据的偏移。</para>
    /// </summary>
    public ulong VerifyDataOffset { get; init; }

    /// <summary>
    /// Gets the verification data size.
    /// <para>获取校验数据大小。</para>
    /// </summary>
    public ulong VerifyDataSize { get; init; }

    /// <summary>
    /// Gets the algorithm name (e.g. "SHA256_RSA2048").
    /// <para>获取算法名称(如 "SHA256_RSA2048")。</para>
    /// </summary>
    public string AlgorithmName { get; init; }

    /// <summary>
    /// Size of the AVB footer in bytes.
    /// <para>AVB 尾部的字节大小。</para>
    /// </summary>
    public const int Size = 84;

    /// <summary>
    /// Expected footer magic.
    /// <para>期望的尾部魔数。</para>
    /// </summary>
    public const string ExpectedMagic = "AVBf";

    /// <summary>
    /// Parses an <see cref="AvbFooter"/> from the tail of the given image data.
    /// <para>从镜像数据尾部解析 AvbFooter。</para>
    /// </summary>
    public static AvbFooter? TryLoad(ReadOnlySpan<byte> imageData)
    {
        if (imageData.Length < Size)
        {
            return null;
        }

        var footerOffset = imageData.Length - Size;
        var footer = imageData.Slice(footerOffset, Size);

        var magic = Encoding.ASCII.GetString(footer[..4]);
        return magic != ExpectedMagic
            ? null
            : new AvbFooter
            {
                Magic = magic,
                Version = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]),
                Reserved = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]),
                FooterSize = BinaryPrimitives.ReadUInt64LittleEndian(footer[12..]),
                AuthDataOffset = BinaryPrimitives.ReadUInt64LittleEndian(footer[20..]),
                AuthDataSize = BinaryPrimitives.ReadUInt64LittleEndian(footer[28..]),
                VerifyDataOffset = BinaryPrimitives.ReadUInt64LittleEndian(footer[36..]),
                VerifyDataSize = BinaryPrimitives.ReadUInt64LittleEndian(footer[44..]),
                AlgorithmName = ReadAscii(footer, 52, 32)
            };
    }

    private static string ReadAscii(ReadOnlySpan<byte> data, int offset, int length)
    {
        var slice = data.Slice(offset, length);
        var end = 0;
        while (end < slice.Length && slice[end] != 0)
        {
            end++;
        }
        return Encoding.ASCII.GetString(slice[..end]);
    }
}