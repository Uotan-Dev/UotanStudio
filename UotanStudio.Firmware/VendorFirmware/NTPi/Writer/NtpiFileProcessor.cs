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

    /// <summary>Encodes a single file into a seekable spool in one pass: compresses, encrypts, hashes the plaintext and measures both lengths while the bytes are read only once.
    /// <para>单遍把单个文件编码进可寻址的暂存流：在只读取一次字节的同时压缩、加密、计算明文哈希并度量两个长度。</para></summary>
    /// <param name="entry">File entry describing this file; it is stored in the returned plan and its name is used in failure messages.
    /// <para>描述该文件的条目；原样存入返回的计划，其名称用于失败信息。</para></param>
    /// <param name="source">Readable stream positioned at the first plaintext byte; it is consumed once by the compressor and left open. A zero-length plan requires the stream to hold no further byte.
    /// <para>定位在首个明文字节处的可读流；只由压缩器读取一次，读完仍保持打开。长度为零的计划要求流中不再有字节。</para></param>
    /// <param name="originalLength">Expected plaintext byte count; negative values are rejected and zero produces the empty-file plan with no spool bytes and the SHA-256 of no input bytes.
    /// <para>预期明文字节数；负值会被拒绝，为 0 时产生不写暂存字节、使用空输入 SHA-256 的空文件计划。</para></param>
    /// <param name="spool">Seekable destination receiving the 112-byte <c>NTENCODE</c> header followed by the padded ciphertext; it is neither flushed to disk nor closed by this call, and its position is left at the end of the written block.
    /// <para>接收 112 字节 <c>NTENCODE</c> 头部及其后补齐密文的可寻址目标；本调用不负责刷盘或关闭该流，位置停在写入块的末尾。</para></param>
    /// <param name="keyMapProvider">Provider resolving the AES key for <paramref name="keyIndex"/> and block index 0, which is the key actually used for the whole block.
    /// <para>为 <paramref name="keyIndex"/> 与块索引 0 解析 AES 密钥的提供者，该密钥即整块实际使用的密钥。</para></param>
    /// <param name="zeroIv">Initialization vector for the CBC encryption of the block; the writer supplies an all-zero vector, and the header's own key and IV fields are written as zeros regardless of this array's contents.
    /// <para>该块 CBC 加密使用的初始化向量；写入器提供全零向量，而头部自带的密钥与 IV 字段无论本数组内容如何都写为零。</para></param>
    /// <param name="keyIndex">Key-map index recorded in the plan and used to derive the block key.
    /// <para>记录到计划中并用于派生块密钥的密钥表索引。</para></param>
    /// <returns>A plan holding the entry, the original, compressed and AES-padded lengths, the lowercase SHA-256 hex of the plaintext and the key index; its <c>Offset</c> is left for the layout stage to fill and its spool length equals the entry length written to the index.
    /// <para>返回的计划包含该条目、明文/压缩/AES 填充后长度、明文的小写 SHA-256 十六进制摘要与密钥索引；其 <c>Offset</c> 留给布局阶段填写，其暂存长度等于写入索引的条目长度。</para></returns>
    public FilePlan EncodeToSpool(FileEntry entry, Stream source, long originalLength, Stream spool,
        KeyMapKeyProvider keyMapProvider, byte[] zeroIv, int keyIndex)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(keyMapProvider);
        ArgumentNullException.ThrowIfNull(zeroIv);
        ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
        if (!spool.CanSeek || !spool.CanWrite)
        {
            throw new ArgumentException("The spool must be writable and seekable.", nameof(spool));
        }

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

        var blockKey = keyMapProvider.GetKey(keyIndex, 0);
        var spoolStart = spool.Position;
        spool.Write(new byte[NtEncodeHeaderSize]);

        long packageLength;
        long encryptedLength;
        string hash;
        using (var hashing = new HashingReadStream(source, leaveOpen: true))
        {
            var cipherCounter = new CountingWriteStream(spool, long.MaxValue, leaveOpen: true);
            using (var encrypt = _cryptoProvider.CreateEncryptStream(cipherCounter, blockKey, zeroIv, leaveOpen: true))
            {
                var plaintextCounter = new CountingWriteStream(encrypt, long.MaxValue, leaveOpen: true);
                _compressor.Compress(hashing, plaintextCounter, (ulong)originalLength);
                packageLength = plaintextCounter.BytesWritten;

                var padding = (int)((NtpiPaddingHelper.AesBlockSize - (packageLength % NtpiPaddingHelper.AesBlockSize)) % NtpiPaddingHelper.AesBlockSize);
                for (var i = 0; i < padding; i++)
                {
                    encrypt.WriteByte(0);
                }
            }

            encryptedLength = cipherCounter.BytesWritten;
            hash = hashing.GetHashHex();
            if (hashing.BytesRead != originalLength)
            {
                throw new IOException($"Source file '{entry.Name}' changed while the package was being built.");
            }
        }

        var expectedEncryptedLength = NtpiPaddingHelper.CalculatePaddedSize(packageLength);
        if (encryptedLength != expectedEncryptedLength || spool.Position - spoolStart != NtEncodeHeaderSize + encryptedLength)
        {
            throw new Lzma2CompressionException(
                $"Encrypted length mismatch for '{entry.Name}': expected {expectedEncryptedLength} bytes, wrote {encryptedLength} bytes.");
        }

        var encodeHeader = new NtEncodeHeader
        {
            Magic = NtEncodeMagic,
            PrimaryType = 1,
            CompressSubtype = 1,
            EncryptSubtype = 1,
            Padding = 0,
            ProcessedSize = (ulong)encryptedLength,
            OriginalSize = (ulong)encryptedLength,
            Key = ZeroKey32,
            Iv = ZeroIv32,
            KeySize = 32,
            IvSize = 16
        };

        var encodeHeaderBuffer = new byte[NtEncodeHeaderSize];
        StructHelper.WriteNtEncodeHeader(encodeHeader, encodeHeaderBuffer);
        var spoolEnd = spool.Position;
        spool.Position = spoolStart;
        spool.Write(encodeHeaderBuffer);
        spool.Position = spoolEnd;

        return new FilePlan
        {
            Entry = entry,
            OriginalLength = originalLength,
            PackageLength = packageLength,
            EncryptedLength = encryptedLength,
            Sha256Hash = hash,
            KeyIndex = keyIndex
        };
    }
}