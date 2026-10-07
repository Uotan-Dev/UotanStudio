using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.BootSecurity.Avb.Signing;

/// <summary>
/// Encodes and validates the RSA public-key blob produced by <c>avbtool extract_public_key</c>.
/// <para>编解码并校验由 <c>avbtool extract_public_key</c> 生成的 RSA 公钥 Blob。</para>
/// </summary>
/// <remarks>
/// Blob layout, all big-endian: a 4-byte key size in bits (2048, 4096 or 8192), a 4-byte <c>n0inv</c> equal to the
/// negated modular inverse of the modulus' least-significant 32-bit word modulo 2^32, the zero-padded modulus, then
/// <c>R^2 mod N</c> with R = 2^(key bits). Total size is 8 + 2 * (key bits / 8).
/// <para>Blob 布局（全部大端）：4 字节密钥位数（2048、4096 或 8192）、4 字节 <c>n0inv</c>（模数最低 32 位字模 2^32
/// 的负乘法逆元）、零填充的模数，随后是 <c>R^2 mod N</c>（R = 2^密钥位数）。总大小为 8 + 2 * (密钥位数 / 8)。</para>
/// </remarks>
public static class AvbRsaPublicKeyCodec
{
    /// <summary>
    /// Exports an RSA key in libavb's big-endian public-key format.
    /// <para>以 libavb 的大端公钥格式导出 RSA 公钥。</para>
    /// </summary>
    /// <param name="key">
    /// RSA public key with 2048-, 4096- or 8192-bit modulus and exponent 65537.
    /// <para>模数为 2048、4096 或 8192 位且公钥指数为 65537 的 RSA 公钥。</para>
    /// </param>
    /// <returns>The encoded public-key blob. <para>编码后的公钥 Blob。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null. <para><paramref name="key"/> 为 null。</para></exception>
    /// <exception cref="CryptographicException">
    /// The modulus is missing, the exponent is not 65537, the key size is outside 2048/4096/8192 bits, or the modulus
    /// does not match its declared size (top bit clear or even).
    /// <para>模数缺失、指数非 65537、密钥位数不在 2048/4096/8192 之内，或模数与声明位数不匹配（最高位为 0 或为偶数）。</para>
    /// </exception>
    public static byte[] Export(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parameters = key.ExportParameters(false);
        if (parameters.Modulus is null || parameters.Exponent is not [1, 0, 1])
        {
            throw new CryptographicException("AVB RSA keys must use public exponent 65537.");
        }
        return Export(parameters.Modulus, key.KeySize);
    }

    /// <summary>
    /// Imports and validates a libavb RSA public-key blob.
    /// <para>导入并校验 libavb RSA 公钥 Blob。</para>
    /// </summary>
    /// <param name="blob">
    /// Complete public-key blob bytes. <para>完整的公钥 Blob 字节。</para>
    /// </param>
    /// <returns>A fresh <see cref="RSA"/> instance carrying the parsed modulus; the caller owns and must dispose it.
    /// <para>携带已解析模数的新 <see cref="RSA"/> 实例；由调用方持有并负责释放。</para></returns>
    /// <exception cref="InvalidDataException">
    /// The blob fails any structural or Montgomery-field check performed by <see cref="TryImport"/>.
    /// <para>Blob 未通过 <see cref="TryImport"/> 执行的任一结构性或 Montgomery 域校验。</para>
    /// </exception>
    public static RSA Import(ReadOnlySpan<byte> blob)
        => TryImport(blob) ?? throw new InvalidDataException("The AVB RSA public-key blob is invalid.");

    /// <summary>
    /// Attempts to import a libavb RSA public-key blob, including Montgomery-field validation.
    /// <para>尝试导入 libavb RSA 公钥 Blob，包含 Montgomery 域校验。</para>
    /// </summary>
    /// <param name="blob">
    /// Candidate blob; it must be exactly 8 + 2 * (key bits / 8) bytes with key bits of 2048, 4096 or 8192.
    /// <para>候选 Blob；长度必须恰为 8 + 2 * (密钥位数 / 8)，且密钥位数为 2048、4096 或 8192。</para>
    /// </param>
    /// <returns>
    /// An <see cref="RSA"/> instance to dispose after use, or <see langword="null"/> when the size, modulus top bit or
    /// parity, stored <c>n0inv</c>, or constant-time compared <c>R^2 mod N</c> fails validation.
    /// <para>用后需释放的 <see cref="RSA"/> 实例；当长度、模数最高位或奇偶性、存储的 <c>n0inv</c>，或以恒定时间比对的
    /// <c>R^2 mod N</c> 校验失败时返回 <see langword="null"/>。</para>
    /// </returns>
    public static RSA? TryImport(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 8)
        {
            return null;
        }
        var keyBits = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (keyBits is not (2048 or 4096 or 8192))
        {
            return null;
        }
        var keyBytes = checked((int)keyBits / 8);
        if (blob.Length != 8 + 2 * keyBytes)
        {
            return null;
        }
        var modulus = blob.Slice(8, keyBytes);
        if ((modulus[0] & 0x80) == 0 || (modulus[^1] & 1) == 0
            || BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(4, 4)) != ComputeN0Inv(modulus))
        {
            return null;
        }
        var expectedRr = ComputeRr(modulus, checked((int)keyBits));
        if (!CryptographicOperations.FixedTimeEquals(expectedRr, blob.Slice(8 + keyBytes, keyBytes)))
        {
            return null;
        }

        try
        {
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = modulus.ToArray(),
                Exponent = [1, 0, 1]
            });
            return rsa;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically writes an AVB public-key blob without replacing the destination on failure.
    /// <para>原子写入 AVB 公钥 Blob；失败时不会替换目标文件。</para>
    /// </summary>
    /// <param name="key">
    /// RSA public key to encode via <see cref="Export(RSA)"/>. <para>通过 <see cref="Export(RSA)"/> 编码的 RSA 公钥。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination file; parent directories are created when missing. <para>目标文件；缺失的父目录会被创建。</para>
    /// </param>
    /// <param name="overwrite">
    /// Whether an existing <paramref name="outputPath"/> may be replaced. <para>是否允许替换已存在的 <paramref name="outputPath"/>。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels before or during the temporary-file write; the temporary file is deleted either way.
    /// <para>在临时文件写入前或写入中取消；两种情况下临时文件都会被删除。</para>
    /// </param>
    /// <returns>
    /// A task completed once the blob has been moved into place. <para>Blob 移动到位后即完成的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is null or whitespace.
    /// <para><paramref name="outputPath"/> 为 null 或空白。</para></exception>
    /// <exception cref="CryptographicException">
    /// <see cref="Export(RSA)"/> rejects the key. <para><see cref="Export(RSA)"/> 拒绝了该密钥。</para></exception>
    /// <exception cref="IOException">
    /// The file move failed, for example because <paramref name="outputPath"/> exists and <paramref name="overwrite"/>
    /// is <see langword="false"/>. <para>文件移动失败，例如 <paramref name="outputPath"/> 已存在且
    /// <paramref name="overwrite"/> 为 <see langword="false"/>。</para></exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para>
    /// </exception>
    public static async Task ExportAsync(
        RSA key,
        string outputPath,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var blob = Export(key);
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = TempFileHelper.CreateAdjacentPath(output);
        try
        {
            await File.WriteAllBytesAsync(temporary, blob, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite);
        }
        finally
        {
            File.Delete(temporary);
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    private static byte[] Export(ReadOnlySpan<byte> modulus, int keyBits)
    {
        if (keyBits is not (2048 or 4096 or 8192))
        {
            throw new CryptographicException("AVB RSA keys must be 2048, 4096, or 8192 bits.");
        }
        var keyBytes = keyBits / 8;
        if (modulus.Length > keyBytes)
        {
            modulus = modulus[^keyBytes..];
        }
        var paddedModulus = new byte[keyBytes];
        modulus.CopyTo(paddedModulus.AsSpan(keyBytes - modulus.Length));
        if ((paddedModulus[0] & 0x80) == 0 || (paddedModulus[^1] & 1) == 0)
        {
            throw new CryptographicException("AVB RSA modulus does not match its declared key size.");
        }

        var blob = new byte[checked(8 + 2 * keyBytes)];
        BinaryPrimitives.WriteUInt32BigEndian(blob, checked((uint)keyBits));
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), ComputeN0Inv(paddedModulus));
        paddedModulus.CopyTo(blob, 8);
        ComputeRr(paddedModulus, keyBits).CopyTo(blob, 8 + keyBytes);
        CryptographicOperations.ZeroMemory(paddedModulus);
        return blob;
    }

    private static uint ComputeN0Inv(ReadOnlySpan<byte> modulus)
    {
        var low = BinaryPrimitives.ReadUInt32BigEndian(modulus[^4..]);
        var inverse = 1U;
        for (var index = 0; index < 5; index++)
        {
            inverse = unchecked(inverse * (2U - low * inverse));
        }
        return unchecked(0U - inverse);
    }

    private static byte[] ComputeRr(ReadOnlySpan<byte> modulus, int keyBits)
    {
        var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var value = (BigInteger.One << checked(keyBits * 2)) % n;
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[keyBits / 8];
        bytes.CopyTo(padded, padded.Length - bytes.Length);
        return padded;
    }
}