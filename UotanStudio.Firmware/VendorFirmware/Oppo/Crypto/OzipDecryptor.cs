using System.Security.Cryptography;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;

/// <summary>
/// Decrypts OZip payloads. The encrypted region is a sequence of units, each a 16-byte AES-128-ECB
/// encrypted prefix block followed by up to 0x4000 stored plaintext bytes; the decrypted output
/// concatenates each prefix block's plaintext with its following plaintext run.
/// <para>解密 OZip 负载。加密区域由若干单元顺序组成：每个单元是一个 16 字节的 AES-128-ECB 加密前缀块，后随最多 0x4000
/// 字节的直存明文；解密输出将每个前缀块的明文与其后续明文段依次拼接。</para>
/// </summary>
internal static class OzipDecryptor
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
    /// <param name="firstEncrypted16Bytes">
    /// The first 16-byte encrypted prefix block of the payload, taken from offset <c>0x1050</c> of a
    /// standard OZip file. <para>负载的第一个 16 字节加密前缀块，取自标准 OZip 文件的偏移 <c>0x1050</c> 处。</para>
    /// </param>
    /// <returns>
    /// The first built-in database key whose ECB decryption of the block starts with a known plaintext
    /// magic (<c>PK\x03\x04</c>, <c>AVB0</c>, or <c>ANDR</c>), or <see langword="null"/> when no built-in
    /// key matches. Custom keys supplied to providers are not consulted here.
    /// <para>内置密钥库中第一个能使 ECB 解密结果以已知明文魔数（<c>PK\x03\x04</c>、<c>AVB0</c> 或 <c>ANDR</c>）开头的密钥；
    /// 没有内置密钥匹配时返回 <see langword="null"/>。此处不会使用提供给提供程序的自定义密钥。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="firstEncrypted16Bytes"/> is not exactly 16 bytes long.
    /// <para><paramref name="firstEncrypted16Bytes"/> 的长度不是 16 字节。</para></exception>
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
    /// <param name="input">
    /// Readable stream positioned at the first encrypted unit; the header region is not read here and
    /// the stream is neither repositioned nor closed.
    /// <para>定位在第一个加密单元起始处的可读流；此处不读取头部区域，也不会移动或关闭该流。</para>
    /// </param>
    /// <param name="output">
    /// Writable stream that receives each unit's decrypted prefix block followed by its plaintext run; it
    /// is not closed. <para>接收每个单元解密前缀块及其后明文段的可写流；不会被关闭。</para>
    /// </param>
    /// <param name="aesKey">The 16-byte AES-128 key used for the ECB prefix-block decryption.
    /// <para>用于 ECB 前缀块解密的 16 字节 AES-128 密钥。</para></param>
    /// <param name="encryptedDataSize">Byte length of the encrypted region to consume from <paramref name="input"/>.
    /// <para>需要从 <paramref name="input"/> 读取的加密区域字节长度。</para></param>
    /// <exception cref="ArgumentException"><paramref name="aesKey"/> is not exactly 16 bytes long.
    /// <para><paramref name="aesKey"/> 的长度不是 16 字节。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="encryptedDataSize"/> is negative.
    /// <para><paramref name="encryptedDataSize"/> 为负数。</para></exception>
    /// <exception cref="InvalidOppFormatException">
    /// The input ends before <paramref name="encryptedDataSize"/> bytes are consumed, or the region ends
    /// with an incomplete 16-byte encrypted prefix.
    /// <para>输入流在消费满 <paramref name="encryptedDataSize"/> 字节前提前结束，或区域末尾存在不完整的 16 字节加密前缀。</para>
    /// </exception>
    /// <exception cref="CryptographicException">The ECB decryption of a prefix block did not produce a full
    /// 16-byte block. <para>某个前缀块的 ECB 解密未产生完整的 16 字节块。</para></exception>
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
        var aggregate = new byte[EncryptedSize + PlaintextBlockSize];

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
            Buffer.BlockCopy(decryptedBuffer, 0, aggregate, 0, EncryptedSize);
            remaining -= EncryptedSize;

            var plaintextToRead = (int)Math.Min(PlaintextBlockSize, remaining);
            if (plaintextToRead > 0)
            {
                ReadExactly(input, plaintextBuffer, plaintextToRead);
                Buffer.BlockCopy(plaintextBuffer, 0, aggregate, EncryptedSize, plaintextToRead);
                remaining -= plaintextToRead;
            }

            output.Write(aggregate, 0, EncryptedSize + plaintextToRead);
        }
    }

    /// <summary>
    /// Decrypts a standard OZip file to an output stream.
    /// <para>解密标准 OZip 文件到输出流。</para>
    /// </summary>
    /// <param name="input">
    /// Readable, seekable OZip file stream; the header is validated, then reading continues from offset
    /// <c>0x1050</c>. Neither stream is closed.
    /// <para>可读取、可寻址的 OZip 文件流；先校验头部，然后从偏移 <c>0x1050</c> 继续读取。两个流都不会被关闭。</para>
    /// </param>
    /// <param name="output">Stream that receives the decrypted payload; it is not closed.
    /// <para>接收解密负载的流；不会被关闭。</para></param>
    /// <param name="aesKey">The 16-byte AES-128 key for the payload.
    /// <para>用于该负载的 16 字节 AES-128 密钥。</para></param>
    /// <exception cref="ArgumentException"><paramref name="input"/> is not readable or not seekable, or
    /// <paramref name="aesKey"/> is not exactly 16 bytes long.
    /// <para><paramref name="input"/> 不可读取或不可寻址，或 <paramref name="aesKey"/> 的长度不是 16 字节。</para></exception>
    /// <exception cref="InvalidOppFormatException">The header is invalid, or the declared size exceeds the
    /// bytes actually present in <paramref name="input"/>.
    /// <para>头部无效，或声明的大小超出 <paramref name="input"/> 中实际存在的字节数。</para></exception>
    /// <exception cref="CryptographicException">The ECB decryption of a prefix block did not produce a full
    /// 16-byte block. <para>某个前缀块的 ECB 解密未产生完整的 16 字节块。</para></exception>
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
    /// <param name="input">Readable, seekable OZip file stream; it stays open and is advanced while copying.
    /// <para>可读取、可寻址的 OZip 文件流；复制过程中保持打开并向前推进。</para></param>
    /// <param name="output">Stream that receives the fully decrypted payload; it is not closed.
    /// <para>接收完整解密负载的流；不会被关闭。</para></param>
    /// <returns>The key entry whose decryption of the first prefix block produced valid plaintext.
    /// <para>对第一个前缀块解密后得到有效明文的密钥项。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="input"/> is not readable or not seekable.
    /// <para><paramref name="input"/> 不可读取或不可寻址。</para></exception>
    /// <exception cref="InvalidOppFormatException">The header is invalid or the declared data size does not
    /// fit between offset <c>0x1050</c> and the end of <paramref name="input"/>.
    /// <para>头部无效，或声明的数据大小超出偏移 <c>0x1050</c> 到 <paramref name="input"/> 末尾的范围。</para></exception>
    /// <exception cref="OppDecryptionException">No built-in key decrypts the first prefix block to a known
    /// plaintext magic. <para>没有任何内置密钥能将第一个前缀块解密为已知明文魔数。</para></exception>
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