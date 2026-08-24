using System.Security.Cryptography;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Helpers;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>提供 OZip 加密固件文件的 AES-128-ECB 解密功能。</para>
/// Provides AES-128-ECB decryption functionality for OZip encrypted firmware files.
/// </summary>
/// <remarks>
/// <para>
/// OZip 加密算法对每 0x4000 (16384) 字节的块，仅加密前 16 字节（AES-ECB），
/// 剩余 0x3FF0 字节保持明文。解密时需要通过尝试已知密钥来检测正确的密钥，
/// 有效的密钥解密后会得到以 PK\x03\x04（ZIP 头）、AVB0 或 ANDR 开头的数据。
/// </para>
/// The OZip encryption algorithm encrypts only the first 16 bytes of each 0x4000 (16384) byte
/// block using AES-ECB, leaving the remaining 0x3FF0 bytes as plaintext. Decryption requires
/// trying known keys to detect the correct one; a valid key produces output starting with
/// PK\x03\x04 (ZIP header), AVB0, or ANDR.
/// </remarks>
public static class OzipDecryptor
{
    /// <summary>
    /// <para>加密数据块的起始偏移量。</para>
    /// The starting offset of encrypted data.
    /// </summary>
    private const int DataOffset = OzipHeader.DataOffset;

    /// <summary>
    /// <para>每个加密块的大小（0x4000 = 16384 字节）。</para>
    /// The size of each encryption block (0x4000 = 16384 bytes).
    /// </summary>
    private const int BlockSize = 0x4000;

    /// <summary>
    /// <para>每个块中加密部分的大小（16 字节，AES 块大小）。</para>
    /// The size of the encrypted portion within each block (16 bytes, AES block size).
    /// </summary>
    private const int EncryptedSize = 16;

    /// <summary>
    /// <para>每个块中明文部分的大小（0x3FF0 字节）。</para>
    /// The size of the plaintext portion within each block (0x3FF0 bytes).
    /// </summary>
    private const int PlaintextSize = BlockSize - EncryptedSize;

    /// <summary>
    /// <para>ZIP 文件头的魔数字节 PK\x03\x04。</para>
    /// The magic bytes for ZIP file header PK\x03\x04.
    /// </summary>
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// <para>AVB0 魔数字节。</para>
    /// The AVB0 magic bytes.
    /// </summary>
    private static readonly byte[] AvbMagic = [0x41, 0x56, 0x42, 0x30];

    /// <summary>
    /// <para>ANDR 魔数字节。</para>
    /// The ANDR magic bytes.
    /// </summary>
    private static readonly byte[] AndrMagic = [0x41, 0x4E, 0x44, 0x52];

    /// <summary>
    /// <para>尝试从加密数据中自动检测正确的 AES 密钥。</para>
    /// Attempts to automatically detect the correct AES key from encrypted data.
    /// </summary>
    /// <param name="firstEncrypted16Bytes"><para>加密数据的前 16 字节（从偏移 0x1050 处读取）。</para>The first 16 bytes of encrypted data (read from offset 0x1050).</param>
    /// <returns><para>检测到的密钥条目；若所有已知密钥均无效则返回 null。</para>The detected key entry, or null if no known key is valid.</returns>
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
    /// <para>使用指定密钥解密 OZip 数据流。</para>
    /// Decrypts OZip data stream using the specified key.
    /// </summary>
    /// <param name="input"><para>输入加密数据流，必须定位到加密数据的起始位置。</para>The input encrypted data stream, must be positioned at the start of encrypted data.</param>
    /// <param name="output"><para>输出解密数据流。</para>The output decrypted data stream.</param>
    /// <param name="aesKey"><para>AES-128 密钥（16 字节）。</para>The AES-128 key (16 bytes).</param>
    /// <param name="encryptedDataSize"><para>加密数据的总大小（字节）。</para>The total size of the encrypted data in bytes.</param>
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

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor(aesKey, null);

        var remaining = encryptedDataSize;
        var encryptedBuffer = new byte[EncryptedSize];
        var plaintextBuffer = new byte[PlaintextSize];

        while (remaining > 0)
        {
            // Read and decrypt first 16 bytes of the block
            var bytesRead = FillBuffer(input, encryptedBuffer, EncryptedSize);
            if (bytesRead == 0)
            {
                break;
            }

            var toWrite = (int)Math.Min(bytesRead, remaining);
            var decrypted = decryptor.TransformFinalBlock(encryptedBuffer, 0, bytesRead);
            output.Write(decrypted, 0, toWrite);
            remaining -= toWrite;

            // Copy remaining plaintext bytes in the block
            if (remaining > 0)
            {
                var plaintextToRead = (int)Math.Min(PlaintextSize, remaining);
                var plaintextRead = FillBuffer(input, plaintextBuffer, plaintextToRead);
                if (plaintextRead == 0)
                {
                    break;
                }

                output.Write(plaintextBuffer, 0, plaintextRead);
                remaining -= plaintextRead;
            }
        }
    }

    /// <summary>
    /// <para>解密标准 OZip 文件到输出流。</para>
    /// Decrypts a standard OZip file to an output stream.
    /// </summary>
    /// <param name="input"><para>输入 OZip 文件流，必须可读取和定位。</para>The input OZip file stream, must be readable and seekable.</param>
    /// <param name="output"><para>输出解密数据流。</para>The output decrypted data stream.</param>
    /// <param name="aesKey"><para>AES-128 密钥（16 字节）。</para>The AES-128 key (16 bytes).</param>
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
    /// <para>自动检测密钥并解密 OZip 文件到输出流。</para>
    /// Automatically detects the key and decrypts an OZip file to an output stream.
    /// </summary>
    /// <param name="input"><para>输入 OZip 文件流，必须可读取和定位。</para>The input OZip file stream, must be readable and seekable.</param>
    /// <param name="output"><para>输出解密数据流。</para>The output decrypted data stream.</param>
    /// <returns><para>检测到的密钥条目；若无法检测则抛出异常。</para>The detected key entry; throws an exception if detection fails.</returns>
    public static OzipKeyEntry DecryptFileWithAutoDetection(Stream input, Stream output)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        var header = OzipHeader.ReadFrom(input);

        // Read first 16 encrypted bytes for key detection
        input.Position = DataOffset;
        var firstEncrypted = new byte[EncryptedSize];
        var bytesRead = FillBuffer(input, firstEncrypted, EncryptedSize);
        if (bytesRead != EncryptedSize)
        {
            throw new OppDecryptionException(
                "Unable to read the first 16 encrypted bytes for key detection.");
        }

        var keyEntry = DetectKey(firstEncrypted) ?? throw new OppDecryptionException(
                "Unable to automatically detect the decryption key. No known key produces valid plaintext.");

        // Reset to data offset and decrypt
        input.Position = DataOffset;
        Decrypt(input, output, keyEntry.KeyBytes, header.EncryptedDataSize);

        return keyEntry;
    }

    /// <summary>
    /// <para>检查解密后的数据是否为有效的明文（以已知魔数开头）。</para>
    /// Checks whether the decrypted data is valid plaintext (starts with known magic bytes).
    /// </summary>
    /// <param name="data"><para>解密后的数据（至少 4 字节）。</para>The decrypted data (at least 4 bytes).</param>
    /// <returns><para>若数据以 PK\x03\x04、AVB0 或 ANDR 开头返回 true；否则返回 false。</para>True if the data starts with PK\x03\x04, AVB0, or ANDR; otherwise, false.</returns>
    internal static bool IsValidPlaintext(byte[] data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        // Check for PK\x03\x04 (ZIP header)
        if (data[0] == ZipMagic[0] && data[1] == ZipMagic[1] &&
            data[2] == ZipMagic[2] && data[3] == ZipMagic[3])
        {
            return true;
        }

        // Check for AVB0
        if (data[0] == AvbMagic[0] && data[1] == AvbMagic[1] &&
            data[2] == AvbMagic[2] && data[3] == AvbMagic[3])
        {
            return true;
        }

        // Check for ANDR
        return data[0] == AndrMagic[0] && data[1] == AndrMagic[1] &&
            data[2] == AndrMagic[2] && data[3] == AndrMagic[3];
    }

    private static int FillBuffer(Stream stream, byte[] buffer, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var bytesRead = stream.Read(buffer, totalRead, count - totalRead);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }
}