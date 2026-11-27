using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.VendorFirmware.Htc.Ruu;

/// <summary>Container variant carried by an RUU ROM ZIP.</summary>
/// <para>RUU ROM ZIP 的容器变体。</para>
internal enum RuuContainerKind
{
    /// <summary>Ordinary ZIP with no HTC framing.<para>没有 HTC 封装的普通 ZIP。</para></summary>
    PlainZip,

    /// <summary>ZIP preceded by a 0x100-byte signature prefix.<para>带 0x100 字节签名前缀的 ZIP。</para></summary>
    SignedZip,

    /// <summary>AES-encrypted ZIP carrying the Htc@egi$ header.<para>带 Htc@egi$ 头的 AES 加密 ZIP。</para></summary>
    EncryptedZip,

    /// <summary>LaR@eZip container holding up to eight ranges.<para>包含最多八段的 LaR@eZip 容器。</para></summary>
    LargeZip
}

/// <summary>One start/length range in a LaR@eZip container.</summary>
/// <para>LaR@eZip 容器中的一段起始/长度范围。</para>
/// <param name="Start">Absolute start offset.<para>绝对起始偏移。</para></param>
/// <param name="Length">Range length in bytes.<para>范围的字节长度。</para></param>
internal readonly record struct RuuRange(long Start, long Length);

/// <summary>Parsed RUU ROM ZIP header.</summary>
/// <para>已解析的 RUU ROM ZIP 头。</para>
/// <param name="Kind">Container variant.<para>容器变体。</para></param>
/// <param name="HeaderOffset">Offset where the magic was found.<para>发现魔数的偏移。</para></param>
/// <param name="KeymapIndex">HTTP-style keymap index from the AES header.<para>AES 头中的密钥映射索引。</para></param>
/// <param name="Chunks">Chunk exponent from the AES header.<para>AES 头中的分块指数。</para></param>
/// <param name="Mainver">Published main version string.<para>公布的主版本号。</para></param>
/// <param name="Ranges">Embedded ranges for LaR@eZip.<para>LaR@eZip 的内嵌范围。</para></param>
internal sealed record RuuHeader(RuuContainerKind Kind, long HeaderOffset, int KeymapIndex, int Chunks, string Mainver, IReadOnlyList<RuuRange> Ranges);

/// <summary>AES key and IV pair derived from the published RUU key tables.</summary>
/// <para>从公布的 RUU 密钥表派生的 AES 密钥与 IV 对。</para>
/// <param name="Key">16-byte AES key.<para>16 字节 AES 密钥。</para></param>
/// <param name="Iv">16-byte AES IV.<para>16 字节 AES IV。</para></param>
internal sealed record RuuKeys(byte[] Key, byte[] Iv);

/// <summary>
/// Parses the HTC RUU ROM ZIP family: plain ZIP, signature-prefixed ZIP, the AES-encrypted
/// Htc@egi$ container and the LaR@eZip multi-ZIP container.
/// <para>解析 HTC RUU 的 ROM ZIP 家族：普通 ZIP、带签名前缀的 ZIP、AES 加密的 Htc@egi$
/// 容器以及 LaR@eZip 多 ZIP 容器。</para>
/// </summary>
internal static class RuuFormat
{
    /// <summary>Length of the fixed HTC AES header.</summary>
    /// <para>HTC AES 固定头长度。</para>
    internal const int HeaderLength = 0x80;

    /// <summary>Length of the InstallShield signature prefix used by signed RUUs.</summary>
    /// <para>已签名 RUU 使用的 InstallShield 签名前缀长度。</para>
    internal const int SignaturePrefixLength = 0x100;

    /// <summary>AES key and IV length in bytes.</summary>
    /// <para>AES 密钥与 IV 的字节长度。</para>
    internal const int KeyLength = 16;

    private const int KeydataEntryLength = 0x60;
    private const int KeymapEntryLength = 32;
    private const int ReadBufferLength = 0x8000;

    internal static ReadOnlySpan<byte> HtcMagic => "Htc@egi$"u8;

    internal static ReadOnlySpan<byte> LargeZipMagic => "LaR@eZip"u8;

    internal static ReadOnlySpan<byte> PkMagic => "PK"u8;

    /// <summary>Reads the RUU header at offset zero or after the 0x100 signature prefix.</summary>
    /// <para>在偏移零处或 0x100 签名前缀之后读取 RUU 头。</para>
    internal static RuuHeader? ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[HeaderLength];
        foreach (var offset in new long[] { 0, SignaturePrefixLength })
        {
            if (!TryReadAt(stream, offset, buffer))
            {
                continue;
            }

            if (buffer[..HtcMagic.Length].SequenceEqual(HtcMagic))
            {
                var keymapIndex = BinaryPrimitives.ReadUInt16BigEndian(buffer[8..10]);
                var chunks = buffer[10];
                var mainver = ParseMainver(buffer[11..31]);
                return new RuuHeader(RuuContainerKind.EncryptedZip, offset, keymapIndex, chunks, mainver, []);
            }

            if (buffer[..LargeZipMagic.Length].SequenceEqual(LargeZipMagic))
            {
                var ranges = new List<RuuRange>();
                for (var i = 0; i < 8; i++)
                {
                    var start = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(8 + (i * 4))..]);
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(40 + (i * 4))..]);
                    if (length > 0)
                    {
                        ranges.Add(new RuuRange(start, length));
                    }
                }

                return new RuuHeader(RuuContainerKind.LargeZip, offset, 0, 0, string.Empty, ranges);
            }

            if (buffer[..PkMagic.Length].SequenceEqual(PkMagic))
            {
                return new RuuHeader(offset == 0 ? RuuContainerKind.PlainZip : RuuContainerKind.SignedZip, offset, 0, 0, string.Empty, []);
            }
        }

        return null;
    }

    /// <summary>Rejects offsets and lengths that fall outside the stream.</summary>
    /// <para>拒绝落在流范围之外的偏移与长度。</para>
    internal static void ValidateRange(long offset, long length, long streamLength, string description)
    {
        if (offset < 0 || length < 0 || offset > streamLength || length > streamLength - offset)
        {
            throw new InvalidDataException($"{description} exceeds the RUU stream bounds.");
        }
    }

    internal static int ChunkSize(byte chunks)
    {
        if (chunks > 20)
        {
            throw new InvalidDataException("The RUU chunk exponent is out of range.");
        }

        return 1 << (chunks + 20);
    }

    /// <summary>Derives the AES key and IV from a device keydata entry and keymap index.</summary>
    /// <para>从设备密钥数据条目与密钥映射索引派生 AES 密钥和 IV。</para>
    internal static RuuKeys DeriveKeys(int keydataIndex, int keymapIndex)
    {
        if (keydataIndex < 0 || (keydataIndex + 1) * KeydataEntryLength > RuuKeydata.Length)
        {
            throw new InvalidDataException("The RUU device key index is out of range.");
        }

        var keymapOffset = (keymapIndex - 1) * KeymapEntryLength;
        if (keymapOffset < 0 || keymapOffset + KeymapEntryLength > RuuKeymap.Length)
        {
            throw new InvalidDataException("The RUU keymap index is out of range.");
        }

        var keydata = RuuKeydata.Bytes.Slice(keydataIndex * KeydataEntryLength, KeydataEntryLength);
        var keymap = RuuKeymap.Bytes.Slice(keymapOffset, KeymapEntryLength);
        var key = new byte[KeyLength];
        var iv = new byte[KeyLength];
        for (var i = 0; i < KeyLength; i++)
        {
            key[i] = keydata[keymap[i]];
            iv[i] = keydata[keymap[i + KeyLength]];
        }

        return new RuuKeys(key, iv);
    }

    /// <summary>Finds a usable key by testing the first AES block against the ZIP magic.</summary>
    /// <para>通过检验首个 AES 块是否为 ZIP 魔数来寻找可用密钥。</para>
    internal static RuuKeys? FindKeys(Stream input, long dataOffset, byte chunks, int keydataCount)
    {
        var firstBlock = new byte[KeyLength];
        if (!TryReadAt(input, dataOffset, firstBlock))
        {
            return null;
        }

        for (var device = 0; device < keydataCount; device++)
        {
            for (var keymap = 1; keymap <= RuuKeymap.Length / KeymapEntryLength; keymap++)
            {
                var keys = DeriveKeys(device, keymap);
                var probe = (byte[])firstBlock.Clone();
                DecryptBlock(probe, keys);
                if (probe.AsSpan(0, PkMagic.Length).SequenceEqual(PkMagic))
                {
                    return keys;
                }
            }
        }

        return null;
    }

    /// <summary>Decrypts one aligned AES-CBC block in place.</summary>
    /// <para>就地解密一个对齐的 AES-CBC 块。</para>
    internal static void DecryptBlock(byte[] block, RuuKeys keys)
    {
        using var aes = Aes.Create();
        aes.Key = keys.Key;
        aes.IV = keys.Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        decryptor.TransformBlock(block, 0, block.Length, block, 0);
    }

    /// <summary>Streams AES-CBC decryption with per-chunk IV resets into <paramref name="output"/>.</summary>
    /// <para>流式 AES-CBC 解密，每个分块重置 IV，结果写入 <paramref name="output"/>。</para>
    internal static void Decrypt(Stream input, Stream output, long length, byte chunks, RuuKeys keys,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ValidateRange(input.Position, length, input.Length, "RUU encrypted payload");
        var chunkSize = ChunkSize(chunks);
        var buffer = new byte[ReadBufferLength];
        long remaining = length;
        long processed = 0;
        long chunkRemaining = chunkSize;

        using var aes = Aes.Create();
        aes.Key = keys.Key;
        aes.IV = keys.Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunkRemaining == 0)
            {
                chunkRemaining = chunkSize;
                aes.IV = keys.Iv;
            }

            var wanted = (int)Math.Min(Math.Min(buffer.Length, remaining), chunkRemaining);
            var read = input.Read(buffer, 0, wanted);
            if (read == 0)
            {
                throw new InvalidDataException("The RUU encrypted payload ended unexpectedly.");
            }

            var aligned = read - (read % KeyLength);
            if (aligned > 0)
            {
                var lastCipherBlock = new byte[KeyLength];
                Array.Copy(buffer, aligned - KeyLength, lastCipherBlock, 0, KeyLength);
                using var transform = aes.CreateDecryptor();
                transform.TransformBlock(buffer, 0, aligned, buffer, 0);
                aes.IV = lastCipherBlock;
            }

            output.Write(buffer, 0, read);
            processed += read;
            remaining -= read;
            chunkRemaining -= read;
            progress?.Report(processed);
        }
    }

    private static string ParseMainver(ReadOnlySpan<byte> data)
    {
        var start = 0;
        while (start < data.Length && (data[start] == 0xFF || data[start] == 0))
        {
            start++;
        }

        var end = start;
        while (end < data.Length && data[end] != 0xFF && data[end] != 0)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(data[start..end]);
    }

    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
        {
            return false;
        }

        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}