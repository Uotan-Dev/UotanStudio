using System.Security.Cryptography;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.NTPi;
using UotanStudio.Firmware.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.VendorFirmware.NTPi;

internal class NtpiFileExtractor(ICryptoProvider cryptoProvider, ILzma2Compressor compressor)
{
    private const int NtEncodeHeaderSize = 112;
    private readonly ICryptoProvider _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));
    private readonly ILzma2Compressor _compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));

    /// <summary>
    /// Extracts the raw data for the specified file entry into an in-memory byte array.
    /// <para>将指定文件条目的原始数据提取为内存字节数组。</para>
    /// </summary>
    /// <param name="source">
    /// Seekable NTPI file stream supplying Region6 bytes; its position moves during extraction.
    /// <para>提供 Region6 数据的可寻址 NTPI 文件流；提取过程中其位置会移动。</para>
    /// </param>
    /// <param name="region6Offset">
    /// Absolute byte offset of Region6 inside <paramref name="source"/>.
    /// <para>Region6 在 <paramref name="source"/> 内的绝对字节偏移。</para>
    /// </param>
    /// <param name="region6Length">
    /// Byte length of the Region6 range inside <paramref name="source"/>.
    /// <para>Region6 在 <paramref name="source"/> 内的字节长度。</para>
    /// </param>
    /// <param name="keyMapProvider">
    /// Provider deriving per-block AES-256 keys from the key-map blob for <c>entry.KeyIndex</c> and the block index.
    /// <para>按 <c>entry.KeyIndex</c> 与块索引从 KeyMap 数据派生每块 AES-256 密钥的提供者。</para>
    /// </param>
    /// <param name="entry">
    /// File entry describing the Region6-relative offset, encrypted and original lengths, key index and SHA-256 hash.
    /// <para>描述 Region6 内相对偏移、密文与原始长度、密钥索引及 SHA-256 哈希的文件条目。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed between extraction blocks; the default (none) token cannot cancel.
    /// <para>在解压块之间检查的取消令牌；默认（无）令牌不可取消。</para>
    /// </param>
    public byte[] ExtractFileToMemory(Stream source, long region6Offset, long region6Length, KeyMapKeyProvider keyMapProvider, FileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var initialCapacity = (int)Math.Min(Math.Max(entry.OriginalLength, 0), 64 * 1024);
        using var output = new MemoryStream(initialCapacity);
        ExtractFileToStream(source, region6Offset, region6Length, keyMapProvider, entry, output, cancellationToken: cancellationToken);
        return output.ToArray();
    }

    /// <summary>
    /// Streams a file from Region6 block by block: FileStream→decrypt stream→bounded decoder→output stream.
    /// <para>按块从 Region6 流式提取文件:FileStream→解密流→有界解码→输出流。</para>
    /// </summary>
    /// <param name="source">
    /// Seekable NTPI file stream supplying Region6 bytes; its position moves during extraction.
    /// <para>提供 Region6 数据的可寻址 NTPI 文件流；提取过程中其位置会移动。</para>
    /// </param>
    /// <param name="region6Offset">
    /// Absolute byte offset of Region6 inside <paramref name="source"/>.
    /// <para>Region6 在 <paramref name="source"/> 内的绝对字节偏移。</para>
    /// </param>
    /// <param name="region6Length">
    /// Byte length of the Region6 range inside <paramref name="source"/>.
    /// <para>Region6 在 <paramref name="source"/> 内的字节长度。</para>
    /// </param>
    /// <param name="keyMapProvider">
    /// Provider deriving per-block AES-256 keys from the key-map blob for <c>entry.KeyIndex</c> and the block index.
    /// <para>按 <c>entry.KeyIndex</c> 与块索引从 KeyMap 数据派生每块 AES-256 密钥的提供者。</para>
    /// </param>
    /// <param name="entry">
    /// File entry describing the Region6-relative offset, encrypted and original lengths, key index and SHA-256 hash.
    /// <para>描述 Region6 内相对偏移、密文与原始长度、密钥索引及 SHA-256 哈希的文件条目。</para>
    /// </param>
    /// <param name="output">
    /// Destination stream receiving the decoded bytes; it stays open and its position advances with each written block.
    /// <para>接收解码字节的输出流；保持打开，其位置随每个写入的块前进。</para>
    /// </param>
    /// <param name="trustedOutputLimit">
    /// Maximum total bytes that may be written to <paramref name="output"/>, bounding untrusted declared lengths; the default allows any size.
    /// <para>可写入 <paramref name="output"/> 的最大总字节数，用于约束不可信的声明长度；默认值表示不限制。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed between extraction blocks; the default (none) token cannot cancel.
    /// <para>在解压块之间检查的取消令牌；默认（无）令牌不可取消。</para>
    /// </param>
    public void ExtractFileToStream(Stream source, long region6Offset, long region6Length, KeyMapKeyProvider keyMapProvider, FileEntry entry, Stream output, long trustedOutputLimit = long.MaxValue, CancellationToken cancellationToken = default)
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
        using var hashing = new Core.Streams.HashingWriteStream(lengthBound, leaveOpen: true);

        while (blockPos < entry.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    /// Verifies the SHA-256 hash of extracted file data.
    /// <para>验证提取的文件数据的 SHA-256 哈希值。</para>
    /// </summary>
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