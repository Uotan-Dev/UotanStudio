using System.Buffers;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.Algorithms;

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
    /// Decrypts data in place using AES-128-CFB with 128-bit segment size.
    /// <para>使用 AES-128-CFB（128 位段大小）就地解密数据。</para>
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


            encryptor.TransformBlock(feedback, 0, BlockSize, encrypted, 0);

            if (blockSize == BlockSize)
            {

                Array.Copy(data, offset, feedback, 0, BlockSize);


                for (var i = 0; i < BlockSize; i++)
                {
                    data[offset + i] ^= encrypted[i];
                }
            }
            else
            {

                for (var i = 0; i < blockSize; i++)
                {
                    data[offset + i] ^= encrypted[i];
                }
            }

            offset += blockSize;
        }
    }

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

                for (var i = 0; i < BlockSize; i++)
                {
                    data[offset + i] ^= encrypted[i];
                }


                Array.Copy(data, offset, feedback, 0, BlockSize);
            }
            else
            {

                for (var i = 0; i < blockSize; i++)
                {
                    data[offset + i] ^= encrypted[i];
                }
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
    /// Encrypts stream data using AES-128-CFB with 128-bit segment size.
    /// <para>使用 AES-128-CFB（128 位段大小）加密流数据。</para>
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
                    ProcessBlock(buffer, 0, bytesToProcess, feedback, encryptedBlock, encryptor, encrypt);
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


            encryptor.TransformBlock(feedback, 0, BlockSize, encryptedBlock, 0);

            if (blockSize == BlockSize)
            {
                if (encrypt)
                {

                    for (var i = 0; i < BlockSize; i++)
                    {
                        buffer[offset + i] ^= encryptedBlock[i];
                    }


                    Array.Copy(buffer, offset, feedback, 0, BlockSize);
                }
                else
                {

                    Array.Copy(buffer, offset, feedback, 0, BlockSize);


                    for (var i = 0; i < BlockSize; i++)
                    {
                        buffer[offset + i] ^= encryptedBlock[i];
                    }
                }
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
