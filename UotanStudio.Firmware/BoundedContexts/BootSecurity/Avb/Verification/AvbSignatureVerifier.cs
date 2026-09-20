using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;

public sealed class AvbDescriptorInfo
{
    public ulong Tag { get; init; }

    public string TypeName { get; init; } = string.Empty;

    public ulong Size { get; init; }

    public string? PartitionName { get; init; }

    public string? HashAlgorithm { get; init; }

    public byte[]? Salt { get; init; }

    public byte[]? Digest { get; init; }

    public byte[]? RootDigest { get; init; }

    public ulong ImageSize { get; init; }

    public ulong TreeOffset { get; init; }

    public ulong TreeSize { get; init; }

    public uint DataBlockSize { get; init; }

    public uint HashBlockSize { get; init; }

    public uint FecNumRoots { get; init; }

    public ulong FecOffset { get; init; }

    public ulong FecSize { get; init; }

    public uint DmVerityVersion { get; init; }

    public uint Flags { get; init; }

    public string? KernelCmdline { get; init; }

    public uint RollbackIndexLocation { get; init; }

    public byte[]? PublicKey { get; init; }

    public string? Key { get; init; }

    public byte[]? Value { get; init; }
}

public sealed class AvbSignatureVerifyResult
{
    public bool IsSigned { get; init; }

    public bool SignatureIsValid { get; init; }

    public bool HashIsValid { get; init; }

    public string? ErrorMessage { get; init; }

    public string AlgorithmName { get; init; } = string.Empty;

    public byte[]? StoredSignature { get; init; }

    public byte[]? StoredHash { get; init; }

    public byte[]? ComputedHash { get; init; }

    public IReadOnlyList<AvbDescriptorInfo> Descriptors { get; init; } = [];
}

public static class AvbSignatureVerifier
{
    public const int HeaderSize = 256;

    public const uint AlgorithmNone = 0;

    private static ReadOnlySpan<byte> MagicBytes => "AVB0"u8;


    private const int PropertyDescriptorTag = 0;
    private const int HashtreeDescriptorTag = 1;
    private const int HashDescriptorTag = 2;
    private const int KernelCmdlineDescriptorTag = 3;
    private const int ChainPartitionDescriptorTag = 4;
    private const int Exponent = 65537;

    /// <summary>
    /// Maps an algorithm type to its hash name and RSA key size.
    /// <para>将算法类型映射为哈希名称与 RSA 密钥大小。</para>
    /// </summary>
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
    public static string GetAlgorithmName(uint algorithmType)
    {
        return algorithmType == AlgorithmNone
            ? "NONE"
            : TryGetKeySizeAndHash(algorithmType, out var keySizeBits, out var hashName)
            ? $"{hashName}_RSA{keySizeBits}"
            : $"UNKNOWN({algorithmType})";
    }

    /// <summary>
    /// Imports an AVB-format RSA public key blob into an RSA instance.
    /// <para>将 AVB 格式 RSA 公钥数据块导入 RSA 实例。</para>
    /// </summary>
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
        if (avbRsaPublicKeyBlob.Length != 8 + 2 * keyNumBytes)
        {
            return null;
        }

        var modulus = avbRsaPublicKeyBlob.AsSpan(8, keyNumBytes).ToArray();

        if ((modulus[^1] & 1) == 0 || (modulus[0] & 0x80) == 0)
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
    /// Imports a PEM or DER public key / certificate into an RSA instance.
    /// <para>将 PEM 或 DER 公钥 / 证书导入 RSA 实例。</para>
    /// </summary>
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

            }
            catch (CryptographicException)
            {

            }

            try
            {
                rsa.ImportSubjectPublicKeyInfo(pemOrDerData, out _);
                return rsa;
            }
            catch (CryptographicException)
            {

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
    /// Verifies the embedded signature of a vbmeta image, using the public key embedded in its auxiliary data block (self-signed verification).
    /// <para>验证 vbmeta 镜像的内嵌签名，使用其辅助数据块中内嵌的公钥（自签名验证）。</para>
    /// </summary>
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
            AlgorithmName = GetAlgorithmName(algorithmType)
        };

        if (algorithmType == AlgorithmNone)
        {
            return With(result, ErrorMessage: "vbmeta image declares algorithm NONE (not signed). vbmeta 镜像声明算法为 NONE（未签名）。");
        }

        if (!TryGetKeySizeAndHash(algorithmType, out var keySizeBits, out _) ||
            authSize > int.MaxValue || auxSize > int.MaxValue ||
            authSize > (ulong)vbmetaData.Length - HeaderSize ||
            auxSize > (ulong)vbmetaData.Length - HeaderSize - authSize)
        {
            return With(result, ErrorMessage: "vbmeta header declares invalid authentication or auxiliary block sizes. vbmeta 头声明的认证块或辅助块大小无效。");
        }

        result = With(result, Descriptors: ExtractDescriptors(vbmetaData));
        if (sigSize != (ulong)keySizeBits / 8 || sigOffset > authSize || sigSize > authSize - sigOffset ||
            sigOffset > int.MaxValue || sigSize > int.MaxValue)
        {
            return With(result, ErrorMessage: "vbmeta header declares no in-range signature. vbmeta 头未声明有效范围内的签名。");
        }

        var sigStart = HeaderSize + (int)sigOffset;
        var signature = vbmetaData.AsSpan(sigStart, (int)sigSize).ToArray();
        result = With(result, IsSigned: true, StoredSignature: signature);


        var (hashOk, storedHash, computedHash) = VerifyStoredHash(vbmetaData, algorithmType, hashOffset, hashSize);
        result = With(result, HashIsValid: hashOk, StoredHash: storedHash, ComputedHash: computedHash);

        RSA? key = trustedPublicKey;
        var embeddedKey = (RSA?)null;
        try
        {
            if (key == null && pubKeySize > 0 &&
                pubKeyOffset <= auxSize && pubKeySize <= auxSize - pubKeyOffset &&
                pubKeyOffset <= int.MaxValue && pubKeySize <= int.MaxValue)
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

        switch (tag)
        {


            case PropertyDescriptorTag:
                if (numBytesFollowing >= 16 && bodyStart + 16 <= data.Length)
                {
                    var keySize = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(bodyStart, 8));
                    var valueSize = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(bodyStart + 8, 8));
                    if (keySize <= int.MaxValue && valueSize <= int.MaxValue &&
                        bodyStart + 16 + (long)keySize + 1 + (long)valueSize + 1 <= data.Length)
                    {
                        var key = Encoding.UTF8.GetString(data, bodyStart + 16, (int)keySize);
                        var value = data.AsSpan(bodyStart + 16 + (int)keySize + 1, (int)valueSize).ToArray();
                        return new AvbDescriptorInfo
                        {
                            Tag = tag,
                            TypeName = GetDescriptorTypeName(tag),
                            Size = (ulong)numBytesFollowing,
                            Key = key,
                            Value = value
                        };
                    }
                }

                break;


            case HashtreeDescriptorTag:
                if (numBytesFollowing >= 164 && bodyStart + 164 <= data.Length)
                {
                    var hashAlgorithm = ReadAscii(data.AsSpan(bodyStart + 56, 32));
                    var nameLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 88, 4));
                    var saltLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 92, 4));
                    var rootDigestLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 96, 4));
                    if (bodyStart + 164 + (long)nameLen + (long)saltLen + (long)rootDigestLen <= data.Length)
                    {
                        var partitionName = Encoding.UTF8.GetString(data, bodyStart + 164, (int)nameLen).TrimEnd('\0');
                        var salt = data.AsSpan(bodyStart + 164 + (int)nameLen, (int)saltLen).ToArray();
                        var rootDigest = data.AsSpan(bodyStart + 164 + (int)nameLen + (int)saltLen, (int)rootDigestLen).ToArray();
                        return new AvbDescriptorInfo
                        {
                            Tag = tag,
                            TypeName = GetDescriptorTypeName(tag),
                            Size = (ulong)numBytesFollowing,
                            PartitionName = partitionName,
                            HashAlgorithm = hashAlgorithm,
                            Salt = salt,
                            RootDigest = rootDigest,
                            ImageSize = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(bodyStart + 4, 8)),
                            TreeOffset = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(bodyStart + 12, 8)),
                            TreeSize = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(bodyStart + 20, 8)),
                            DataBlockSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 28, 4)),
                            HashBlockSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 32, 4)),
                            FecNumRoots = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 36, 4)),
                            FecOffset = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(bodyStart + 40, 8)),
                            FecSize = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(bodyStart + 48, 8)),
                            DmVerityVersion = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart, 4)),
                            Flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 100, 4))
                        };
                    }
                }

                break;


            case HashDescriptorTag:
                if (numBytesFollowing >= 116 && bodyStart + 116 <= data.Length)
                {
                    var hashAlgorithm = ReadAscii(data.AsSpan(bodyStart + 8, 32));
                    var partitionNameLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 40, 4));
                    var saltLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 44, 4));
                    var digestLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 48, 4));

                    if (bodyStart + 116 + (long)partitionNameLen + (long)saltLen + (long)digestLen <= data.Length)
                    {
                        var partitionName = Encoding.UTF8.GetString(data, bodyStart + 116, (int)partitionNameLen).TrimEnd('\0');
                        var salt = data.AsSpan(bodyStart + 116 + (int)partitionNameLen, (int)saltLen).ToArray();
                        var digest = data.AsSpan(bodyStart + 116 + (int)partitionNameLen + (int)saltLen, (int)digestLen).ToArray();
                        return new AvbDescriptorInfo
                        {
                            Tag = tag,
                            TypeName = GetDescriptorTypeName(tag),
                            Size = (ulong)numBytesFollowing,
                            PartitionName = partitionName,
                            HashAlgorithm = hashAlgorithm,
                            Salt = salt,
                            Digest = digest,
                            ImageSize = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(bodyStart, 8)),
                            Flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 52, 4))
                        };
                    }
                }

                break;


            case KernelCmdlineDescriptorTag:
                if (numBytesFollowing >= 8 && bodyStart + 8 <= data.Length)
                {
                    var cmdlineLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 4, 4));
                    if (cmdlineLen <= int.MaxValue && bodyStart + 8 + (long)cmdlineLen <= data.Length)
                    {
                        var cmdline = Encoding.UTF8.GetString(data, bodyStart + 8, (int)cmdlineLen).TrimEnd('\0');
                        return new AvbDescriptorInfo
                        {
                            Tag = tag,
                            TypeName = GetDescriptorTypeName(tag),
                            Size = (ulong)numBytesFollowing,
                            KernelCmdline = cmdline,
                            Flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart, 4))
                        };
                    }
                }

                break;


            case ChainPartitionDescriptorTag:
                if (numBytesFollowing >= 76 && bodyStart + 76 <= data.Length)
                {
                    var nameLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 4, 4));
                    var keyLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 8, 4));
                    if (bodyStart + 76 + (long)nameLen + (long)keyLen <= data.Length)
                    {
                        var partitionName = Encoding.UTF8.GetString(data, bodyStart + 76, (int)nameLen).TrimEnd('\0');
                        var publicKey = data.AsSpan(bodyStart + 76 + (int)nameLen, (int)keyLen).ToArray();
                        return new AvbDescriptorInfo
                        {
                            Tag = tag,
                            TypeName = GetDescriptorTypeName(tag),
                            Size = (ulong)numBytesFollowing,
                            PartitionName = partitionName,
                            RollbackIndexLocation = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart, 4)),
                            PublicKey = publicKey
                        };
                    }
                }

                break;
        }

        return info;
    }

    private static string GetDescriptorTypeName(ulong tag) => tag switch
    {
        0 => "prop",
        1 => "hashtree",
        2 => "hash",
        3 => "kernel_cmdline",
        4 => "chain",
        _ => "unknown"
    };

    private static bool TryVerifySignature(byte[] vbmetaData, byte[] signature, RSA key, uint algorithmType)
    {
        if (!TryGetKeySizeAndHash(algorithmType, out var keySizeBits, out var hashName) ||
            key.KeySize != keySizeBits || signature.Length != keySizeBits / 8)
        {
            return false;
        }

        var authSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(12, 8));
        var auxSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(20, 8));
        if (authSize > (ulong)vbmetaData.Length - HeaderSize ||
            auxSize > (ulong)vbmetaData.Length - HeaderSize - authSize)
        {
            return false;
        }

        var signedData = Combine(
            vbmetaData.AsSpan(0, HeaderSize).ToArray(),
            vbmetaData.AsSpan(HeaderSize + (int)authSize, (int)auxSize).ToArray());
        var keyParams = ToRsaKeyParameters(key);
        if (keyParams == null)
        {
            return false;
        }

        try
        {
            IDigest digest = hashName == "SHA512" ? new Sha512Digest() : new Sha256Digest();
            ISigner signer = new RsaDigestSigner(digest);
            signer.Init(false, keyParams);
            signer.BlockUpdate(signedData, 0, signedData.Length);
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
        byte[] vbmetaData, uint algorithmType, ulong hashOffset, ulong hashSize)
    {
        var authSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(12, 8));
        if (hashSize == 0 || hashOffset > authSize || hashSize > authSize - hashOffset ||
            hashOffset > int.MaxValue || hashSize > int.MaxValue)
        {
            return (false, null, null);
        }

        if (!TryGetKeySizeAndHash(algorithmType, out _, out var hashName))
        {
            return (false, null, null);
        }

        var storedHash = vbmetaData.AsSpan(HeaderSize + (int)hashOffset, (int)hashSize).ToArray();
        var auxSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaData.AsSpan(20, 8));
        if (authSize + auxSize > (ulong)vbmetaData.Length - HeaderSize)
        {
            return (false, storedHash, null);
        }

        var signedData = Combine(
            vbmetaData.AsSpan(0, HeaderSize).ToArray(),
            vbmetaData.AsSpan(HeaderSize + (int)authSize, (int)auxSize).ToArray());
        var computed = hashName == "SHA512" ? SHA512.HashData(signedData) : SHA256.HashData(signedData);
        return computed.AsSpan().SequenceEqual(storedHash)
            ? (true, storedHash, computed)
            : (false, storedHash, computed);
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