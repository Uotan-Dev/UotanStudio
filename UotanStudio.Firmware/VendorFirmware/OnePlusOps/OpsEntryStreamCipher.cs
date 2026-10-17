using System.Buffers;
using System.Security.Cryptography;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.VendorFirmware.OnePlusOps;

/// <summary>
/// Decryption kind selected for one OPS entry payload by probing only its head and tail windows.
/// <para>仅探测头部与尾部窗口后为单个 OPS 条目负载选定的解密方式。</para>
/// </summary>
internal enum OpsEntryCipherKind
{
    /// <summary>No accepted key produced recognisable plaintext. <para>没有密钥产出可识别明文。</para></summary>
    None,

    /// <summary>Whole-entry AES-ECB decryption. <para>整条目 AES-ECB 解密。</para></summary>
    AesEcb,

    /// <summary>Whole-entry AES-ECB decryption followed by a byte reversal. <para>整条目 AES-ECB 解密后整体反转字节。</para></summary>
    ReversedAesEcb,

    /// <summary>SBox stream-cipher XOR with the default key schedule. <para>使用默认密钥调度的 SBox 流密码异或。</para></summary>
    SBox
}

/// <summary>
/// Streaming detection and decryption for one stored OPS entry. Payload bytes are copied through
/// bounded pooled buffers, so an entry of any size is never held in memory as a whole.
/// <para>针对单条已存储 OPS 条目的流式探测与解密。负载字节经有界池化缓冲复制，任意大小的条目都不会整体驻留内存。</para>
/// </summary>
internal static class OpsEntryStreamCipher
{
    private const int BufferSize = 81920;

    private const int ProbeLength = 0x440;

    private static byte[]? _sbox;

    /// <summary>
    /// Probes the head and tail windows of one entry and selects the decryption kind without reading the whole payload.
    /// <para>探测单条条目的头部与尾部窗口，在不解密整个负载的前提下选择解密方式。</para>
    /// </summary>
    /// <param name="source">Readable, seekable package stream owned by the caller.
    /// <para>调用方持有的可读、可寻址包流。</para></param>
    /// <param name="offset">Absolute payload start. <para>负载起始的绝对偏移。</para></param>
    /// <param name="size">Payload byte length. <para>负载字节长度。</para></param>
    /// <param name="cancellationToken">Checked before every probe read. <para>在每次探测读取前检查的取消令牌。</para></param>
    /// <returns>The selected kind and, for AES kinds, the matching key. <para>选定的方式；AES 类方式还包含匹配的密钥。</para></returns>
    internal static (OpsEntryCipherKind Kind, byte[]? AesKey) Detect(Stream source, long offset, long size, CancellationToken cancellationToken)
    {
        if (size < OpsConstants.AES_BLOCK_SIZE || size % OpsConstants.AES_BLOCK_SIZE != 0)
        {
            return (OpsEntryCipherKind.None, null);
        }

        var probeLength = (int)Math.Min(size, ProbeLength);
        var head = new byte[probeLength];
        ReadAt(source, offset, head, cancellationToken);
        if (OnePlusOpsFormatHandler.LooksLikeDecrypted(head))
        {
            return (OpsEntryCipherKind.None, null);
        }

        foreach (var key in OnePlusKeyTable.GetAllKeys())
        {
            try
            {
                var decryptedHead = DecryptAesEcb(head, 0, probeLength, key);
                if (OnePlusOpsFormatHandler.LooksLikeDecrypted(decryptedHead))
                {
                    return (OpsEntryCipherKind.AesEcb, key);
                }

                if (size <= ProbeLength)
                {
                    Array.Reverse(decryptedHead);
                    if (OnePlusOpsFormatHandler.LooksLikeDecrypted(decryptedHead))
                    {
                        return (OpsEntryCipherKind.ReversedAesEcb, key);
                    }
                }
                else
                {
                    var tail = new byte[probeLength];
                    ReadAt(source, offset + size - probeLength, tail, cancellationToken);
                    var decryptedTail = DecryptAesEcb(tail, 0, probeLength, key);
                    Array.Reverse(decryptedTail);
                    if (OnePlusOpsFormatHandler.LooksLikeDecrypted(decryptedTail))
                    {
                        return (OpsEntryCipherKind.ReversedAesEcb, key);
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
            }
        }

        var probe = new byte[probeLength];
        Array.Copy(head, probe, probeLength);
        var sbox = GetSBox();
        for (var index = 0; index < probeLength; index++)
        {
            probe[index] ^= sbox[index % 256];
        }

        return OnePlusOpsFormatHandler.LooksLikeDecrypted(probe)
            ? (OpsEntryCipherKind.SBox, null)
            : (OpsEntryCipherKind.None, null);
    }

    /// <summary>
    /// Writes the decrypted payload selected by <see cref="Detect"/> to the destination in bounded blocks.
    /// <para>把 <see cref="Detect"/> 选定的解密负载以有界块写入目标。</para>
    /// </summary>
    /// <param name="source">Readable, seekable package stream owned by the caller.
    /// <para>调用方持有的可读、可寻址包流。</para></param>
    /// <param name="offset">Absolute payload start. <para>负载起始的绝对偏移。</para></param>
    /// <param name="size">Payload byte length. <para>负载字节长度。</para></param>
    /// <param name="kind">Selected decryption kind; <see cref="OpsEntryCipherKind.None"/> writes nothing.
    /// <para>选定的解密方式；<see cref="OpsEntryCipherKind.None"/> 不写入任何内容。</para></param>
    /// <param name="aesKey">AES key for the AES kinds; ignored for the SBox kind.
    /// <para>AES 类方式使用的 AES 密钥；SBox 方式忽略。</para></param>
    /// <param name="output">Writable destination owned by the caller. <para>调用方持有的可写目标。</para></param>
    /// <param name="cancellationToken">Checked between blocks. <para>在各块之间检查的取消令牌。</para></param>
    internal static void WriteDecrypted(
        Stream source,
        long offset,
        long size,
        OpsEntryCipherKind kind,
        byte[]? aesKey,
        Stream output,
        CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case OpsEntryCipherKind.AesEcb when aesKey is not null:
                WriteAesEcb(source, offset, size, aesKey, reverse: false, output, cancellationToken);
                return;
            case OpsEntryCipherKind.ReversedAesEcb when aesKey is not null:
                WriteAesEcb(source, offset, size, aesKey, reverse: true, output, cancellationToken);
                return;
            case OpsEntryCipherKind.SBox:
                WriteSBox(source, offset, size, output, cancellationToken);
                return;
        }
    }

    private static void WriteAesEcb(
        Stream source,
        long offset,
        long size,
        byte[] key,
        bool reverse,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var decryptor = aes.CreateDecryptor();

        var input = ArrayPool<byte>.Shared.Rent(BufferSize);
        var plaintext = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            if (!reverse)
            {
                source.Position = offset;
                var remaining = size;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var take = (int)Math.Min(BufferSize, remaining);
                    ReadExactly(source, input, take);
                    var written = decryptor.TransformBlock(input, 0, take, plaintext, 0);
                    output.Write(plaintext, 0, written);
                    remaining -= take;
                }

                return;
            }

            var blocksRemaining = size;
            while (blocksRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var take = (int)Math.Min(BufferSize, blocksRemaining);
                source.Position = offset + blocksRemaining - take;
                ReadExactly(source, input, take);
                var written = decryptor.TransformBlock(input, 0, take, plaintext, 0);
                Array.Reverse(plaintext, 0, written);
                output.Write(plaintext, 0, written);
                blocksRemaining -= take;
            }
        }
        finally
        {
            Array.Clear(input, 0, input.Length);
            Array.Clear(plaintext, 0, plaintext.Length);
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(plaintext);
        }
    }

    private static void WriteSBox(
        Stream source,
        long offset,
        long size,
        Stream output,
        CancellationToken cancellationToken)
    {
        var sbox = GetSBox();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            source.Position = offset;
            var position = 0L;
            while (position < size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var take = (int)Math.Min(BufferSize, size - position);
                ReadExactly(source, buffer, take);
                for (var index = 0; index < take; index++)
                {
                    buffer[index] ^= sbox[(position + index) % 256];
                }

                output.Write(buffer, 0, take);
                position += take;
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] DecryptAesEcb(byte[] ciphertext, int offset, int count, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var decryptor = aes.CreateDecryptor();
        var output = new byte[count];
        decryptor.TransformBlock(ciphertext, offset, count, output, 0);
        return output;
    }

    private static void ReadAt(Stream source, long offset, byte[] buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        source.Position = offset;
        ReadExactly(source, buffer, buffer.Length);
    }

    private static void ReadExactly(Stream source, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = source.Read(buffer, total, count - total);
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of OPS entry: expected {count} bytes, read {total} bytes.");
            }

            total += read;
        }
    }

    private static byte[] GetSBox() => _sbox ??= OnePlusOpsFormatHandler.SBoxTable();
}