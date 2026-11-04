using System.Buffers;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>
/// Which RC6 context of an encrypted IMAGEWTY container a key belongs to.
/// <para>加密 IMAGEWTY 容器中某个 RC6 上下文对应的密钥。</para>
/// </summary>
internal enum ImageWtyCipherScope
{
    /// <summary>Container header record (first 1024 bytes). <para>容器头记录（前 1024 字节）。</para></summary>
    Header,

    /// <summary>Each 1024-byte file header record. <para>每条 1024 字节文件头记录。</para></summary>
    FileHeader,

    /// <summary>Stored file payloads. <para>已存储的文件负载。</para></summary>
    Content
}

/// <summary>
/// RC6 decryption for encrypted IMAGEWTY containers. The three contexts share the 32-byte key
/// template <c>{filler} × 31 + last byte</c> with distinct fillers and terminators, and payloads are
/// decrypted block-wise, leaving a trailing partial block untouched exactly like the reference reader.
/// <para>加密 IMAGEWTY 容器的 RC6 解密。三个上下文共用 32 字节密钥模板 <c>{填充字节} × 31 + 末字节</c>，
/// 只是填充值与末字节不同；负载按分组解密，末尾不足一个分组的字节与参考读取器一致地保持原样。</para>
/// </summary>
internal static class AllwinnerImageWtyCrypto
{
    private const int KeyLength = 32;
    private const int BlockSize = 16;
    private const int TransferSize = 64 * 1024;

    private static readonly Lazy<Rc6> HeaderCipher = new(() => BuildCipher(0x00, (byte)'i'));
    private static readonly Lazy<Rc6> FileHeaderCipher = new(() => BuildCipher(0x01, (byte)'m'));
    private static readonly Lazy<Rc6> ContentCipher = new(() => BuildCipher(0x02, (byte)'g'));

    /// <summary>
    /// Decrypts every complete 16-byte block of the buffer in place with the requested context; a
    /// trailing partial block is left as read because the format never encrypts a partial block.
    /// <para>用指定上下文就地解密缓冲区中每个完整的 16 字节分组；末尾不足一个分组的字节保持原样，因为该格式不加密不完整分组。</para>
    /// </summary>
    /// <param name="scope">Context whose key is used. <para>使用其密钥的上下文。</para></param>
    /// <param name="data">Buffer to decrypt in place. <para>就地解密的缓冲区。</para></param>
    public static void DecryptBlocks(ImageWtyCipherScope scope, Span<byte> data)
    {
        var cipher = GetCipher(scope);
        for (var offset = 0; offset + BlockSize <= data.Length; offset += BlockSize)
        {
            cipher.DecryptBlock(data.Slice(offset, BlockSize));
        }
    }

    /// <summary>
    /// Streams a stored payload through the content cipher and writes at most
    /// <paramref name="copyLength"/> bytes, so an entry that declares a smaller original length is
    /// truncated instead of exposing padding.
    /// <para>让已存储负载经内容密钥流式解密，最多写出 <paramref name="copyLength"/> 字节，
    /// 因此声明了更小原始长度的条目会被截断而不会暴露填充数据。</para>
    /// </summary>
    /// <param name="source">Container stream positioned at the payload. <para>定位到负载的容器流。</para></param>
    /// <param name="destination">Destination receiving the decrypted bytes. <para>接收解密字节的目标流。</para></param>
    /// <param name="storedLength">Payload bytes to consume from <paramref name="source"/>. <para>从 <paramref name="source"/> 消费的负载字节数。</para></param>
    /// <param name="copyLength">Bytes actually written; never above <paramref name="storedLength"/>. <para>实际写出的字节数；不超过 <paramref name="storedLength"/>。</para></param>
    /// <param name="cancellationToken">Cancels the copy. <para>取消本次复制。</para></param>
    /// <exception cref="EndOfStreamException">The source ends before <paramref name="storedLength"/> bytes.
    /// <para>源流在写满 <paramref name="storedLength"/> 字节之前结束。</para></exception>
    public static void DecryptCopy(Stream source, Stream destination, long storedLength, long copyLength,
        CancellationToken cancellationToken)
    {
        var cipher = GetCipher(ImageWtyCipherScope.Content);
        var buffer = ArrayPool<byte>.Shared.Rent(TransferSize);
        try
        {
            var remainingRead = storedLength;
            var remainingWrite = copyLength;
            while (remainingRead > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(buffer.Length, remainingRead);
                source.ReadExactly(buffer, 0, wanted);
                remainingRead -= wanted;
                var decryptable = wanted - (wanted % BlockSize);
                for (var offset = 0; offset < decryptable; offset += BlockSize)
                {
                    cipher.DecryptBlock(buffer.AsSpan(offset, BlockSize));
                }

                var write = (int)Math.Min(remainingWrite, wanted);
                destination.Write(buffer, 0, write);
                remainingWrite -= write;
                if (remainingWrite == 0)
                {
                    return;
                }
            }

            throw new EndOfStreamException("The declared payload length exceeds the available ciphertext.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Rc6 GetCipher(ImageWtyCipherScope scope) => scope switch
    {
        ImageWtyCipherScope.Header => HeaderCipher.Value,
        ImageWtyCipherScope.FileHeader => FileHeaderCipher.Value,
        ImageWtyCipherScope.Content => ContentCipher.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown IMAGEWTY cipher scope.")
    };

    private static Rc6 BuildCipher(byte filler, byte terminator)
    {
        var key = new byte[KeyLength];
        key.AsSpan().Fill(filler);
        key[^1] = terminator;
        return Rc6.Create(key);
    }
}