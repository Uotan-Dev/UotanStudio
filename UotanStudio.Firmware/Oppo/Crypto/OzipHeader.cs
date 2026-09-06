using System.Text;
using UotanStudio.Firmware.Oppo.Helpers;

namespace UotanStudio.Firmware.Oppo.Crypto;

public sealed class OzipHeader
{
    internal static readonly byte[] MagicBytes =
        [0x4F, 0x50, 0x50, 0x4F, 0x45, 0x4E, 0x43, 0x52, 0x59, 0x50, 0x54, 0x21];

    internal const string MagicString = "OPPOENCRYPT!";

    internal const int SizeOffset = 0x10;

    internal const int SizeFieldLength = 16;

    public const int DataOffset = 0x1050;

    public long EncryptedDataSize { get; }

    private OzipHeader(long encryptedDataSize)
    {
        EncryptedDataSize = encryptedDataSize;
    }

    /// <summary>
    /// Reads and validates the OZip file header from a stream.
    /// <para>从流中读取并验证 OZip 文件头部。</para>
    /// </summary>
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
