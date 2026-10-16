using System.Text;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.VendorFirmware.NTPi.Writer;

internal sealed class FilePlan
{
    public FileEntry Entry { get; init; } = null!;

    public long OriginalLength { get; init; }

    public long PackageLength { get; init; }

    public long EncryptedLength { get; init; }

    public string Sha256Hash { get; init; } = string.Empty;

    public int KeyIndex { get; init; }

    public long Offset { get; set; }
}

internal class NtpiFileProcessor(ICryptoProvider cryptoProvider, ILzma2Compressor compressor)
{
    private const int NtEncodeHeaderSize = 112;
    private static readonly byte[] NtEncodeMagic = Encoding.ASCII.GetBytes("NTENCODE");
    private static readonly byte[] ZeroKey32 = new byte[32];
    private static readonly byte[] ZeroIv32 = new byte[16];

    private readonly ICryptoProvider _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));
    private readonly ILzma2Compressor _compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));

    /// <summary>
    /// First pass: stream-hashes a single file and measures its compressed/encrypted length.
    /// <para>第一遍扫描:流式计算单文件哈希并测量压缩/加密后的长度。</para>
    /// </summary>
    /// <param name="entry">
    /// File entry describing this file; it is stored in the returned plan and its name is used in failure messages.
    /// <para>描述该文件的条目；原样存入返回的计划，其名称用于失败信息。</para>
    /// </param>
    /// <param name="source">
    /// Readable stream positioned at the first plaintext byte; it is consumed while hashing and compressing and is left
    /// open. A zero-length plan requires the stream to hold no further byte.
    /// <para>定位在首个明文字节处的可读流；在计算哈希与压缩期间被读取，读完仍保持打开。长度为零的计划要求流中不再有字节。</para>
    /// </param>
    /// <param name="originalLength">
    /// Plaintext byte count handed to the compressor as the expected size; negative values are rejected and zero selects
    /// the empty-file plan with the SHA-256 of no input bytes.
    /// <para>交给压缩器作为预期大小的明文字节数；负值会被拒绝，为 0 时返回使用空输入 SHA-256 的空文件计划。</para>
    /// </param>
    /// <param name="keyIndex">
    /// Key-map index recorded in the plan; the value is only stored here and is resolved during the second pass.
    /// <para>记录到计划中的密钥表索引；本遍只存储该值，第二遍才据此取密钥。</para>
    /// </param>
    /// <returns>
    /// A plan holding the entry, the measured original, compressed and AES-padded lengths, the lowercase SHA-256 hex of
    /// the plaintext and the key index; its <c>Offset</c> is left for the layout stage to fill.
    /// <para>返回的计划包含该条目、实测的明文/压缩/AES 填充后长度、明文的小写 SHA-256 十六进制摘要与密钥索引；其 <c>Offset</c> 留给布局阶段填写。</para>
    /// </returns>
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
        using (var hashing = new HashingReadStream(source))
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
    /// Second-pass write: encodes a single file (compress + encrypt) and streams it into Region6.
    /// <para>第二遍写入:将单文件逐文件编码(压缩+加密)并流式写入 Region6。</para>
    /// </summary>
    /// <param name="plan">
    /// First-pass plan supplying the entry, the expected plaintext length, the compressed and padded lengths, the
    /// expected plaintext hash and the key index; a plan with a zero plaintext length writes no bytes.
    /// <para>第一遍生成的计划，提供条目、预期明文字节数、压缩后与填充后长度、预期的明文哈希和密钥索引；明文字节数为 0 的计划不写入任何字节。</para>
    /// </param>
    /// <param name="source">
    /// Readable stream positioned at the first plaintext byte; it is re-read and re-hashed here and must still match the
    /// plan, and it stays open afterwards.
    /// <para>定位在首个明文字节处的可读流；本遍重新读取并计算哈希，内容必须仍与计划一致，读完仍保持打开。</para>
    /// </param>
    /// <param name="output">
    /// Region6 destination: the 112-byte <c>NTENCODE</c> header is written first, then the padded ciphertext; the stream
    /// is not flushed or closed by this call.
    /// <para>Region6 目标流：先写入 112 字节的 <c>NTENCODE</c> 头部，随后写入补齐后的密文；本调用不负责刷新或关闭该流。</para>
    /// </param>
    /// <param name="keyMapProvider">
    /// Provider resolving the AES key for <c>plan.KeyIndex</c> and block index 0, which is the key actually used for the
    /// whole block.
    /// <para>为 <c>plan.KeyIndex</c> 与块索引 0 解析 AES 密钥的提供者，该密钥即整块实际使用的密钥。</para>
    /// </param>
    /// <param name="zeroIv">
    /// Initialization vector for the CBC encryption of the block; the writer supplies an all-zero vector, and the header's
    /// own key and IV fields are written as zeros regardless of this array's contents.
    /// <para>该块 CBC 加密使用的初始化向量；写入器提供全零向量，而头部自带的密钥与 IV 字段无论本数组内容如何都写为零。</para>
    /// </param>
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
        using var hashing = new HashingReadStream(source);
        using (var encrypt = _cryptoProvider.CreateEncryptStream(encryptedCounter, blockKey, zeroIv, leaveOpen: true))
        {
            _compressor.Compress(hashing, encrypt, (ulong)plan.OriginalLength);


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