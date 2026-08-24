using System.Text;
using UotanStudio.Firmware.Oppo.Helpers;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>表示 OZip 加密固件文件的头部结构。</para>
/// Represents the header structure of an OZip encrypted firmware file.
/// </summary>
/// <remarks>
/// <para>
/// OZip 文件头部包含 12 字节的魔数 "OPPOENCRYPT!"，以及从偏移 0x10 处读取的加密数据大小。
/// 加密数据从偏移 0x1050 处开始。
/// </para>
/// The OZip file header contains a 12-byte magic "OPPOENCRYPT!", and the encrypted data size
/// read from offset 0x10. Encrypted data starts from offset 0x1050.
/// </remarks>
public sealed class OzipHeader
{
    /// <summary>
    /// <para>OZip 文件的魔数标识 "OPPOENCRYPT!" 的字节序列。</para>
    /// The byte sequence of the OZip file magic "OPPOENCRYPT!".
    /// </summary>
    internal static readonly byte[] MagicBytes =
        [0x4F, 0x50, 0x50, 0x4F, 0x45, 0x4E, 0x43, 0x52, 0x59, 0x50, 0x54, 0x21];

    /// <summary>
    /// <para>魔数字符串 "OPPOENCRYPT!"。</para>
    /// The magic string "OPPOENCRYPT!".
    /// </summary>
    internal const string MagicString = "OPPOENCRYPT!";

    /// <summary>
    /// <para>加密数据大小字段的偏移量。</para>
    /// The offset of the encrypted data size field.
    /// </summary>
    internal const int SizeOffset = 0x10;

    /// <summary>
    /// <para>加密数据大小字段的长度（16 字节 ASCII 十进制字符串）。</para>
    /// The length of the encrypted data size field (16-byte ASCII decimal string).
    /// </summary>
    internal const int SizeFieldLength = 16;

    /// <summary>
    /// <para>加密数据的起始偏移量。</para>
    /// The starting offset of the encrypted data.
    /// </summary>
    public const int DataOffset = 0x1050;

    /// <summary>
    /// <para>获取加密数据的大小（字节）。</para>
    /// Gets the size of the encrypted data in bytes.
    /// </summary>
    public long EncryptedDataSize { get; }

    private OzipHeader(long encryptedDataSize)
    {
        EncryptedDataSize = encryptedDataSize;
    }

    /// <summary>
    /// <para>从流中读取并验证 OZip 文件头部。</para>
    /// Reads and validates the OZip file header from a stream.
    /// </summary>
    /// <param name="stream"><para>可读取的流，必须支持定位操作。</para>A readable stream that supports seeking.</param>
    /// <returns><para>解析后的 <see cref="OzipHeader"/> 实例。</para>The parsed <see cref="OzipHeader"/> instance.</returns>
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
            // Read and validate magic bytes
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

            // Read encrypted data size from offset 0x10
            stream.Position = SizeOffset;
            var sizeBuffer = new byte[SizeFieldLength];
            bytesRead = stream.Read(sizeBuffer, 0, SizeFieldLength);
            if (bytesRead != SizeFieldLength)
            {
                throw new InvalidOppFormatException(
                    "Unable to read OZip encrypted data size. The file is too short or corrupted.");
            }

            // Parse size as ASCII decimal string (null-padded)
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
    /// <para>检查指定流是否为 OZip 加密文件。</para>
    /// Checks whether the specified stream is an OZip encrypted file.
    /// </summary>
    /// <param name="stream"><para>可读取的流，必须支持定位操作。</para>A readable stream that supports seeking.</param>
    /// <returns><para>若流以 OZip 魔数开头返回 true；否则返回 false。</para>True if the stream starts with the OZip magic; otherwise, false.</returns>
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