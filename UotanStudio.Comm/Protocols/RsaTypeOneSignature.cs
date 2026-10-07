using System.Numerics;
using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols;

/// <summary>
/// Raw RSA private-key operation with PKCS#1 v1.5 type-1 (signature) padding over an unframed payload.
/// The MTK Boot ROM SLA responder and the legacy HDC <c>RsaEncrypt</c> handshake both make the device
/// recover <c>00 01 FF..FF 00 || payload</c> with the host public exponent, so neither a digest nor a
/// DigestInfo prefix may be inserted. The BCL exposes no raw private-key operation, therefore the
/// modular exponentiation is composed from <see cref="BigInteger.ModPow"/> and keeps the blinding of
/// BouncyCastle's <c>RsaBlindedEngine</c>: the payload is multiplied by <c>r^e</c> before the private
/// exponentiation and the result is multiplied by <c>r^-1</c> afterwards, so the exponentiation never
/// sees the caller-controlled payload directly.
/// <para>对未加帧负载执行 PKCS#1 v1.5 type-1（签名）填充的裸 RSA 私钥运算。MTK Boot ROM SLA 应答器与
/// 旧版 HDC <c>RsaEncrypt</c> 握手都要求设备用主机公钥指数还原出 <c>00 01 FF..FF 00 || 负载</c>，
/// 因此既不能插入摘要也不能插入 DigestInfo 前缀。BCL 不提供裸私钥运算，故此处用
/// <see cref="BigInteger.ModPow"/> 组合模幂，并保留 BouncyCastle <c>RsaBlindedEngine</c> 的盲化：
/// 私钥指数运算前把负载乘以 <c>r^e</c>，运算后再乘以 <c>r^-1</c>，使幂运算不会直接看到调用方可控的负载。</para>
/// </summary>
internal static class RsaTypeOneSignature
{
    private const int MinimumPaddingLength = 8;

    /// <summary>
    /// Produces the RSA private-key operation over a PKCS#1 v1.5 type-1 encoded payload.
    /// <para>对 PKCS#1 v1.5 type-1 编码的负载执行 RSA 私钥运算。</para>
    /// </summary>
    /// <param name="key">RSA key with exportable private CRT parameters. <para>带可导出私钥 CRT 参数的 RSA 密钥。</para></param>
    /// <param name="payload">
    /// Bytes placed at the end of the encoded block; at most <c>modulus length - 11</c> bytes.
    /// <para>置于编码块末尾的字节；至多 <c>模数长度 - 11</c> 字节。</para>
    /// </param>
    /// <returns>The signature-width result of the private-key operation. <para>私钥运算得到的签名宽度结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.
    /// <para><paramref name="key"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="CryptographicException">
    /// The key has no exportable private parameters, or <paramref name="payload"/> does not fit the type-1 block.
    /// <para>密钥没有可导出的私钥参数，或 <paramref name="payload"/> 无法装入 type-1 块。</para>
    /// </exception>
    internal static byte[] Sign(RSA key, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parameters = key.ExportParameters(true);
        try
        {
            var modulus = parameters.Modulus!;
            if (payload.Length > modulus.Length - 3 - MinimumPaddingLength)
            {
                throw new CryptographicException("The payload does not fit the RSA type-1 block.");
            }

            var encoded = new byte[modulus.Length];
            encoded[1] = 0x01;
            encoded.AsSpan(2, encoded.Length - payload.Length - 3).Fill(0xFF);
            payload.CopyTo(encoded.AsSpan(encoded.Length - payload.Length));

            var publicExponent = new BigInteger(parameters.Exponent!, isUnsigned: true, isBigEndian: true);
            var privateExponent = new BigInteger(parameters.D!, isUnsigned: true, isBigEndian: true);
            var modulusValue = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
            var message = new BigInteger(encoded, isUnsigned: true, isBigEndian: true);
            CryptographicOperations.ZeroMemory(encoded);

            var blindBytes = new byte[modulus.Length + sizeof(ulong)];
            BigInteger blindingFactor;
            BigInteger blindingInverse;
            do
            {
                RandomNumberGenerator.Fill(blindBytes);
                blindingFactor = new BigInteger(blindBytes, isUnsigned: true, isBigEndian: true) % modulusValue;
            }
            while (blindingFactor < 2 || blindingFactor > modulusValue - 2
                || !TryModInverse(blindingFactor, modulusValue, out blindingInverse));
            CryptographicOperations.ZeroMemory(blindBytes);

            var blinded = message * BigInteger.ModPow(blindingFactor, publicExponent, modulusValue) % modulusValue;
            var unblinded = BigInteger.ModPow(blinded, privateExponent, modulusValue) * blindingInverse % modulusValue;

            var bytes = unblinded.ToByteArray(isUnsigned: true, isBigEndian: true);
            var result = new byte[modulus.Length];
            bytes.CopyTo(result.AsSpan(result.Length - bytes.Length));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
            CryptographicOperations.ZeroMemory(parameters.P);
            CryptographicOperations.ZeroMemory(parameters.Q);
            CryptographicOperations.ZeroMemory(parameters.DP);
            CryptographicOperations.ZeroMemory(parameters.DQ);
            CryptographicOperations.ZeroMemory(parameters.InverseQ);
        }
    }

    private static bool TryModInverse(BigInteger value, BigInteger modulus, out BigInteger inverse)
    {
        var remainder = modulus;
        var divisor = value;
        var coefficient = BigInteger.Zero;
        var nextCoefficient = BigInteger.One;
        while (!divisor.IsZero)
        {
            var quotient = remainder / divisor;
            (remainder, divisor) = (divisor, remainder - quotient * divisor);
            (coefficient, nextCoefficient) = (nextCoefficient, coefficient - quotient * nextCoefficient);
        }

        if (remainder != BigInteger.One)
        {
            inverse = BigInteger.Zero;
            return false;
        }

        inverse = coefficient.Sign < 0 ? coefficient + modulus : coefficient;
        return true;
    }
}