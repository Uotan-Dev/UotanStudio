using System.Security.Cryptography;
using UotanStudio.Firmware.Common.Streams;
using UotanStudio.Firmware.NTPi.Compression;
using UotanStudio.Firmware.NTPi.Crypto;
using UotanStudio.Firmware.NTPi.Exceptions;
using UotanStudio.Firmware.NTPi.Helpers;
using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Reader;

/// <summary>
/// <para>负责从 NTPI 固件包的 Region6 中按块流式提取和解密单个文件。</para>
/// Responsible for extracting and decrypting individual files from Region6 of NTPI firmware packages.
/// </summary>
/// <remarks>
/// <para>使用指定的加密提供者和压缩器初始化 NtpiFileExtractor 的新实例。</para>
/// Initializes a new instance of NtpiFileExtractor with the specified crypto provider and compressor.
/// </remarks>
internal class NtpiFileExtractor(ICryptoProvider cryptoProvider, ILzma2Compressor compressor)
{
    private const int NtEncodeHeaderSize = 112;
    private readonly ICryptoProvider _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));
    private readonly ILzma2Compressor _compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));

    /// <summary>
    /// <para>将指定文件条目的原始数据提取为内存字节数组。</para>
    /// Extracts the raw data for the specified file entry into an in-memory byte array.
    /// </summary>
    public byte[] ExtractFileToMemory(Stream source, long region6Offset, long region6Length, KeyMapKeyProvider keyMapProvider, FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(source);
        var initialCapacity = (int)Math.Min(Math.Max(entry.OriginalLength, 0), 64 * 1024);
        using var output = new MemoryStream(initialCapacity);
        ExtractFileToStream(source, region6Offset, region6Length, keyMapProvider, entry, output);
        return output.ToArray();
    }

    /// <summary>
    /// <para>按块从 Region6 流式提取文件:FileStream→解密流→有界解码→输出流。</para>
    /// Streams a file from Region6 block by block: FileStream→decrypt stream→bounded decoder→output stream.
    /// </summary>
    /// <param name="source">可定位的 NTPI 文件流。<para>The seekable NTPI file stream.</para></param>
    /// <param name="region6Offset">Region6 在文件中的绝对偏移。<para>The absolute Region6 offset in the file.</para></param>
    /// <param name="region6Length">Region6 的长度。<para>The Region6 length.</para></param>
    /// <param name="keyMapProvider">Region6 块密钥提供者。<para>The Region6 block key provider.</para></param>
    /// <param name="entry">文件索引条目。<para>The file index entry.</para></param>
    /// <param name="output">解压结果输出流。<para>The output stream receiving the decompressed data.</para></param>
    public void ExtractFileToStream(Stream source, long region6Offset, long region6Length, KeyMapKeyProvider keyMapProvider, FileEntry entry, Stream output, long trustedOutputLimit = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keyMapProvider);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);

        if (entry.Offset < 0 || entry.Length < 0 || entry.OriginalLength < 0)
        {
            throw new InvalidFormatException($"File entry '{entry.Name}' has negative offset/length fields.");
        }
        if (trustedOutputLimit < 0 || entry.OriginalLength > trustedOutputLimit)
        {
            throw new InvalidFormatException($"File entry '{entry.Name}' exceeds its trusted output limit.");
        }
        if (region6Offset < 0 || region6Length < 0 || region6Offset > source.Length || region6Length > source.Length - region6Offset)
        {
            throw new InvalidFormatException("Region6 range exceeds the NTPI file boundaries.");
        }
        if (entry.Offset > region6Length || entry.Length > region6Length - entry.Offset)
        {
            throw new InvalidFormatException(
                $"File entry '{entry.Name}' (offset {entry.Offset}, length {entry.Length}) exceeds Region6 boundaries.");
        }
        if (entry.OriginalLength == 0 && entry.Length == 0)
        {
            var emptyHash = StructHelper.ComputeSha256Hex([]);
            if (!string.IsNullOrEmpty(entry.FileSha256Hash) &&
                !string.Equals(emptyHash, entry.FileSha256Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IntegrityVerificationException($"SHA-256 hash mismatch for empty file '{entry.Name}'.");
            }
            return;
        }
        if (entry.Length < NtEncodeHeaderSize)
        {
            throw new InvalidFormatException($"File entry '{entry.Name}' is shorter than an NTENCODE header ({NtEncodeHeaderSize} bytes).");
        }

        var region6End = checked(region6Offset + region6Length);
        var entryEnd = checked(region6Offset + entry.Offset + entry.Length);
        var zeroIv = KeyMapKeyProvider.GetZeroIv();
        var blockKeyBuffer = new byte[32];
        var encodeHeaderBuffer = new byte[NtEncodeHeaderSize];
        var blockPos = 0L;
        var blockIdx = 0;

        using var lengthBound = new CountingWriteStream(output, Math.Min(entry.OriginalLength, trustedOutputLimit), leaveOpen: true);
        using var hashing = new Common.Streams.HashingWriteStream(lengthBound, leaveOpen: true);

        while (blockPos < entry.Length)
        {
            var blockStart = checked(region6Offset + entry.Offset + blockPos);
            if (blockStart > entryEnd || NtEncodeHeaderSize > Math.Min(region6End, entryEnd) - blockStart)
            {
                throw new InvalidFormatException(
                    $"NTENCODE header for file '{entry.Name}' at offset {blockStart} exceeds Region6 boundaries.");
            }

            var encryptedSize = 0L;
            try
            {
                source.Position = blockStart;
                source.ReadExactly(encodeHeaderBuffer);
                if (!StructHelper.IsNtEncodeMagic(encodeHeaderBuffer))
                {
                    throw new InvalidFormatException(
                        $"Invalid NTENCODE header at offset {blockStart} in Region6 for file '{entry.Name}'.");
                }

                encryptedSize = (long)StructHelper.ReadNtEncodeOriginalSize(encodeHeaderBuffer);
                if (encryptedSize <= 0 || encryptedSize > Math.Min(region6End, entryEnd) - (blockStart + NtEncodeHeaderSize))
                {
                    throw new InvalidFormatException(
                        $"Encrypted data for file '{entry.Name}' at offset {blockStart} exceeds Region6 boundaries.");
                }

                keyMapProvider.GetKey(entry.KeyIndex, blockIdx, blockKeyBuffer);

                var declaredRemaining = entry.OriginalLength - lengthBound.BytesWritten;
                var trustedRemaining = trustedOutputLimit - lengthBound.BytesWritten;
                var remaining = Math.Min(declaredRemaining, trustedRemaining);
                using var encryptedRange = new BoundedReadStream(source, blockStart + NtEncodeHeaderSize, encryptedSize, leaveOpen: true);
                using var decrypt = _cryptoProvider.CreateDecryptStream(encryptedRange, blockKeyBuffer, zeroIv, leaveOpen: true);
                _compressor.Decompress(decrypt, hashing, remaining);
            }
            catch (Exception ex) when (ex is CryptographicException or EndOfStreamException or ArgumentOutOfRangeException)
            {
                throw new InvalidFormatException(
                    $"Failed to extract file '{entry.Name}': the encrypted or compressed data is truncated or corrupt.", ex);
            }

            blockPos += NtEncodeHeaderSize + encryptedSize;
            blockIdx++;
        }

        if (blockPos != entry.Length)
        {
            throw new InvalidFormatException(
                $"NTENCODE blocks for file '{entry.Name}' do not exactly fill its declared entry length.");
        }

        if (entry.OriginalLength > 0 && lengthBound.BytesWritten != entry.OriginalLength)
        {
            throw new IntegrityVerificationException(
                $"File '{entry.Name}' extracted length mismatch: expected {entry.OriginalLength} bytes, got {lengthBound.BytesWritten} bytes.");
        }

        var computed = hashing.GetHashHex();
        if (!string.IsNullOrEmpty(entry.FileSha256Hash) &&
            !string.Equals(computed, entry.FileSha256Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IntegrityVerificationException(
                $"SHA-256 hash mismatch for file '{entry.Name}': expected {entry.FileSha256Hash}, got {computed}.");
        }
    }

    /// <summary>
    /// <para>验证提取的文件数据的 SHA-256 哈希值。</para>
    /// Verifies the SHA-256 hash of extracted file data.
    /// </summary>
    /// <returns>哈希匹配时返回 true,否则抛出异常。</returns>
    public void VerifyHash(FileEntry entry, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(data);
        var computed = StructHelper.ComputeSha256Hex(data);
        if (!string.IsNullOrEmpty(entry.FileSha256Hash) &&
            !string.Equals(computed, entry.FileSha256Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IntegrityVerificationException(
                $"SHA-256 hash mismatch for file '{entry.Name}': expected {entry.FileSha256Hash}, got {computed}.");
        }
    }
}