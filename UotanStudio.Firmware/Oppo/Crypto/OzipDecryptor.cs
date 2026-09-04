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
/// OZip 加密算法先加密 16 字节前缀（AES-ECB），再保留随后的 0x4000 (16384) 字节明文，
/// 因此每个加密单元的总步长为 0x4010。解密时需要通过尝试已知密钥来检测正确的密钥，
/// 有效的密钥解密后会得到以 PK\x03\x04（ZIP 头）、AVB0 或 ANDR 开头的数据。
/// </para>
/// The OZip encryption algorithm encrypts a 16-byte AES prefix, then leaves the following
/// 0x4000 (16384) bytes as plaintext. Each encryption unit is therefore 0x4010 bytes. Decryption requires
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
    /// <para>每个加密前缀之后的明文块大小（0x4000 = 16384 字节）。</para>
    /// The plaintext block size after each encrypted prefix (0x4000 = 16384 bytes).
    /// </summary>
    internal const int PlaintextBlockSize = 0x4000;

    /// <summary>
    /// <para>每个块中加密部分的大小（16 字节，AES 块大小）。</para>
    /// The size of the encrypted portion within each block (16 bytes, AES block size).
    /// </summary>
    internal const int EncryptedSize = 16;

    /// <summary>
    /// <para>一个完整加密单元的步长（16 字节密文 + 0x4000 字节明文）。</para>
    /// The stride of one encryption unit (16 encrypted bytes plus 0x4000 plaintext bytes).
    /// </summary>
    internal const int EncryptionUnitSize = EncryptedSize + PlaintextBlockSize;

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

            // Mode 1 stores 0x4000 plaintext bytes after every 16-byte encrypted prefix.
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