using System.Text;
using UotanStudio.Firmware.Common.Streams;
using UotanStudio.Firmware.NTPi.Compression;
using UotanStudio.Firmware.NTPi.Crypto;
using UotanStudio.Firmware.NTPi.Exceptions;
using UotanStudio.Firmware.NTPi.Helpers;
using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Writer;

/// <summary>
/// <para>表示单个文件在 Region6 中的布局与编码参数。</para>
/// Represents the Region6 layout and encoding parameters of a single file.
/// </summary>
/// <remarks>
/// <para>解析/测量阶段仅记录偏移、长度与加密参数,不保留文件内容字节。</para>
/// Only offsets, lengths, and encryption parameters are recorded during measurement;
/// the file content bytes are never retained.
/// </remarks>
internal sealed class FilePlan
{
    /// <summary>原始文件条目。<para>The original file entry.</para></summary>
    public FileEntry Entry { get; init; } = null!;

    /// <summary>原始数据长度。<para>Original data length.</para></summary>
    public long OriginalLength { get; init; }

    /// <summary>压缩包总长度(NtDecompressHeader + 属性 + LZMA 数据)。<para>Total compressed package length (NtDecompressHeader + properties + LZMA data).</para></summary>
    public long PackageLength { get; init; }

    /// <summary>加密后长度(AES 块对齐)。<para>Encrypted length after AES block alignment.</para></summary>
    public long EncryptedLength { get; init; }

    /// <summary>SHA-256 十六进制哈希。<para>SHA-256 hex hash.</para></summary>
    public string Sha256Hash { get; init; } = string.Empty;

    /// <summary>密钥索引。<para>Key index.</para></summary>
    public int KeyIndex { get; init; }

    /// <summary>Region6 内偏移。<para>Offset within Region6.</para></summary>
    public long Offset { get; set; }
}

/// <summary>
/// <para>负责 NTPI 打包过程中文件的压缩与加密处理。</para>
/// Handles file compression and encryption during NTPI packing.
/// </summary>
/// <remarks>
/// <para>使用指定的加密提供者和压缩器初始化 NtpiFileProcessor 的新实例。</para>
/// Initializes a new instance of NtpiFileProcessor with the specified crypto provider and compressor.
/// </remarks>
internal class NtpiFileProcessor(ICryptoProvider cryptoProvider, ILzma2Compressor compressor)
{
    private const int NtEncodeHeaderSize = 112;
    private static readonly byte[] NtEncodeMagic = Encoding.ASCII.GetBytes("NTENCODE");
    private static readonly byte[] ZeroKey32 = new byte[32];
    private static readonly byte[] ZeroIv32 = new byte[16];

    private readonly ICryptoProvider _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));
    private readonly ILzma2Compressor _compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));

    /// <summary>
    /// <para>第一遍扫描:流式计算单文件哈希并测量压缩/加密后的长度。</para>
    /// First pass: stream-hashes a single file and measures its compressed/encrypted length.
    /// </summary>
    public FilePlan MeasureFile(FileEntry entry, Stream source, long originalLength, int keyIndex)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(originalLength);

        if (originalLength == 0)
        {
            if (source.ReadByte() != -1)
            {
                throw new IOException($"Source file '{entry.Name}' changed while the package was being built.");
            }
            return new FilePlan
            {
                Entry = entry,
                OriginalLength = 0,
                PackageLength = 0,
                EncryptedLength = 0,
                Sha256Hash = StructHelper.ComputeSha256Hex([]),
                KeyIndex = keyIndex
            };
        }

        var packageCounter = new CountingWriteStream(Stream.Null, long.MaxValue);
        string hash;
        long packageLength;
        using (var hashing = new Common.Streams.HashingReadStream(source))
        {
            _compressor.Compress(hashing, packageCounter, (ulong)originalLength);
            hash = hashing.GetHashHex();
        }

        packageLength = packageCounter.BytesWritten;
        return new FilePlan
        {
            Entry = entry,
            OriginalLength = originalLength,
            PackageLength = packageLength,
            EncryptedLength = NtpiPaddingHelper.CalculatePaddedSize(packageLength),
            Sha256Hash = hash,
            KeyIndex = keyIndex
        };
    }

    /// <summary>
    /// <para>第二遍写入:将单文件逐文件编码(压缩+加密)并流式写入 Region6。</para>
    /// Second-pass write: encodes a single file (compress + encrypt) and streams it into Region6.
    /// </summary>
    public void EncodeFile(FilePlan plan, Stream source, Stream output, KeyMapKeyProvider keyMapProvider, byte[] zeroIv)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(keyMapProvider);
        ArgumentNullException.ThrowIfNull(zeroIv);

        if (plan.OriginalLength == 0)
        {
            if (source.ReadByte() != -1)
            {
                throw new IOException($"Source file '{plan.Entry.Name}' changed while the package was being built.");
            }
            return;
        }

        var blockKey = keyMapProvider.GetKey(plan.KeyIndex, 0);

        var encodeHeader = new NtEncodeHeader
        {
            Magic = NtEncodeMagic,
            PrimaryType = 1,
            CompressSubtype = 1,
            EncryptSubtype = 1,
            Padding = 0,
            ProcessedSize = (ulong)plan.EncryptedLength,
            OriginalSize = (ulong)plan.EncryptedLength,
            Key = ZeroKey32,
            Iv = ZeroIv32,
            KeySize = 32,
            IvSize = 16
        };

        var encodeHeaderBuffer = new byte[NtEncodeHeaderSize];
        StructHelper.WriteNtEncodeHeader(encodeHeader, encodeHeaderBuffer);
        output.Write(encodeHeaderBuffer, 0, NtEncodeHeaderSize);

        var encryptedCounter = new CountingWriteStream(output, long.MaxValue, leaveOpen: true);
        using var hashing = new Common.Streams.HashingReadStream(source);
        using (var encrypt = _cryptoProvider.CreateEncryptStream(encryptedCounter, blockKey, zeroIv, leaveOpen: true))
        {
            _compressor.Compress(hashing, encrypt, (ulong)plan.OriginalLength);

            // AES-CBC (PaddingMode.None) requires block-aligned input; append zero padding.
            var padding = (int)((NtpiPaddingHelper.AesBlockSize - (plan.PackageLength % NtpiPaddingHelper.AesBlockSize)) % NtpiPaddingHelper.AesBlockSize);
            for (var i = 0; i < padding; i++)
            {
                encrypt.WriteByte(0);
            }
        }

        var secondPassHash = hashing.GetHashHex();
        if (hashing.BytesRead != plan.OriginalLength ||
            !string.Equals(secondPassHash, plan.Sha256Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Source file '{plan.Entry.Name}' changed while the package was being built.");
        }
        if (encryptedCounter.BytesWritten != plan.EncryptedLength)
        {
            throw new Lzma2CompressionException(
                $"Encrypted length mismatch for '{plan.Entry.Name}': expected {plan.EncryptedLength} bytes, wrote {encryptedCounter.BytesWritten} bytes.");
        }
    }
}