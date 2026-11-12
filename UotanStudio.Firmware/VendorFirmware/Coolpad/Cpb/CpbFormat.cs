using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Hash.CpbCrc16;

namespace UotanStudio.Firmware.VendorFirmware.Coolpad.Cpb;

/// <summary>
/// Field layout, header key, name sanitizing and CRC-16 decoding for Coolpad CPB containers.
/// <para>酷派 CPB 容器的字段布局、头部密钥、名称净化与 CRC-16 解码。</para>
/// </summary>
/// <remarks>
/// A CPB container is one 552-byte header followed by <see cref="ImageHeaderSize"/>-byte image headers and the
/// image payloads they describe. The recorded offset and size of every image are stored obfuscated with a key
/// derived from the header's own correction words, and every checksum is a CRC-16 accumulated over the raw
/// header bytes.
/// <para>CPB 容器由一个 552 字节头、若干 <see cref="ImageHeaderSize"/> 字节的镜像头以及其描述的镜像负载组成。
/// 每个镜像记录的偏移与长度都用一个由头部自身校正字推导出的密钥混淆存储，各校验和则是按原始头部字节累加的 CRC-16。</para>
/// </remarks>
internal static class CpbFormat
{
    /// <summary>Size of the container header in bytes.
    /// <para>容器头的字节长度。</para></summary>
    internal const int HeaderSize = 552;

    /// <summary>Size of one image header in bytes.
    /// <para>单个镜像头的字节长度。</para></summary>
    internal const int ImageHeaderSize = 76;

    /// <summary>Length of the NUL-padded image file name inside an image header.
    /// <para>镜像头内以 NUL 填充的镜像文件名长度。</para></summary>
    internal const int ImageNameLength = 64;

    /// <summary>Upper bound on the image count accepted from one container.
    /// <para>单个容器允许的镜像数量上限。</para></summary>
    internal const int MaximumImageCount = 4096;

    /// <summary>Container magic: <c>CP</c> followed by the supported major and minor version bytes.
    /// <para>容器魔数：<c>CP</c> 加受支持的主、次版本字节。</para></summary>
    internal static ReadOnlySpan<byte> Magic => [(byte)'C', (byte)'P', 0x01, 0x07];

    /// <summary>Offset of the correction words used by the header key.
    /// <para>头部密钥所用校正字的偏移。</para></summary>
    private const int CorrectionOffset = 4;

    /// <summary>Offset of the hardware version string.
    /// <para>硬件版本号字符串的偏移。</para></summary>
    private const int HardwareVersionOffset = 80;

    /// <summary>Offset of the ROM version string.
    /// <para>ROM 版本号字符串的偏移。</para></summary>
    private const int VersionOffset = 96;

    /// <summary>Offset of the ROM source string.
    /// <para>ROM 来源字符串的偏移。</para></summary>
    private const int RomFromOffset = 160;

    /// <summary>Length of the ROM source string.
    /// <para>ROM 来源字符串的长度。</para></summary>
    private const int RomFromLength = 256;

    /// <summary>Offset of the model string.
    /// <para>机型字符串的偏移。</para></summary>
    private const int ModelOffset = 48;

    /// <summary>Length of the model string.
    /// <para>机型字符串的长度。</para></summary>
    private const int ModelLength = 32;

    /// <summary>Length of the hardware version string.
    /// <para>硬件版本号字符串的长度。</para></summary>
    private const int HardwareVersionLength = 16;

    /// <summary>Length of the ROM version string.
    /// <para>ROM 版本号字符串的长度。</para></summary>
    private const int VersionLength = 64;

    /// <summary>Offset of the image-header end position.
    /// <para>镜像头结束位置的偏移。</para></summary>
    private const int ImageHeaderEndOffset = 416;

    /// <summary>Offset of the container checksum.
    /// <para>容器校验和的偏移。</para></summary>
    private const int ChecksumOffset = 548;

    /// <summary>Decodes one stored image offset or size.
    /// <para>解码一个存储的镜像偏移或长度。</para>
    /// </summary>
    /// <param name="value">Stored value as read from the image header.
    /// <para>从镜像头读到的存储值。</para></param>
    /// <param name="index">Zero-based image index.
    /// <para>从零开始的镜像序号。</para></param>
    /// <param name="key">Header key from <see cref="DeriveKey"/>.
    /// <para>来自 <see cref="DeriveKey"/> 的头部密钥。</para></param>
    /// <returns>The decoded value; the transform is its own inverse, so applying it twice restores the input.
    /// <para>解码值；该变换是自逆的，因此应用两次可还原输入。</para></returns>
    internal static uint Decode(uint value, int index, uint key)
    {
        unchecked
        {
            var k0 = (byte)(index + (byte)key);
            var k1 = (byte)(index + (byte)(key >> 8));
            var k2 = (byte)(index + (byte)(key >> 16));
            var k3 = (byte)(index + (byte)(key >> 24));
            return (uint)(((byte)value) ^ k2)
                | (uint)(((byte)(value >> 8) ^ k3) << 8)
                | (uint)(((byte)(value >> 16) ^ k0) << 16)
                | (uint)(((byte)(value >> 24) ^ k1) << 24);
        }
    }

    /// <summary>Parses one CPB container without reading any image payload.
    /// <para>解析一个 CPB 容器，不读取任何镜像负载。</para>
    /// </summary>
    /// <param name="stream">Seekable container stream opened read-only.
    /// <para>以只读方式打开的可寻址容器流。</para></param>
    /// <param name="length">Total container length in bytes, used to bound every declared range.
    /// <para>容器总字节长度，用于约束每一处声明的范围。</para></param>
    /// <param name="cancellationToken">Cancels the parse between image headers.
    /// <para>在镜像头之间取消解析。</para></param>
    /// <returns>The decoded header and image list.
    /// <para>解码后的头部与镜像列表。</para></returns>
    /// <exception cref="InvalidDataException">The magic, version, geometry or a declared range is inconsistent.
    /// <para>魔数、版本、几何或某个声明的范围不一致。</para></exception>
    internal static CpbArchive Read(Stream stream, long length, CancellationToken cancellationToken)
    {
        if (length < HeaderSize)
        {
            throw new InvalidDataException($"CPB header is truncated: {length} bytes available, {HeaderSize} required.");
        }

        var header = new byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(header);

        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("CPB magic mismatch: expected 'CP' with version 1.7.");
        }

        var computedChecksum = CpbCrc16.Compute(header.AsSpan(0, ChecksumOffset), 0);
        var storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(ChecksumOffset, 4));
        var headerEnd = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(ImageHeaderEndOffset, 4));
        if (headerEnd < HeaderSize || headerEnd > length)
        {
            throw new InvalidDataException($"CPB image header table ends at {headerEnd}, outside the {length}-byte container.");
        }

        var tableBytes = (long)headerEnd - HeaderSize;
        if (tableBytes > (long)MaximumImageCount * ImageHeaderSize)
        {
            throw new InvalidDataException($"CPB declares {tableBytes / ImageHeaderSize} image headers, above the {MaximumImageCount} limit.");
        }

        var count = (int)(tableBytes / ImageHeaderSize);
        var table = new byte[count * ImageHeaderSize];
        if (table.Length > 0)
        {
            stream.ReadExactly(table);
        }

        var key = DeriveKey(header);
        var images = new List<CpbImage>(count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = table.AsSpan(index * ImageHeaderSize, ImageHeaderSize);
            var offset = Decode(BinaryPrimitives.ReadUInt32LittleEndian(raw[(ImageNameLength + 0)..]), index, key);
            var size = Decode(BinaryPrimitives.ReadUInt32LittleEndian(raw[(ImageNameLength + 4)..]), index, key);
            var entryChecksum = BinaryPrimitives.ReadUInt32LittleEndian(raw[(ImageNameLength + 8)..]);
            var decoded = raw.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(decoded.AsSpan(ImageNameLength + 0, 4), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(decoded.AsSpan(ImageNameLength + 4, 4), size);
            computedChecksum = CpbCrc16.Compute(decoded, computedChecksum);
            if (size > 0)
            {
                if (offset < headerEnd || (ulong)offset + size > (ulong)length)
                {
                    throw new InvalidDataException($"CPB image {index} declares bytes {offset}+{size} outside the container.");
                }
            }

            var name = SanitizeName(raw[..ImageNameLength], index);
            if (!names.Add(name))
            {
                name = index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + name;
                names.Add(name);
            }

            images.Add(new CpbImage(index, name, offset, size, entryChecksum));
        }

        unchecked
        {
            computedChecksum += header[3];
        }

        var matches = computedChecksum == storedChecksum || (computedChecksum & 0xFFFF) == (storedChecksum & 0xFFFF);
        var cpbHeader = new CpbHeader(
            header[2],
            header[3],
            ReadString(header, ModelOffset, ModelLength),
            ReadString(header, HardwareVersionOffset, HardwareVersionLength),
            ReadString(header, VersionOffset, VersionLength),
            ReadString(header, RomFromOffset, RomFromLength),
            headerEnd,
            storedChecksum,
            computedChecksum,
            matches);
        return new CpbArchive(cpbHeader, images);
    }

    /// <summary>Derives the header key from the correction words stored in the header.
    /// <para>由头部存储的校正字推导头部密钥。</para>
    /// </summary>
    /// <param name="header">Raw header bytes.
    /// <para>原始头部字节。</para></param>
    /// <returns>The 32-bit key used to decode every stored image offset and size.
    /// <para>用于解码全部存储的镜像偏移与长度的 32 位密钥。</para></returns>
    internal static uint DeriveKey(ReadOnlySpan<byte> header)
    {
        unchecked
        {
            uint sum = 0;
            for (var index = 0; index < 6; index++)
            {
                sum += BinaryPrimitives.ReadUInt16LittleEndian(header[(CorrectionOffset + (index * 2))..]);
            }

            var quotient = (uint)((0x66666667UL * sum) >> 32) >> 1;
            var normalized = (ushort)((quotient >> 31) + quotient);
            var low = (int)normalized - BinaryPrimitives.ReadUInt16LittleEndian(header[(CorrectionOffset + 6)..]);
            var high = (int)normalized - BinaryPrimitives.ReadUInt16LittleEndian(header[(CorrectionOffset + 8)..]);
            return (uint)low | (uint)(high << 16);
        }
    }

    private static string ReadString(ReadOnlySpan<byte> header, int offset, int length)
    {
        var span = header.Slice(offset, length);
        for (var index = 0; index < span.Length; index++)
        {
            if (span[index] == 0)
            {
                span = span[..index];
                break;
            }
        }

        return Encoding.UTF8.GetString(span).Trim();
    }

    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    private static string SanitizeName(ReadOnlySpan<byte> field, int index)
    {
        var end = field.IndexOf((byte)0);
        var span = end >= 0 ? field[..end] : field;
        var text = Encoding.UTF8.GetString(span).Trim();
        var separator = text.LastIndexOfAny(['/', '\\']);
        if (separator >= 0)
        {
            text = text[(separator + 1)..];
        }

        if (text.Length == 0 || text is "." or "..")
        {
            return "image-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin";
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(InvalidFileNameChars.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }
}

/// <summary>Decoded CPB container header.
/// <para>解码后的 CPB 容器头。</para>
/// </summary>
/// <param name="VersionMajor">Container major version byte.
/// <para>容器主版本字节。</para></param>
/// <param name="VersionMinor">Container minor version byte.
/// <para>容器次版本字节。</para></param>
/// <param name="Model">Device model the package targets; empty when not recorded.
/// <para>包适用的机型；未记录时为空。</para></param>
/// <param name="HardwareVersion">Hardware revision the package targets; empty when not recorded.
/// <para>包适用的硬件版本；未记录时为空。</para></param>
/// <param name="Version">ROM version string; empty when not recorded.
/// <para>ROM 版本号字符串；未记录时为空。</para></param>
/// <param name="RomFrom">ROM source string; empty when not recorded.
/// <para>ROM 来源字符串；未记录时为空。</para></param>
/// <param name="ImageHeaderEndPosition">Byte offset where the image header table ends; also the first payload byte.
/// <para>镜像头表结束的字节偏移；也是第一个负载字节的位置。</para></param>
/// <param name="CheckSum">Checksum stored in the header.
/// <para>头部存储的校验和。</para></param>
/// <param name="ComputedCheckSum">Checksum recomputed from the header and image headers.
/// <para>由头部与镜像头重新计算的校验和。</para></param>
/// <param name="CheckSumMatches">Whether the stored checksum matches the recomputed value, fully or in its low 16 bits.
/// <para>存储的校验和是否与重算值一致（完整一致或低 16 位一致）。</para></param>
internal sealed record CpbHeader(
    int VersionMajor,
    int VersionMinor,
    string Model,
    string HardwareVersion,
    string Version,
    string RomFrom,
    uint ImageHeaderEndPosition,
    uint CheckSum,
    uint ComputedCheckSum,
    bool CheckSumMatches)
{
    /// <summary>Container version formatted as <c>major.minor</c>.
    /// <para>形如 <c>major.minor</c> 的容器版本。</para></summary>
    /// <value>The version text used in summaries and metadata.
    /// <para>用于摘要与元数据的版本文本。</para></value>
    public string VersionText => VersionMajor.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
        VersionMinor.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One image declared by a CPB container.
/// <para>CPB 容器声明的一个镜像。</para>
/// </summary>
/// <param name="Index">Zero-based position inside the image header table.
/// <para>镜像头表内从零开始的位置。</para></param>
/// <param name="Name">Sanitized file name safe to write inside the output directory.
/// <para>可安全写入输出目录的文件名。</para></param>
/// <param name="Offset">Decoded absolute byte offset of the payload.
/// <para>解码后的负载绝对字节偏移。</para></param>
/// <param name="Size">Decoded payload length in bytes; <c>0</c> means the entry carries no data.
/// <para>解码后的负载字节长度；<c>0</c> 表示该条目不携带数据。</para></param>
/// <param name="CheckSum">Checksum recorded for the payload.
/// <para>该负载记录的校验和。</para></param>
internal sealed record CpbImage(int Index, string Name, uint Offset, uint Size, uint CheckSum);

/// <summary>Decoded CPB container: its header plus every declared image.
/// <para>解码后的 CPB 容器：头部及全部声明的镜像。</para>
/// </summary>
/// <param name="Header">Decoded container header.
/// <para>解码后的容器头。</para></param>
/// <param name="Images">Images in image-header order.
/// <para>按镜像头顺序排列的镜像。</para></param>
internal sealed record CpbArchive(CpbHeader Header, IReadOnlyList<CpbImage> Images);