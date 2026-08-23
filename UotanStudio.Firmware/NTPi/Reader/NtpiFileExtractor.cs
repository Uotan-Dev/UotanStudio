using System.Buffers;
using UotanStudio.Firmware.NTPi.Compression;
using UotanStudio.Firmware.NTPi.Crypto;
using UotanStudio.Firmware.NTPi.Exceptions;
using UotanStudio.Firmware.NTPi.Helpers;
using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Reader;

/// <summary>
/// <para>负责从 NTPI 固件包的 Region6 中提取和解密单个文件。</para>
/// Responsible for extracting and decrypting individual files from Region6 of NTPI firmware packages.
/// </summary>
/// <remarks>
/// <para>使用指定的加密提供者和压缩器初始化 NtpiFileExtractor 的新实例。</para>
/// Initializes a new instance of NtpiFileExtractor with the specified crypto provider and compressor.
/// </remarks>
internal class NtpiFileExtractor(ICryptoProvider cryptoProvider, ILzma2Compressor compressor)
{
    private readonly ICryptoProvider _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));
    private readonly ILzma2Compressor _compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));

    /// <summary>
    /// <para>从 Region6 数据中提取指定文件条目的原始数据。</para>
    /// Extracts the raw data for the specified file entry from Region6 data.
    /// </summary>
    public byte[] ExtractFileFromRegion6(byte[] region6Data, int region6Offset, KeyMapKeyProvider keyMapProvider, FileEntry entry)
    {
        long totalDecompressedSize = Math.Max((int)entry.OriginalLength, (int)entry.Length);
        var outputBuffer = ArrayPool<byte>.Shared.Rent((int)totalDecompressedSize);
        try
        {
            var outputOffset = 0;
            var currentOffset = region6Offset + entry.Offset;
            var endOffset = region6Offset + entry.Offset + entry.Length;
            var blockIdx = 0;
            const int ntEncodeHeaderSize = 112;
            var zeroIv = KeyMapKeyProvider.GetZeroIv();

            var blockKeyBuffer = new byte[32];
            while (currentOffset < endOffset)
            {
                if (currentOffset + ntEncodeHeaderSize > region6Data.Length)
                {
                    throw new InvalidFormatException(
                        $"NtEncodeHeader for file '{entry.Name}' at offset {currentOffset} exceeds Region6 boundaries.");
                }

                if (!StructHelper.IsNtEncodeMagic(region6Data, (int)currentOffset))
                {
                    throw new InvalidFormatException(
                        $"Invalid NTENCODE header at offset {currentOffset} in Region6 for file '{entry.Name}'.");
                }

                keyMapProvider.GetKey(entry.KeyIndex, blockIdx, blockKeyBuffer);

                var encryptedSize = (long)StructHelper.ReadNtEncodeOriginalSize(region6Data, (int)currentOffset);
                if (currentOffset + ntEncodeHeaderSize + encryptedSize > region6Data.Length)
                {
                    throw new InvalidFormatException(
                        $"Encrypted data for file '{entry.Name}' at offset {currentOffset} exceeds Region6 boundaries.");
                }

                var encryptedSizeInt = (int)encryptedSize;
                var encryptedData = ArrayPool<byte>.Shared.Rent(encryptedSizeInt);
                try
                {
                    Buffer.BlockCopy(region6Data, (int)currentOffset + ntEncodeHeaderSize, encryptedData, 0, encryptedSizeInt);

                    var dataToDecrypt = encryptedSizeInt == encryptedData.Length
                        ? encryptedData
                        : encryptedData.AsSpan(0, encryptedSizeInt).ToArray();
                    var decrypted = _cryptoProvider.Decrypt(dataToDecrypt, blockKeyBuffer, zeroIv);
                    var decompressed = _compressor.Decompress(decrypted);

                    if (outputOffset + decompressed.Length > outputBuffer.Length)
                    {
                        var newSize = Math.Max(outputBuffer.Length * 2, outputOffset + decompressed.Length);
                        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
                        Buffer.BlockCopy(outputBuffer, 0, newBuffer, 0, outputOffset);
                        ArrayPool<byte>.Shared.Return(outputBuffer);
                        outputBuffer = newBuffer;
                    }

                    Buffer.BlockCopy(decompressed, 0, outputBuffer, outputOffset, decompressed.Length);
                    outputOffset += decompressed.Length;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(encryptedData);
                }

                currentOffset += ntEncodeHeaderSize + encryptedSize;
                blockIdx++;
            }

            var result = new byte[outputOffset];
            Buffer.BlockCopy(outputBuffer, 0, result, 0, outputOffset);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }

    /// <summary>
    /// <para>验证提取的文件数据的 SHA-256 哈希值。</para>
    /// Verifies the SHA-256 hash of extracted file data.
    /// </summary>
    /// <returns>哈希匹配时返回 true,否则抛出异常。</returns>
    public void VerifyHash(FileEntry entry, byte[] data)
    {
        var computed = StructHelper.ComputeSha256Hex(data);
        if (!string.IsNullOrEmpty(entry.FileSha256Hash) &&
            !string.Equals(computed, entry.FileSha256Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IntegrityVerificationException(
                $"SHA-256 hash mismatch for file '{entry.Name}': expected {entry.FileSha256Hash}, got {computed}.");
        }
    }
}