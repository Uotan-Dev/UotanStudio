using System.Buffers;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.Oppo.Crypto.Algorithms;

/// <summary>
/// <para>使用 AES-128-CFB（128 位段大小）进行加解密操作的实现。</para>
/// Implementation of encryption/decryption using AES-128-CFB with 128-bit segment size.
/// </summary>
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
    /// <para>使用 AES-128-CFB（128 位段大小）解密数据。</para>
    /// Decrypts data using AES-128-CFB with 128-bit segment size.
    /// </summary>
    internal static byte[] Decrypt(ReadOnlySpan<byte> data, byte[] key, byte[] iv)
    {
        ValidateKeyIv(key, iv);
        var result = data.ToArray();
        DecryptInPlace(result, key, iv);
        return result;
    }

    /// <summary>
    /// <para>使用 AES-128-CFB（128 位段大小）加密数据。</para>
    /// Encrypts data using AES-128-CFB with 128-bit segment size.
    /// </summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> data, byte[] key, byte[] iv)
    {
        ValidateKeyIv(key, iv);
        var result = data.ToArray();
        EncryptInPlace(result, key, iv);
        return result;
    }

    /// <summary>
    /// <para>使用 AES-128-CFB（128 位段大小）就地解密数据。</para>
    /// Decrypts data in place using AES-128-CFB with 128-bit segment size.
    /// </summary>
    internal static void DecryptInPlace(byte[] data, byte[] key, byte[] iv)
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

            // Encrypt the feedback (IV or previous ciphertext block)
            encryptor.TransformBlock(feedback, 0, BlockSize, encrypted, 0);

            if (blockSize == BlockSize)
            {
                // Save ciphertext for next feedback before XOR
                Array.Copy(data, offset, feedback, 0, BlockSize);

                // XOR ciphertext with encrypted feedback to get plaintext
                for (var i = 0; i < BlockSize; i++)
                {
                    data[offset + i] ^= encrypted[i];
                }
            }
            else
            {
                // Partial block
                for (var i = 0; i < blockSize; i++)
                {
                    data[offset + i] ^= encrypted[i];
                }
            }

            offset += blockSize;
        }
    }

    /// <summary>
    /// <para>使用 AES-128-CFB（128 位段大小）就地加密数据。</para>
    /// Encrypts data in place using AES-128-CFB with 128-bit segment size.
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

            // Encrypt the feedback (IV or previous ciphertext block)
            encryptor.TransformBlock(feedback, 0, BlockSize, encrypted, 0);

            if (blockSize == BlockSize)
            {
                // XOR plaintext with encrypted feedback to get ciphertext
                for (var i = 0; i < BlockSize; i++)
                {
                    data[offset + i] ^= encrypted[i];
                }

                // Update feedback with ciphertext
                Array.Copy(data, offset, feedback, 0, BlockSize);
            }
            else
            {
                // Partial block
                for (var i = 0; i < blockSize; i++)
                {
                    data[offset + i] ^= encrypted[i];
                }
            }

            offset += blockSize;
        }
    }

    /// <summary>
    /// <para>使用 AES-128-CFB（128 位段大小）解密流数据。</para>
    /// Decrypts stream data using AES-128-CFB with 128-bit segment size.
    /// </summary>
    internal static void Decrypt(Stream input, Stream output, byte[] key, byte[] iv, CancellationToken cancellationToken = default) => ProcessStream(input, output, key, iv, encrypt: false, cancellationToken);

    /// <summary>
    /// <para>使用 AES-128-CFB（128 位段大小）加密流数据。</para>
    /// Encrypts stream data using AES-128-CFB with 128-bit segment size.
    /// </summary>
    internal static void Encrypt(Stream input, Stream output, byte[] key, byte[] iv, CancellationToken cancellationToken = default) => ProcessStream(input, output, key, iv, encrypt: true, cancellationToken);

    private static void ProcessStream(Stream input, Stream output, byte[] key, byte[] iv, bool encrypt, CancellationToken cancellationToken)
    {
        ValidateKeyIv(key, iv);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        var feedback = new byte[BlockSize];
        iv.AsSpan().CopyTo(feedback);
        var encryptedBlock = new byte[BlockSize];
        var buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);

        try
        {
            var leftoverCount = 0;
            while (true)
            {
                // Read new data after leftover bytes
                var bytesRead = input.Read(buffer, leftoverCount, DefaultBufferSize - leftoverCount);
                var totalBytes = leftoverCount + bytesRead;

                if (totalBytes == 0)
                {
                    break;
                }

                int bytesToProcess;
                if (bytesRead == 0)
                {
                    // EOF - process all remaining bytes including partial block
                    bytesToProcess = totalBytes;
                }
                else
                {
                    // More data may come - only process full blocks
                    bytesToProcess = totalBytes / BlockSize * BlockSize;
                }

                if (bytesToProcess > 0)
                {
                    ProcessBlock(buffer, 0, bytesToProcess, feedback, encryptedBlock, encryptor, encrypt);
                    output.Write(buffer, 0, bytesToProcess);
                }

                // Carry over leftover bytes
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
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ProcessBlock(byte[] buffer, int offset, int length, byte[] feedback, byte[] encryptedBlock, ICryptoTransform encryptor, bool encrypt)
    {
        var end = offset + length;
        while (offset < end)
        {
            var blockSize = Math.Min(BlockSize, end - offset);

            // Encrypt the feedback
            encryptor.TransformBlock(feedback, 0, BlockSize, encryptedBlock, 0);

            if (blockSize == BlockSize)
            {
                if (encrypt)
                {
                    // XOR plaintext with encrypted feedback to get ciphertext
                    for (var i = 0; i < BlockSize; i++)
                    {
                        buffer[offset + i] ^= encryptedBlock[i];
                    }

                    // Update feedback with ciphertext
                    Array.Copy(buffer, offset, feedback, 0, BlockSize);
                }
                else
                {
                    // Save ciphertext for next feedback before XOR
                    Array.Copy(buffer, offset, feedback, 0, BlockSize);

                    // XOR ciphertext with encrypted feedback to get plaintext
                    for (var i = 0; i < BlockSize; i++)
                    {
                        buffer[offset + i] ^= encryptedBlock[i];
                    }
                }
            }
            else
            {
                // Partial block (only at true end of stream)
                for (var i = 0; i < blockSize; i++)
                {
                    buffer[offset + i] ^= encryptedBlock[i];
                }
            }

            offset += blockSize;
        }
    }
}