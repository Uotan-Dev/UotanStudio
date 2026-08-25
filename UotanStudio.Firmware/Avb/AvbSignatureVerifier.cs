using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;

namespace UotanStudio.Firmware.Avb;

/// <summary>
/// A single descriptor parsed from the vbmeta auxiliary data block.
/// <para>从 vbmeta 辅助数据块中解析出的单个描述符。</para>
/// </summary>
public sealed class AvbDescriptorInfo
{
    /// <summary>
    /// Gets the descriptor tag (0 = hash descriptor, 1 = hashtree, 2 = prop, 3 = kernel cmdline, 4 = chain).
    /// <para>获取描述符标签（0 = 哈希描述符，1 = 哈希树，2 = 属性，3 = 内核命令行，4 = 链）。</para>
    /// </summary>
    public ulong Tag { get; init; }

    /// <summary>
    /// Gets a human-readable descriptor type name.
    /// <para>获取人类可读的描述符类型名称。</para>
    /// </summary>
    public string TypeName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of bytes following the descriptor header (tag + length).
    /// <para>获取描述符头（标签 + 长度）之后紧随的字节数。</para>
    /// </summary>
    public ulong Size { get; init; }

    /// <summary>
    /// Gets the target partition name (hash/hashtree descriptors only).
    /// <para>获取目标分区名称（仅哈希/哈希树描述符）。</para>
    /// </summary>
    public string? PartitionName { get; init; }

    /// <summary>
    /// Gets the hash algorithm name (hash descriptors only, e.g. "sha256").
    /// <para>获取哈希算法名称（仅哈希描述符，如 "sha256"）。</para>
    /// </summary>
    public string? HashAlgorithm { get; init; }

    /// <summary>
    /// Gets the salt used when computing the digest (hash descriptors only).
    /// <para>获取计算摘要时使用的盐值（仅哈希描述符）。</para>
    /// </summary>
    public byte[]? Salt { get; init; }

    /// <summary>
    /// Gets the expected digest stored in the descriptor (hash descriptors only).
    /// <para>获取描述符中存储的期望摘要（仅哈希描述符）。</para>
    /// </summary>
    public byte[]? Digest { get; init; }
}

/// <summary>
/// Result of an AVB signature / hash verification attempt.
/// <para>AVB 签名 / 哈希验证尝试的结果。</para>
/// </summary>
public sealed class AvbSignatureVerifyResult
{
    /// <summary>
    /// Gets whether the image carries a signature (algorithm != NONE and signature bytes present).
    /// <para>获取镜像是否携带签名（算法 != NONE 且存在签名字节）。</para>
    /// </summary>
    public bool IsSigned { get; init; }

    /// <summary>
    /// Gets whether the RSASSA-PSS / PKCS#1 v1.5 signature verified successfully.
    /// <para>获取 RSASSA-PSS / PKCS#1 v1.5 签名是否验证成功。</para>
    /// </summary>
    public bool SignatureIsValid { get; init; }

    /// <summary>
    /// Gets whether the digest stored in the header matches a recomputed digest.
    /// <para>获取头中存储的摘要是否与重新计算的摘要匹配。</para>
    /// </summary>
    public bool HashIsValid { get; init; }

    /// <summary>
    /// Gets the error message when verification could not complete.
    /// <para>获取验证无法完成时的错误信息。</para>
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the algorithm display name (e.g. "SHA256_RSA4096").
    /// <para>获取算法显示名称（如 "SHA256_RSA4096"）。</para>
    /// </summary>
    public string AlgorithmName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the raw signature bytes extracted from the authentication data block.
    /// <para>获取从认证数据块中提取的原始签名字节。</para>
    /// </summary>
    public byte[]? StoredSignature { get; init; }

    /// <summary>
    /// Gets the digest stored in the header (when present).
    /// <para>获取头中存储的摘要（若存在）。</para>
    /// </summary>
    public byte[]? StoredHash { get; init; }

    /// <summary>
    /// Gets the digest recomputed from the image contents (when a match was found).
    /// <para>获取根据镜像内容重新计算的摘要（若找到匹配项）。</para>
    /// </summary>
    public byte[]? ComputedHash { get; init; }

    /// <summary>
    /// Gets the descriptors parsed from the auxiliary data block.
    /// <para>获取从辅助数据块解析出的描述符。</para>
    /// </summary>
    public IReadOnlyList<AvbDescriptorInfo> Descriptors { get; init; } = [];
}

/// <summary>
/// Verifies the RSASSA-PSS / PKCS#1 v1.5 signature of an Android Verified Boot (AVB) vbmeta image.
/// <para>验证 Android Verified Boot（AVB）vbmeta 镜像的 RSASSA-PSS / PKCS#1 v1.5 签名。</para>
/// </summary>
/// <remarks>
/// Layout facts (verified against real avbtool-generated images):
/// the vbmeta header is 256 bytes; <c>authentication_data_block_size</c> / <c>auxiliary_data_block_size</c>
/// live at offsets 12/20; hash/signature offsets are relative to the authentication data block; public key,
/// descriptors and metadata offsets are relative to the auxiliary data block. The signing input is the
/// header followed by the auxiliary data block; legacy avbtool forks may keep the two block-size fields
/// intact while spec-strict tooling zeroes them before hashing/signing, so both forms are tried.
/// <para>布局事实（已对照真实 avbtool 生成的镜像验证）：vbmeta 头为 256 字节；
/// <c>authentication_data_block_size</c> / <c>auxiliary_data_block_size</c> 位于偏移 12/20；
/// 哈希/签名偏移相对于认证数据块；公钥、描述符和元数据偏移相对于辅助数据块。签名输入为
/// 头及其后的辅助数据块；旧式 avbtool 分支可能保留两个块大小字段不变，而符合规范的
/// 工具在哈希/签名前将其清零，因此两种形式都会被尝试。</para>
/// </remarks>
public static class AvbSignatureVerifier
{
    /// <summary>
    /// Size of the AVB vbmeta header in bytes.
    /// <para>AVB vbmeta 头的字节大小。</para>
    /// </summary>
    public const int HeaderSize = 256;

    /// <summary>
    /// Algorithm type value meaning "not signed".
    /// <para>表示"未签名"的算法类型值。</para>
    /// </summary>
    public const uint AlgorithmNone = 0;

    private static ReadOnlySpan<byte> MagicBytes => "AVB0"u8;
    private const int HashDescriptorTag = 0;
    private const int Exponent = 65537;

    /// <summary>
    /// Maps an algorithm type to its hash name and RSA key size.
    /// <para>将算法类型映射为哈希名称与 RSA 密钥大小。</para>
    /// </summary>
    /// <param name="algorithmType">The libavb algorithm type value (0-6). <para>libavb 算法类型值（0-6）。</para></param>
    /// <param name="keySizeBits">The RSA modulus size in bits (2048/4096/8192; 0 for NONE). <para>RSA 模数大小（位）（2048/4096/8192；NONE 为 0）。</para></param>
    /// <param name="hashName">The hash algorithm name ("SHA256"/"SHA512"; empty for NONE). <para>哈希算法名称（"SHA256"/"SHA512"；NONE 为空）。</para></param>
    /// <returns><c>true</c> when the algorithm type is known. <para>算法类型已知时返回 <c>true</c>。</para></returns>
    public static bool TryGetKeySizeAndHash(uint algorithmType, out int keySizeBits, out string hashName)
    {
        switch (algorithmType)
        {
            case 1:
            case 2:
            case 3:
                keySizeBits = algorithmType switch
                {
                    1 => 2048,
                    2 => 4096,
                    _ => 8192
                };
                hashName = "SHA256";
                return true;
            case 4:
            case 5:
            case 6:
                keySizeBits = algorithmType switch
                {
                    4 => 2048,
                    5 => 4096,
                    _ => 8192
                };
                hashName = "SHA512";
                return true;
            default:
                keySizeBits = 0;
                hashName = string.Empty;
                return algorithmType == AlgorithmNone;
        }
    }

    /// <summary>
    /// Gets the display name of an algorithm type (e.g. "SHA256_RSA4096").
    /// <para>获取算法类型的显示名称（如 "SHA256_RSA4096"）。</para>
    /// </summary>
    /// <param name="algorithmType">The libavb algorithm type value. <para>libavb 算法类型值。</para></param>
    /// <returns>The display name, or "NONE"/"UNKNOWN" when not mappable. <para>显示名称；无法映射时为 "NONE"/"UNKNOWN"。</para></returns>
    public static string GetAlgorithmName(uint algorithmType)
    {
        return algorithmType == AlgorithmNone
            ? "NONE"
            : TryGetKeySizeAndHash(algorithmType, out var keySizeBits, out var hashName)
            ? $"{hashName}_RSA{keySizeBits}"
            : $"UNKNOWN({algorithmType})";
    }

    /// <summary>
    /// Imports an AVB-format RSA public key blob (<c>avbtool extract_public_key</c> output:
    /// key_num_bits BE, n0inv BE, modulus BE, rr BE) into an <see cref="RSA"/> instance.
    /// <para>将 AVB 格式 RSA 公钥数据块（<c>avbtool extract_public_key</c> 输出：key_num_bits BE、
    /// n0inv BE、模数 BE、rr BE）导入 <see cref="RSA"/> 实例。</para>
    /// </summary>
    /// <param name="avbRsaPublicKeyBlob">The raw AVB public key blob. <para>原始 AVB 公钥数据块。</para></param>
    /// <returns>The imported key, or <c>null</c> when the blob is malformed. <para>导入的密钥；数据块格式错误时返回 <c>null</c>。</para></returns>
    public static RSA? ImportAvbRsaPublicKey(byte[]? avbRsaPublicKeyBlob)
    {
        if (avbRsaPublicKeyBlob is not { Length: >= 8 })
        {
            return null;
        }

        var keyNumBits = BinaryPrimitives.ReadUInt32BigEndian(avbRsaPublicKeyBlob);
        if (keyNumBits is not (2048 or 4096 or 8192))
        {
            return null;
        }

        var keyNumBytes = (int)(keyNumBits / 8);
        if (avbRsaPublicKeyBlob.Length < 8 + keyNumBytes)
        {
            return null;
        }

        var modulus = avbRsaPublicKeyBlob.AsSpan(8, keyNumBytes).ToArray();
        // The modulus must be odd; the high bit of the first byte is the sign guard for BigInteger.
        if ((modulus[^1] & 1) == 0)
        {
            return null;
        }

        try
        {
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = modulus,
                Exponent = [1, 0, 1]
            });
            return rsa;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Imports a PEM or DER public key / certificate into an <see cref="RSA"/> instance.
    /// <para>将 PEM 或 DER 公钥 / 证书导入 <see cref="RSA"/> 实例。</para>
    /// </summary>
    /// <param name="pemOrDerData">PEM text or DER bytes of an RSA public key, subject public key info, or X.509 certificate.
    /// <para>RSA 公钥、SubjectPublicKeyInfo 或 X.509 证书的 PEM 文本或 DER 字节。</para></param>
    /// <returns>The imported key, or <c>null</c> when it cannot be parsed. <para>导入的密钥；无法解析时返回 <c>null</c>。</para></returns>
    public static RSA? ImportPublicKeyPem(byte[]? pemOrDerData)
    {
        if (pemOrDerData is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(Encoding.ASCII.GetString(pemOrDerData));
                return rsa;
            }
            catch (ArgumentException)
            {
                // Not PEM text; fall through to DER handling.
            }
            catch (CryptographicException)
            {
                // Not PEM text; fall through to DER handling.
            }

            try
            {
                rsa.ImportSubjectPublicKeyInfo(pemOrDerData, out _);
                return rsa;
            }
            catch (CryptographicException)
            {
                // Not a subject public key info blob; try a certificate.
            }

            try
            {
                using var cert = X509CertificateLoader.LoadCertificate(pemOrDerData);
                var certKey = cert.GetRSAPublicKey();
                if (certKey != null)
                {
                    rsa.ImportParameters(certKey.ExportParameters(false));
                    return rsa;
                }
            }
            catch (CryptographicException)
            {
                // Not a certificate either.
            }

            rsa.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Verifies the embedded signature of a vbmeta image, using the public key embedded in its
    /// auxiliary data block (self-signed verification). Pass a trusted key to verify authenticity.
    /// <para>验证 vbmeta 镜像的内嵌签名，使用其辅助数据块中内嵌的公钥（自签名验证）。
    /// 传入受信任的密钥可验证真实性。</para>
    /// </summary>
    /// <param name="vbmetaData">The raw vbmeta image bytes. <para>原始 vbmeta 镜像字节。</para></param>
    /// <param name="trustedPublicKey">Optional trusted RSA public key; when null the embedded key is used.
    /// <para>可选的受信任 RSA 公钥；为 null 时使用内嵌密钥。</para></param>
    /// <returns>The verification result. <para>验证结果。</para></returns>
    public static AvbSignatureVerifyResult Verify(byte[] vbmetaData, RSA? trustedPublicKey = null)
    {
        if (vbmetaData is not { Length: >= HeaderSize })
        {
            return new AvbSignatureVerifyResult { ErrorMessage = "vbmeta data is too short to contain a 256-byte header. vbmeta 数据过短，不足以容纳 256 字节头。" };
        }

        var algorithmType = BinaryPrimitives.ReadUInt32BigEndian(vbmetaData.AsSpan(28, 4));
        var authSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(12, 8));
        var auxSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(20, 8));
        var sigOffset = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(48, 8));
        var sigSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(56, 8));
        var hashOffset = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(32, 8));
        var hashSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(40, 8));
        var pubKeyOffset = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(64, 8));
        var pubKeySize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(72, 8));

        if (!vbmetaData.AsSpan(0, 4).SequenceEqual(MagicBytes))
        {
            return new AvbSignatureVerifyResult { ErrorMessage = "Invalid vbmeta magic (expected AVB0). 无效的 vbmeta 魔数（应为 AVB0）。" };
        }

        var result = new AvbSignatureVerifyResult
        {
            IsSigned = false,
            AlgorithmName = GetAlgorithmName(algorithmType),
            Descriptors = ExtractDescriptors(vbmetaData)
        };

        if (algorithmType == AlgorithmNone)
        {
            result = With(result, ErrorMessage: "vbmeta image declares algorithm NONE (not signed). vbmeta 镜像声明算法为 NONE（未签名）。");
            return result;
        }

        if (sigSize == 0 || sigOffset + sigSize > authSize)
        {
            result = With(result, ErrorMessage: "vbmeta header declares no in-range signature. vbmeta 头未声明有效范围内的签名。");
            return result;
        }

        var sigStart = (int)(HeaderSize + sigOffset);
        var signature = vbmetaData.AsSpan(sigStart, (int)sigSize).ToArray();
        result = With(result, IsSigned: true, StoredSignature: signature);

        // Verify the stored digest first (cheap and independent of the public key).
        var salt = FindFirstSalt(result.Descriptors);
        var (hashOk, storedHash, computedHash) = VerifyStoredHash(vbmetaData, salt, algorithmType, hashOffset, hashSize);
        result = With(result, HashIsValid: hashOk, StoredHash: storedHash, ComputedHash: computedHash);

        RSA? key = trustedPublicKey;
        var embeddedKey = (RSA?)null;
        try
        {
            if (key == null && pubKeySize > 0 && pubKeyOffset + pubKeySize <= auxSize)
            {
                var keyBlob = vbmetaData.AsSpan(HeaderSize + (int)authSize + (int)pubKeyOffset, (int)pubKeySize).ToArray();
                embeddedKey = ImportAvbRsaPublicKey(keyBlob);
                key = embeddedKey;
            }

            if (key == null)
            {
                result = With(result, ErrorMessage: "No public key available for signature verification (provide a trusted key or an embedded key). 没有可用于签名验证的公钥（请提供受信任密钥或内嵌密钥）。");
                return result;
            }

            var valid = TryVerifySignature(vbmetaData, signature, key, algorithmType);
            return With(result, SignatureIsValid: valid);
        }
        finally
        {
            embeddedKey?.Dispose();
        }
    }

    /// <summary>
    /// Verifies an explicit signature over a vbmeta image with a raw AVB public key blob.
    /// <para>使用原始 AVB 公钥数据块验证 vbmeta 镜像上的显式签名。</para>
    /// </summary>
    /// <param name="vbmetaData">The raw vbmeta image bytes (used to derive the signing input). <para>原始 vbmeta 镜像字节（用于推导签名输入）。</para></param>
    /// <param name="signature">The raw RSA signature blob. <para>原始 RSA 签名数据块。</para></param>
    /// <param name="avbRsaPublicKeyBlob">The AVB-format public key blob. <para>AVB 格式公钥数据块。</para></param>
    /// <param name="algorithmType">The libavb algorithm type value. <para>libavb 算法类型值。</para></param>
    /// <returns>The verification result. <para>验证结果。</para></returns>
    public static AvbSignatureVerifyResult VerifySignature(byte[] vbmetaData, byte[] signature, byte[] avbRsaPublicKeyBlob, uint algorithmType)
    {
        if (vbmetaData is not { Length: >= HeaderSize } ||
            !vbmetaData.AsSpan(0, 4).SequenceEqual(MagicBytes))
        {
            return new AvbSignatureVerifyResult { ErrorMessage = "Invalid vbmeta data. 无效的 vbmeta 数据。" };
        }

        using var key = ImportAvbRsaPublicKey(avbRsaPublicKeyBlob);
        if (key == null)
        {
            return new AvbSignatureVerifyResult
            {
                AlgorithmName = GetAlgorithmName(algorithmType),
                ErrorMessage = "Failed to import AVB RSA public key. 无法导入 AVB RSA 公钥。"
            };
        }

        var valid = TryVerifySignature(vbmetaData, signature, key, algorithmType);
        return new AvbSignatureVerifyResult
        {
            IsSigned = true,
            SignatureIsValid = valid,
            AlgorithmName = GetAlgorithmName(algorithmType),
            StoredSignature = signature,
            Descriptors = ExtractDescriptors(vbmetaData)
        };
    }

    /// <summary>
    /// Extracts all descriptors from the vbmeta auxiliary data block.
    /// <para>从 vbmeta 辅助数据块中提取所有描述符。</para>
    /// </summary>
    /// <param name="vbmetaData">The raw vbmeta image bytes. <para>原始 vbmeta 镜像字节。</para></param>
    /// <returns>The parsed descriptors. <para>解析出的描述符。</para></returns>
    public static IReadOnlyList<AvbDescriptorInfo> ExtractDescriptors(byte[] vbmetaData)
    {
        var result = new List<AvbDescriptorInfo>();
        if (vbmetaData is not { Length: >= HeaderSize })
        {
            return result;
        }

        var authSize = (long)BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(12, 8));
        var auxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(20, 8));
        var descriptorsOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(96, 8));
        var descriptorsSize = (long)BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(104, 8));

        var auxStart = HeaderSize + authSize;
        if (auxStart < HeaderSize || auxSize < 0 || descriptorsOffset < 0 || descriptorsSize < 0 ||
            auxStart + auxSize > vbmetaData.Length ||
            descriptorsOffset + descriptorsSize > auxSize)
        {
            return result;
        }

        var cursor = auxStart + descriptorsOffset;
        var end = cursor + descriptorsSize;
        while (cursor + 16 <= end)
        {
            var tag = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan((int)cursor, 8));
            var numBytesFollowing = (long)BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan((int)cursor + 8, 8));
            if (numBytesFollowing < 0 || cursor + 16 + numBytesFollowing > end)
            {
                break;
            }

            result.Add(ParseDescriptor(tag, numBytesFollowing, vbmetaData, (int)cursor + 16));
            cursor += 16 + numBytesFollowing;
        }

        return result;
    }

    private static AvbDescriptorInfo ParseDescriptor(ulong tag, long numBytesFollowing, byte[] data, int bodyStart)
    {
        var info = new AvbDescriptorInfo
        {
            Tag = tag,
            TypeName = GetDescriptorTypeName(tag),
            Size = (ulong)numBytesFollowing
        };

        if (tag == HashDescriptorTag && numBytesFollowing >= 55 && bodyStart + 55 <= data.Length)
        {
            var hashAlgorithm = ReadAscii(data.AsSpan(bodyStart + 20, 32));
            var partitionNameLen = data[bodyStart + 52];
            var saltLen = data[bodyStart + 53];
            var digestLen = data[bodyStart + 54];

            if (bodyStart + 56 + partitionNameLen + saltLen + digestLen <= data.Length)
            {
                var partitionName = Encoding.UTF8.GetString(data, bodyStart + 56, partitionNameLen).TrimEnd('\0');
                var salt = data.AsSpan(bodyStart + 56 + partitionNameLen, saltLen).ToArray();
                var digest = data.AsSpan(bodyStart + 56 + partitionNameLen + saltLen, digestLen).ToArray();
                return new AvbDescriptorInfo
                {
                    Tag = tag,
                    TypeName = GetDescriptorTypeName(tag),
                    Size = (ulong)numBytesFollowing,
                    PartitionName = partitionName,
                    HashAlgorithm = hashAlgorithm,
                    Salt = salt,
                    Digest = digest
                };
            }
        }

        return info;
    }

    private static string GetDescriptorTypeName(ulong tag) => tag switch
    {
        0 => "hash",
        1 => "hashtree",
        2 => "prop",
        3 => "kernel_cmdline",
        4 => "chain",
        _ => "unknown"
    };

    private static bool TryVerifySignature(byte[] vbmetaData, byte[] signature, RSA key, uint algorithmType)
    {
        if (!TryGetKeySizeAndHash(algorithmType, out _, out var hashName))
        {
            return false;
        }

        var authSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(12, 8));
        var auxSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(20, 8));
        if (authSize + auxSize > (ulong)vbmetaData.Length - HeaderSize)
        {
            return false;
        }

        var header = vbmetaData.AsSpan(0, HeaderSize).ToArray();
        var aux = vbmetaData.AsSpan(HeaderSize + (int)authSize, (int)auxSize).ToArray();

        var inputs = new List<byte[]> { Combine(header, aux) };
        var zeroedHeader = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt64BigEndian(zeroedHeader.AsSpan(12, 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(zeroedHeader.AsSpan(20, 8), 0);
        inputs.Add(Combine(zeroedHeader, aux));

        var keyParams = ToRsaKeyParameters(key);
        if (keyParams == null)
        {
            return false;
        }

        foreach (var input in inputs)
        {
            // Spec-strict tooling uses RSASSA-PSS; legacy avbtool forks use PKCS#1 v1.5.
            if (VerifyWithPadding(input, signature, keyParams, hashName, usePss: true) ||
                VerifyWithPadding(input, signature, keyParams, hashName, usePss: false))
            {
                return true;
            }
        }

        return false;
    }

    private static bool VerifyWithPadding(byte[] data, byte[] signature, RsaKeyParameters key, string hashName, bool usePss)
    {
        try
        {
            IDigest digest = hashName == "SHA512" ? new Sha512Digest() : new Sha256Digest();
            ISigner signer = usePss
                ? new PssSigner(new RsaEngine(), digest, digest.GetDigestSize())
                : new RsaDigestSigner(digest);
            signer.Init(false, key);
            signer.BlockUpdate(data, 0, data.Length);
            return signer.VerifySignature(signature);
        }
        catch
        {
            return false;
        }
    }

    private static RsaKeyParameters? ToRsaKeyParameters(RSA key)
    {
        try
        {
            var parameters = key.ExportParameters(false);
            return new RsaKeyParameters(false, new BigInteger(1, parameters.Modulus!), new BigInteger(1, parameters.Exponent!));
        }
        catch
        {
            return null;
        }
    }

    private static (bool Ok, byte[]? StoredHash, byte[]? ComputedHash) VerifyStoredHash(
        byte[] vbmetaData, byte[]? salt, uint algorithmType, ulong hashOffset, ulong hashSize)
    {
        if (hashSize == 0 || hashOffset + hashSize > BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(12, 8)))
        {
            return (false, null, null);
        }

        if (!TryGetKeySizeAndHash(algorithmType, out _, out var hashName))
        {
            return (false, null, null);
        }

        var storedHash = vbmetaData.AsSpan(HeaderSize + (int)hashOffset, (int)hashSize).ToArray();
        var authSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(12, 8));
        var auxSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(20, 8));
        if (authSize + auxSize > (ulong)vbmetaData.Length - HeaderSize)
        {
            return (false, storedHash, null);
        }

        var header = vbmetaData.AsSpan(0, HeaderSize).ToArray();
        var aux = vbmetaData.AsSpan(HeaderSize + (int)authSize, (int)auxSize).ToArray();
        var zeroedHeader = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt64BigEndian(zeroedHeader.AsSpan(12, 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(zeroedHeader.AsSpan(20, 8), 0);

        var candidates = new List<byte[]> { Combine(header, aux), Combine(zeroedHeader, aux) };
        if (salt is { Length: > 0 })
        {
            candidates.Add(Combine(salt, Combine(header, aux)));
            candidates.Add(Combine(salt, Combine(zeroedHeader, aux)));
        }

        foreach (var candidate in candidates)
        {
            var computed = hashName == "SHA512" ? SHA512.HashData(candidate) : SHA256.HashData(candidate);
            if (computed.AsSpan().SequenceEqual(storedHash))
            {
                return (true, storedHash, computed);
            }
        }

        return (false, storedHash, null);
    }

    private static byte[]? FindFirstSalt(IReadOnlyList<AvbDescriptorInfo> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Tag == HashDescriptorTag && descriptor.Salt is { Length: > 0 })
            {
                return descriptor.Salt;
            }
        }

        return null;
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        var combined = new byte[first.Length + second.Length];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);
        return combined;
    }

    private static string ReadAscii(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return length == 0 ? string.Empty : Encoding.ASCII.GetString(bytes[..length]);
    }

    private static AvbSignatureVerifyResult With(AvbSignatureVerifyResult source, bool? IsSigned = null, bool? SignatureIsValid = null,
        bool? HashIsValid = null, string? ErrorMessage = null, string? AlgorithmName = null, byte[]? StoredSignature = null,
        byte[]? StoredHash = null, byte[]? ComputedHash = null, IReadOnlyList<AvbDescriptorInfo>? Descriptors = null)
    {
        return new AvbSignatureVerifyResult
        {
            IsSigned = IsSigned ?? source.IsSigned,
            SignatureIsValid = SignatureIsValid ?? source.SignatureIsValid,
            HashIsValid = HashIsValid ?? source.HashIsValid,
            ErrorMessage = ErrorMessage ?? source.ErrorMessage,
            AlgorithmName = AlgorithmName ?? source.AlgorithmName,
            StoredSignature = StoredSignature ?? source.StoredSignature,
            StoredHash = StoredHash ?? source.StoredHash,
            ComputedHash = ComputedHash ?? source.ComputedHash,
            Descriptors = Descriptors ?? source.Descriptors
        };
    }
}