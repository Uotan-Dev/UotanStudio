using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;

/// <summary>
/// Validated header of an OZip file: the 12-byte <c>OPPOENCRYPT!</c> magic plus the decimal encrypted
/// data size stored in the 16-byte field at offset <c>0x10</c>.
/// <para>OZip 文件的已校验头部：12 字节的 <c>OPPOENCRYPT!</c> 魔数，加上偏移 <c>0x10</c> 处 16 字节字段中的十进制加密数据大小。</para>
/// </summary>
internal sealed class OzipHeader
{
    internal static readonly byte[] MagicBytes =
        [0x4F, 0x50, 0x50, 0x4F, 0x45, 0x4E, 0x43, 0x52, 0x59, 0x50, 0x54, 0x21];

    internal const string MagicString = "OPPOENCRYPT!";

    internal const int SizeOffset = 0x10;

    internal const int SizeFieldLength = 16;

    /// <summary>Offset (0x1050) from the start of a standard OZip file where the encrypted payload begins.
    /// <para>标准 OZip 文件中加密负载的起始偏移（0x1050）。</para></summary>
    public const int DataOffset = 0x1050;

    /// <summary>Byte length of the encrypted payload as declared by the header size field.
    /// <para>头部大小字段所声明的加密负载字节长度。</para></summary>
    /// <value>The size parsed from the header's decimal text field, trimmed of NUL and whitespace.
    /// <para>从头部十进制文本字段解析得到的大小，已去除 NUL 与空白字符。</para></value>
    public long EncryptedDataSize { get; }

    private OzipHeader(long encryptedDataSize)
    {
        EncryptedDataSize = encryptedDataSize;
    }

    /// <summary>
    /// Reads and validates the OZip file header from a stream.
    /// <para>从流中读取并验证 OZip 文件头部。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable, seekable stream; the magic is read at offset 0 and the size field at 0x10 regardless of
    /// the current position, and the original position is restored before returning, including on failure.
    /// <para>可读取、可寻址的流；无论当前位置如何，魔数从偏移 0 读取、大小字段从 0x10 读取，返回前（包括失败时）都会恢复原始位置。</para>
    /// </param>
    /// <returns>The validated header carrying the declared encrypted payload size.
    /// <para>已校验的头部，携带声明的加密负载大小。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable or not seekable.
    /// <para><paramref name="stream"/> 不可读取或不可寻址。</para></exception>
    /// <exception cref="InvalidOppFormatException">
    /// The file is too short for the magic or size field, the magic bytes do not match <c>OPPOENCRYPT!</c>,
    /// or the size field is not a decimal number.
    /// <para>文件长度不足以容纳魔数或大小字段、魔数字节与 <c>OPPOENCRYPT!</c> 不匹配，或大小字段不是十进制数。</para>
    /// </exception>
    public static OzipHeader ReadFrom(Stream stream)
    {
        Guard.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Stream must be readable and seekable.", nameof(stream));
        }

        var originalPosition = stream.Position;

        try
        {

            stream.Position = 0;
            var magicBuffer = new byte[MagicBytes.Length];
            var bytesRead = stream.Read(magicBuffer, 0, MagicBytes.Length);
            if (bytesRead != MagicBytes.Length)
            {
                throw new InvalidOppFormatException(
                    "Unable to read OZip magic bytes. The file is too short or corrupted.");
            }

            for (var i = 0; i < MagicBytes.Length; i++)
            {
                if (magicBuffer[i] != MagicBytes[i])
                {
                    throw new InvalidOppFormatException(
                        $"Invalid OZip magic. Expected 'OPPOENCRYPT!' but found different bytes at position {i}.");
                }
            }


            stream.Position = SizeOffset;
            var sizeBuffer = new byte[SizeFieldLength];
            bytesRead = stream.Read(sizeBuffer, 0, SizeFieldLength);
            if (bytesRead != SizeFieldLength)
            {
                throw new InvalidOppFormatException(
                    "Unable to read OZip encrypted data size. The file is too short or corrupted.");
            }


            var sizeString = Encoding.ASCII.GetString(sizeBuffer).TrimEnd('\0').Trim();
            return !long.TryParse(sizeString, out var encryptedDataSize)
                ? throw new InvalidOppFormatException(
                    $"Invalid OZip encrypted data size field: '{sizeString}'. Expected a decimal number.")
                : new OzipHeader(encryptedDataSize);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Checks whether the specified stream is an OZip encrypted file.
    /// <para>检查指定流是否为 OZip 加密文件。</para>
    /// </summary>
    /// <param name="stream">
    /// Candidate stream; only the 12 magic bytes at offset 0 are inspected and the original position is
    /// restored before returning.
    /// <para>待检查的流；仅检查偏移 0 处的 12 个魔数字节，返回前恢复原始位置。</para>
    /// </param>
    /// <returns><see langword="true"/> when the leading bytes match <c>OPPOENCRYPT!</c>;
    /// <see langword="false"/> when the stream is not readable or seekable, is shorter than the magic,
    /// mismatches, or a read fails — probing never throws for malformed content.
    /// <para>起始字节与 <c>OPPOENCRYPT!</c> 匹配时返回 <see langword="true"/>；流不可读取或不可寻址、长度不足魔数长度、
    /// 内容不匹配或读取失败时返回 <see langword="false"/>——探测过程不会因内容异常而抛出异常。</para></returns>
    public static bool IsOzipFile(Stream stream)
    {
        Guard.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek)
        {
            return false;
        }

        if (stream.Length < MagicBytes.Length)
        {
            return false;
        }

        var originalPosition = stream.Position;

        try
        {
            stream.Position = 0;
            var buffer = new byte[MagicBytes.Length];
            var bytesRead = stream.Read(buffer, 0, MagicBytes.Length);

            if (bytesRead != MagicBytes.Length)
            {
                return false;
            }

            for (var i = 0; i < MagicBytes.Length; i++)
            {
                if (buffer[i] != MagicBytes[i])
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }
}