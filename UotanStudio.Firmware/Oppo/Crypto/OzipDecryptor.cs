using System.Security.Cryptography;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Helpers;

namespace UotanStudio.Firmware.Oppo.Crypto;

public static class OzipDecryptor
{
    private const int DataOffset = OzipHeader.DataOffset;

    internal const int PlaintextBlockSize = 0x4000;

    internal const int EncryptedSize = 16;

    internal const int EncryptionUnitSize = EncryptedSize + PlaintextBlockSize;

    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

    private static readonly byte[] AvbMagic = [0x41, 0x56, 0x42, 0x30];

    private static readonly byte[] AndrMagic = [0x41, 0x4E, 0x44, 0x52];

    /// <summary>
    /// Attempts to automatically detect the correct AES key from encrypted data.
    /// <para>尝试从加密数据中自动检测正确的 AES 密钥。</para>
    /// </summary>
    public static OzipKeyEntry? DetectKey(byte[] firstEncrypted16Bytes)
    {
        Guard.ThrowIfNull(firstEncrypted16Bytes);

        if (firstEncrypted16Bytes.Length != EncryptedSize)
        {
            throw new ArgumentException(
                $"The input must be exactly {EncryptedSize} bytes, but was {firstEncrypted16Bytes.Length} bytes.",
                nameof(firstEncrypted16Bytes));
        }

        foreach (var entry in OzipKeyDatabase.GetAllKeys())
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor(entry.KeyBytes, null);
            var decrypted = decryptor.TransformFinalBlock(firstEncrypted16Bytes, 0, EncryptedSize);

            if (IsValidPlaintext(decrypted))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Decrypts OZip data stream using the specified key.
    /// <para>使用指定密钥解密 OZip 数据流。</para>
    /// </summary>
    public static void Decrypt(Stream input, Stream output, byte[] aesKey, long encryptedDataSize)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);
        Guard.ThrowIfNull(aesKey);

        if (aesKey.Length != EncryptedSize)
        {
            throw new ArgumentException(
                $"The AES key must be exactly {EncryptedSize} bytes, but was {aesKey.Length} bytes.",
                nameof(aesKey));
        }

        if (encryptedDataSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(encryptedDataSize));
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor(aesKey, null);

        var remaining = encryptedDataSize;
        var encryptedBuffer = new byte[EncryptedSize];
        var decryptedBuffer = new byte[EncryptedSize];
        var plaintextBuffer = new byte[PlaintextBlockSize];

        while (remaining > 0)
        {
            if (remaining < EncryptedSize)
            {
                throw new InvalidOppFormatException(
                    $"The OZip data ends with an incomplete {EncryptedSize}-byte encrypted prefix.");
            }

            ReadExactly(input, encryptedBuffer, EncryptedSize);
            if (decryptor.TransformBlock(encryptedBuffer, 0, EncryptedSize, decryptedBuffer, 0) != EncryptedSize)
            {
                throw new CryptographicException("AES did not decrypt a complete OZip block prefix.");
            }

            output.Write(decryptedBuffer, 0, EncryptedSize);
            remaining -= EncryptedSize;


            if (remaining > 0)
            {
                var plaintextToRead = (int)Math.Min(PlaintextBlockSize, remaining);
                ReadExactly(input, plaintextBuffer, plaintextToRead);
                output.Write(plaintextBuffer, 0, plaintextToRead);
                remaining -= plaintextToRead;
            }
        }
    }

    /// <summary>
    /// Decrypts a standard OZip file to an output stream.
    /// <para>解密标准 OZip 文件到输出流。</para>
    /// </summary>
    public static void DecryptFile(Stream input, Stream output, byte[] aesKey)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);
        Guard.ThrowIfNull(aesKey);

        var header = OzipHeader.ReadFrom(input);

        input.Position = DataOffset;
        Decrypt(input, output, aesKey, header.EncryptedDataSize);
    }

    /// <summary>
    /// Automatically detects the key and decrypts an OZip file to an output stream.
    /// <para>自动检测密钥并解密 OZip 文件到输出流。</para>
    /// </summary>
    public static OzipKeyEntry DecryptFileWithAutoDetection(Stream input, Stream output)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        using var decrypted = OpenDecryptedStream(input, out var keyEntry, leaveOpen: true);
        decrypted.CopyTo(output);

        return keyEntry;
    }

    /// <summary>
    /// Opens a seekable, on-demand decrypted view over an OZip stream and returns the detected key.
    /// <para>在 OZip 流上打开可定位的按需解密视图，并返回自动检测到的密钥。</para>
    /// </summary>
    internal static OzipBlockDecryptStream OpenDecryptedStream(
        Stream input,
        out OzipKeyEntry keyEntry,
        bool leaveOpen)
    {
        Guard.ThrowIfNull(input);

        var header = OzipHeader.ReadFrom(input);
        if (header.EncryptedDataSize < EncryptedSize
            || header.EncryptedDataSize > input.Length - DataOffset)
        {
            throw new InvalidOppFormatException(
                $"Invalid OZip data size {header.EncryptedDataSize}; available data is {Math.Max(0, input.Length - DataOffset)} bytes.");
        }

        input.Position = DataOffset;
        var firstEncrypted = new byte[EncryptedSize];
        ReadExactly(input, firstEncrypted, EncryptedSize);
        keyEntry = DetectKey(firstEncrypted) ?? throw new OppDecryptionException(
            "Unable to automatically detect the decryption key. No known key produces valid plaintext.");

        return new OzipBlockDecryptStream(input, keyEntry.KeyBytes, header.EncryptedDataSize, leaveOpen);
    }

    /// <summary>
    /// Checks whether the decrypted data is valid plaintext (starts with known magic bytes).
    /// <para>检查解密后的数据是否为有效的明文（以已知魔数开头）。</para>
    /// </summary>
    internal static bool IsValidPlaintext(byte[] data)
    {
        if (data.Length < 4)
        {
            return false;
        }


        if (data[0] == ZipMagic[0] && data[1] == ZipMagic[1] &&
            data[2] == ZipMagic[2] && data[3] == ZipMagic[3])
        {
            return true;
        }


        if (data[0] == AvbMagic[0] && data[1] == AvbMagic[1] &&
            data[2] == AvbMagic[2] && data[3] == AvbMagic[3])
        {
            return true;
        }


        return data[0] == AndrMagic[0] && data[1] == AndrMagic[1] &&
            data[2] == AndrMagic[2] && data[3] == AndrMagic[3];
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var bytesRead = stream.Read(buffer, totalRead, count - totalRead);
            if (bytesRead == 0)
            {
                throw new InvalidOppFormatException(
                    $"Unexpected end of OZip data: expected {count} bytes but read {totalRead}.");
            }

            totalRead += bytesRead;
        }

    }
}
