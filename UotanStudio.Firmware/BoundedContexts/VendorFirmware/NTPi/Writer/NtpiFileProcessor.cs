using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Compression;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Crypto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Exceptions;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Helpers;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Writer;

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
        using (var hashing = new SharedKernel.Streams.HashingReadStream(source))
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
        using var hashing = new SharedKernel.Streams.HashingReadStream(source);
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