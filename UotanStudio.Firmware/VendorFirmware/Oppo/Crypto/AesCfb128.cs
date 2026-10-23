using System.Buffers;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;

internal static class AesCfb128
{
    private const int BlockSize = 16;
    private const int DefaultBufferSize = 81920;

    private static void ValidateKeyIv(byte[] key, byte[] iv)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (key.Length != 16)
        {
            throw new ArgumentException("AES key must be 16 bytes.", nameof(key));
        }

        if (iv is null)
        {
            throw new ArgumentNullException(nameof(iv));
        }

        if (iv.Length != 16)
        {
            throw new ArgumentException("IV must be 16 bytes.", nameof(iv));
        }
    }

    /// <summary>
    /// Decrypts data using AES-128-CFB with 128-bit segment size.
    /// <para>使用 AES-128-CFB（128 位段大小）解密数据。</para>
    /// </summary>
    internal static byte[] Decrypt(ReadOnlySpan<byte> data, byte[] key, byte[] iv)
    {
        ValidateKeyIv(key, iv);
        var result = data.ToArray();
        DecryptInPlace(result, key, iv);
        return result;
    }

    /// <summary>
    /// Encrypts data using AES-128-CFB with 128-bit segment size.
    /// <para>使用 AES-128-CFB（128 位段大小）加密数据。</para>
    /// </summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> data, byte[] key, byte[] iv)
    {
        ValidateKeyIv(key, iv);
        var result = data.ToArray();
        EncryptInPlace(result, key, iv);
        return result;
    }

    /// <summary>
    /// Decrypts data in place using AES-128-CFB with 128-bit segment size, batching the keystream through ECB.
    /// <para>使用 AES-128-CFB（128 位段大小）就地解密数据，并通过 ECB 批量生成密钥流。</para>
    /// </summary>
    internal static void DecryptInPlace(byte[] data, byte[] key, byte[] iv) => DecryptCore(data.AsSpan(), key, iv);

    /// <summary>
    /// Encrypts data in place using AES-128-CFB with 128-bit segment size.
    /// <para>使用 AES-128-CFB（128 位段大小）就地加密数据。</para>
    /// </summary>
    internal static void EncryptInPlace(byte[] data, byte[] key, byte[] iv)
    {
        ValidateKeyIv(key, iv);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        var feedback = new byte[BlockSize];
        iv.AsSpan().CopyTo(feedback);
        var encrypted = new byte[BlockSize];

        var offset = 0;
        while (offset < data.Length)
        {
            var blockSize = Math.Min(BlockSize, data.Length - offset);


            encryptor.TransformBlock(feedback, 0, BlockSize, encrypted, 0);

            if (blockSize == BlockSize)
            {

                XorBlockInPlace(data.AsSpan(offset, BlockSize), encrypted);


                Array.Copy(data, offset, feedback, 0, BlockSize);
            }
            else
            {

                XorBlockInPlace(data.AsSpan(offset, blockSize), encrypted);
            }

            offset += blockSize;
        }
    }

    /// <summary>
    /// Decrypts stream data using AES-128-CFB with 128-bit segment size.
    /// <para>使用 AES-128-CFB（128 位段大小）解密流数据。</para>
    /// </summary>
    internal static void Decrypt(Stream input, Stream output, byte[] key, byte[] iv, CancellationToken cancellationToken = default) => ProcessStream(input, output, key, iv, encrypt: false, cancellationToken);

    /// <summary>
    /// Searches the decrypted prefix of a ciphertext for a byte pattern without decrypting the whole buffer.
    /// <para>在不解密整个缓冲区的前提下，在密文的解密前缀中查找字节模式。</para>
    /// </summary>
    /// <param name="ciphertext">Ciphertext read from its first byte; only bytes below <paramref name="maximumBytes"/> are processed.
    /// <para>从首字节开始的密文；仅处理 <paramref name="maximumBytes"/> 之前的字节。</para></param>
    /// <param name="key">AES-128 key used in ECB mode for the keystream.
    /// <para>以 ECB 模式生成密钥流的 AES-128 密钥。</para></param>
    /// <param name="iv">Initial 16-byte feedback vector.
    /// <para>初始 16 字节反馈向量。</para></param>
    /// <param name="pattern">Non-empty byte pattern to find in the decrypted bytes.
    /// <para>在解密字节中查找的非空字节模式。</para></param>
    /// <param name="maximumBytes">Upper bound of ciphertext bytes to decrypt; values at or below zero search nothing.
    /// <para>需要解密的密文字节上限；小于等于零时不查找。</para></param>
    /// <returns>The zero-based offset of the first occurrence in the decrypted stream, or <c>-1</c> when it is absent.
    /// <para>该模式在解密流中首次出现的从零开始偏移；不存在时为 <c>-1</c>。</para></returns>
    internal static int IndexOfDecrypted(ReadOnlySpan<byte> ciphertext, byte[] key, byte[] iv, ReadOnlySpan<byte> pattern, int maximumBytes)
    {
        ValidateKeyIv(key, iv);
        if (pattern.Length == 0 || maximumBytes <= 0 || ciphertext.Length == 0)
        {
            return -1;
        }

        var limit = Math.Min(maximumBytes, ciphertext.Length);
        var retained = pattern.Length - 1;
        var scan = new byte[retained + BlockSize];
        var encrypted = new byte[BlockSize];
        var feedback = new byte[BlockSize];
        iv.AsSpan().CopyTo(feedback);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var held = 0;
        var offset = 0;
        while (offset < limit)
        {
            var blockSize = Math.Min(BlockSize, limit - offset);
            ciphertext.Slice(offset, blockSize).CopyTo(scan.AsSpan(held));

            if (encryptor.TransformBlock(feedback, 0, BlockSize, encrypted, 0) != BlockSize)
            {
                throw new CryptographicException("AES did not produce a complete CFB keystream block.");
            }

            if (blockSize == BlockSize)
            {
                ciphertext.Slice(offset, BlockSize).CopyTo(feedback);
            }

            var decrypted = scan.AsSpan(held, blockSize);
            for (var i = 0; i < decrypted.Length; i++)
            {
                decrypted[i] ^= encrypted[i];
            }

            var searchLength = held + blockSize;
            var found = scan.AsSpan(0, searchLength).IndexOf(pattern);
            if (found >= 0)
            {
                return offset - held + found;
            }

            var keep = Math.Min(retained, searchLength);
            scan.AsSpan(searchLength - keep, keep).CopyTo(scan);
            held = keep;
            offset += blockSize;
        }

        return -1;
    }

    /// <summary>
    /// Encrypts stream data using AES-128-CFB with 128-bit segment size.
    /// <para>使用 AES-128-CFB（128 位段大小）加密流数据。</para>
    /// </summary>
    internal static void Encrypt(Stream input, Stream output, byte[] key, byte[] iv, CancellationToken cancellationToken = default) => ProcessStream(input, output, key, iv, encrypt: true, cancellationToken);

    private static void ProcessStream(Stream input, Stream output, byte[] key, byte[] iv, bool encrypt, CancellationToken cancellationToken)
    {
        ValidateKeyIv(key, iv);
        using var aes = CreateEcbAes(key);
        var encryptor = encrypt ? aes.CreateEncryptor() : null;
        var feedback = new byte[BlockSize];
        iv.AsSpan().CopyTo(feedback);
        var encryptedBlock = new byte[BlockSize];
        var buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        var ecbInput = encrypt ? null : ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        var keystream = encrypt ? null : ArrayPool<byte>.Shared.Rent(DefaultBufferSize);

        try
        {
            var leftoverCount = 0;
            while (true)
            {
                var bytesRead = input.Read(buffer, leftoverCount, DefaultBufferSize - leftoverCount);
                var totalBytes = leftoverCount + bytesRead;

                if (totalBytes == 0)
                {
                    break;
                }

                int bytesToProcess;
                if (bytesRead == 0)
                {
                    bytesToProcess = totalBytes;
                }
                else
                {
                    bytesToProcess = totalBytes / BlockSize * BlockSize;
                }

                if (bytesToProcess > 0)
                {
                    if (encrypt)
                    {
                        ProcessBlock(buffer, 0, bytesToProcess, feedback, encryptedBlock, encryptor!);
                    }
                    else
                    {
                        DecryptBatch(buffer.AsSpan(0, bytesToProcess), feedback, aes, ecbInput!, keystream!);
                    }

                    output.Write(buffer, 0, bytesToProcess);
                }

                leftoverCount = totalBytes - bytesToProcess;
                if (leftoverCount > 0)
                {
                    Array.Copy(buffer, bytesToProcess, buffer, 0, leftoverCount);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            encryptor?.Dispose();
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
            if (ecbInput is not null)
            {
                ArrayPool<byte>.Shared.Return(ecbInput);
            }
            if (keystream is not null)
            {
                ArrayPool<byte>.Shared.Return(keystream);
            }
        }
    }

    private static void DecryptCore(Span<byte> data, byte[] key, byte[] iv)
    {
        ValidateKeyIv(key, iv);
        if (data.IsEmpty)
        {
            return;
        }

        using var aes = CreateEcbAes(key);
        var feedback = new byte[BlockSize];
        iv.AsSpan().CopyTo(feedback);
        var ecbInput = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        var keystream = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        try
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var length = Math.Min(DefaultBufferSize, data.Length - offset);
                DecryptBatch(data.Slice(offset, length), feedback, aes, ecbInput, keystream);
                offset += length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ecbInput);
            ArrayPool<byte>.Shared.Return(keystream);
        }
    }

    private static void DecryptBatch(Span<byte> data, byte[] feedback, Aes aes, byte[] ecbInput, byte[] keystream)
    {
        var keystreamLength = (data.Length + BlockSize - 1) / BlockSize * BlockSize;
        var cipherLength = keystreamLength - BlockSize;
        feedback.AsSpan().CopyTo(ecbInput.AsSpan(0, BlockSize));
        data[..cipherLength].CopyTo(ecbInput.AsSpan(BlockSize));
        if (aes.EncryptEcb(ecbInput.AsSpan(0, keystreamLength), keystream.AsSpan(0, keystreamLength), PaddingMode.None) != keystreamLength)
        {
            throw new CryptographicException("AES did not produce a complete CFB keystream block.");
        }

        var fullBlocks = data.Length / BlockSize;
        if (fullBlocks > 0)
        {
            data.Slice((fullBlocks - 1) * BlockSize, BlockSize).CopyTo(feedback);
        }

        XorBlockInPlace(data, keystream.AsSpan(0, data.Length));
    }

    private static Aes CreateEcbAes(byte[] key)
    {
        var aes = Aes.Create();
        try
        {
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            return aes;
        }
        catch
        {
            aes.Dispose();
            throw;
        }
    }

    /// <summary>
    /// XORs a 16-byte block in place with the encrypted keystream block (vectorized when available).
    /// <para>就地 XOR 16 字节块与加密密钥流块（可用时向量化）。</para>
    /// </summary>
    private static void XorBlockInPlace(Span<byte> data, ReadOnlySpan<byte> encryptedBlock)
    {
        if (System.Numerics.Vector.IsHardwareAccelerated && data.Length >= System.Numerics.Vector<byte>.Count)
        {
            var width = System.Numerics.Vector<byte>.Count;
            var i = 0;
            for (; i <= data.Length - width; i += width)
            {
                System.Numerics.Vector.Xor(
                    new System.Numerics.Vector<byte>(data.Slice(i, width)),
                    new System.Numerics.Vector<byte>(encryptedBlock.Slice(i, width)))
                    .CopyTo(data.Slice(i, width));
            }
            for (; i < data.Length; i++)
            {
                data[i] ^= encryptedBlock[i];
            }
            return;
        }

        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= encryptedBlock[i];
        }
    }

    private static void ProcessBlock(byte[] buffer, int offset, int length, byte[] feedback, byte[] encryptedBlock, ICryptoTransform encryptor)
    {
        var end = offset + length;
        while (offset < end)
        {
            var blockSize = Math.Min(BlockSize, end - offset);
            encryptor.TransformBlock(feedback, 0, BlockSize, encryptedBlock, 0);

            if (blockSize == BlockSize)
            {
                XorBlockInPlace(buffer.AsSpan(offset, BlockSize), encryptedBlock);
                Array.Copy(buffer, offset, feedback, 0, BlockSize);
            }
            else
            {
                for (var i = 0; i < blockSize; i++)
                {
                    buffer[offset + i] ^= encryptedBlock[i];
                }
            }

            offset += blockSize;
        }
    }
}