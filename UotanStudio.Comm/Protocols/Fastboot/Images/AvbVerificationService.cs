using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Hash.Digests;
using FirmwareAvbVerifier = UotanStudio.Firmware.BootSecurity.Avb.Verification.AvbSignatureVerifier;

namespace UotanStudio.Comm.Protocols.Fastboot.Images;

/// <summary>
/// AVB 验证结果，包含详细的哈希/签名诊断信息。
/// 使用 Firmware 类型：<see cref="AvbVBMetaImageHeader"/> 和 <see cref="AvbFooter"/>。
/// <para>AVB verification result with detailed hash/signature diagnostics.
/// Uses Firmware types: <see cref="AvbVBMetaImageHeader"/> and <see cref="AvbFooter"/>.</para>
/// </summary>
internal sealed class AvbVerificationResult
{
    /// <summary>
    /// Gets or sets whether hash verification succeeded.
    /// <para>哈希校验是否成功。</para>
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a human-readable message.
    /// <para>人类可读消息。</para>
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the algorithm type (uint, 0-6).
    /// <para>算法类型（uint，0-6）。</para>
    /// </summary>
    public uint AlgorithmType { get; set; }

    /// <summary>Gets or sets the hash stored in the vbmeta header. vbmeta 头中存储的哈希。</summary>
    public byte[]? StoredHash { get; set; }

    /// <summary>
    /// Gets or sets the digest recomputed from the image.
    /// <para>从镜像重新计算的摘要。</para>
    /// </summary>
    public byte[]? ComputedHash { get; set; }

    /// <summary>
    /// Gets or sets whether the recomputed digest matched the stored hash.
    /// <para>重新计算的摘要是否匹配。</para>
    /// </summary>
    public bool HashVerificationSucceeded { get; set; }

    /// <summary>
    /// Gets or sets the signature extracted from the vbmeta image.
    /// <para>从 vbmeta 提取的签名。</para>
    /// </summary>
    public byte[]? Signature { get; set; }

    /// <summary>
    /// Gets or sets the parsed vbmeta header (Firmware type).
    /// <para>解析出的 vbmeta 头（Firmware 类型）。</para>
    /// </summary>
    public AvbVBMetaImageHeader? Header { get; set; }

    /// <summary>
    /// Gets or sets the tag-2 hash descriptors parsed by <see cref="AvbDescriptorReader"/>.
    /// <para>由 <see cref="AvbDescriptorReader"/> 解析出的 tag-2 哈希描述符。</para>
    /// </summary>
    public IReadOnlyList<AvbHashDescriptor> Descriptors { get; set; } = [];

    /// <summary>
    /// Gets or sets whether a valid AVB footer was found.
    /// <para>是否找到有效的 AVB footer。</para>
    /// </summary>
    public bool HasFooter { get; set; }

    /// <summary>Gets or sets the AVB footer if present (Firmware type). AVB footer（Firmware 类型）。</summary>
    public AvbFooter? Footer { get; set; }
}

/// <summary>
/// 签名验证结果。
/// <para>Signature verification result.</para>
/// </summary>
internal sealed class AvbSignatureResult
{
    /// <summary>
    /// Gets or sets whether signature verification succeeded.
    /// <para>签名验证是否成功。</para>
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a human-readable message.
    /// <para>人类可读消息。</para>
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the algorithm name.
    /// <para>算法名称。</para>
    /// </summary>
    public string AlgorithmName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the signature.
    /// <para>签名。</para>
    /// </summary>
    public byte[]? Signature { get; set; }

    /// <summary>
    /// Gets or sets the public key.
    /// <para>公钥。</para>
    /// </summary>
    public byte[]? PublicKey { get; set; }
}

/// <summary>
/// AVB 验证服务：vbmeta 头解析、哈希校验、签名验证。
/// 内部使用 Firmware 的 <see cref="AvbVBMetaImageHeader"/>、<see cref="AvbFooter"/> 和
/// <see cref="FirmwareAvbVerifier"/>，消除 Comm 侧的重复 AVB 解析代码。
/// <para>AVB verification service: vbmeta header parsing, hash verification, signature verification.
/// Internally uses Firmware's <see cref="AvbVBMetaImageHeader"/>, <see cref="AvbFooter"/>, and
/// <see cref="FirmwareAvbVerifier"/>, eliminating duplicate AVB parsing code in Comm.</para>
/// </summary>
internal sealed class AvbVerificationService
{
    /// <summary>短头 vbmeta 镜像的候选头大小。
    /// <para>Candidate header sizes for short-header vbmeta images.</para></summary>
    private static readonly int[] CandidateHeaderSizes = [204, 200, 196];

    /// <summary>
    /// Verifies the integrity of a vbmeta image from raw bytes.
    /// <para>验证原始字节 vbmeta 镜像的完整性。</para>
    /// </summary>
    public AvbVerificationResult VerifyVbmetaImage(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var result = new AvbVerificationResult();
        if (data.Length == 0)
        {
            result.Success = false;
            result.Message = "Vbmeta data is empty. vbmeta 数据为空。";
            return result;
        }

        try
        {
            if (!TryParseHeader(data, out var header))
            {
                result.Success = false;
                result.Message = "Not a valid AVB vbmeta image (bad magic or short header). 不是有效的 AVB vbmeta 镜像。";
                return result;
            }

            result.Header = header;
            result.AlgorithmType = header.AlgorithmType;

            if (header.HashSize > 0)
            {
                result.StoredHash = CopyRegion(data, AvbVBMetaImageHeader.Size + (long)header.HashOffset, (long)header.HashSize);
            }

            if (header.SignatureSize > 0)
            {
                result.Signature = CopyRegion(data, AvbVBMetaImageHeader.Size + (long)header.SignatureOffset, (long)header.SignatureSize);
            }

            result.Descriptors = AvbDescriptorReader.ExtractHashDescriptors(data);

            if (result.StoredHash is { Length: > 0 })
            {
                byte[]? computed = null;
                foreach (var headerSize in new[] { AvbVBMetaImageHeader.Size }.Concat(CandidateHeaderSizes))
                {
                    try { computed = ComputeVbmetaDigest(data, header, headerSize); }
                    catch (InvalidDataException) { continue; }

                    if (computed != null && CryptographicOperations.FixedTimeEquals(computed, result.StoredHash))
                    {
                        result.ComputedHash = computed;
                        result.HashVerificationSucceeded = true;
                        break;
                    }
                }

                result.ComputedHash ??= computed;
                result.Success = result.HashVerificationSucceeded;
                result.Message = result.HashVerificationSucceeded
                    ? "VBMeta image hash verification succeeded. vbmeta 镜像哈希校验成功。"
                    : "VBMeta image hash verification failed. vbmeta 镜像哈希校验失败。";
            }
            else
            {
                result.Success = false;
                result.Message = "Vbmeta header declares no hash; nothing to verify. vbmeta 头未声明哈希。";
            }

            if (data.Length >= AvbFooter.Size)
            {
                var footer = AvbFooter.TryLoad(data);
                if (footer.HasValue)
                {
                    result.HasFooter = true;
                    result.Footer = footer;
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Failed to parse or verify vbmeta image: {ex.Message} 解析或校验失败。";
        }

        return result;
    }

    /// <summary>
    /// Verifies the integrity of a vbmeta image from a file.
    /// <para>从文件验证 vbmeta 镜像的完整性。</para>
    /// </summary>
    public AvbVerificationResult VerifyVbmetaFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Vbmeta image file not found: {filePath}", filePath);
        return VerifyVbmetaImage(File.ReadAllBytes(filePath));
    }

    /// <summary>
    /// Returns whether the byte array starts with the vbmeta magic ("AVB0").
    /// <para>返回字节数组是否以 vbmeta 魔数（"AVB0"）开头。</para>
    /// </summary>
    public bool IsVbmetaImage(byte[] data) => data != null && data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("AVB0"u8);

    /// <summary>
    /// Recomputes the AVB digest of a vbmeta image.
    /// <para>重新计算 vbmeta 镜像的 AVB 摘要。</para>
    /// </summary>
    public byte[]? ComputeVbmetaDigest(byte[] data)
    {
        if (data == null) return null;
        if (!TryParseHeader(data, out var header)) return null;
        return ComputeVbmetaDigest(data, header, AvbVBMetaImageHeader.Size);
    }

    /// <summary>
    /// Verifies the signature of a vbmeta image using Firmware's verifier.
    /// <para>使用 Firmware 的验证器验证 vbmeta 镜像的签名。</para>
    /// </summary>
    public AvbSignatureResult VerifySignature(byte[] data, byte[] signature, byte[] publicKey, uint algorithmType)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(publicKey);
        if (!FirmwareAvbVerifier.TryGetKeySizeAndHash(algorithmType, out _, out _))
            throw new ArgumentException($"Unsupported AVB algorithm type: {algorithmType}", nameof(algorithmType));

        var verifyResult = FirmwareAvbVerifier.VerifySignature(data, signature, publicKey, algorithmType);
        return new AvbSignatureResult
        {
            Success = verifyResult.SignatureIsValid,
            Message = verifyResult.SignatureIsValid
                ? "AVB signature verification succeeded. AVB 签名验证成功。"
                : $"AVB signature verification failed. {(string.IsNullOrEmpty(verifyResult.ErrorMessage) ? string.Empty : $"({verifyResult.ErrorMessage})")}",
            AlgorithmName = verifyResult.AlgorithmName,
            Signature = signature,
            PublicKey = publicKey
        };
    }

    /// <summary>
    /// Extracts all tag-2 hash descriptors through the Firmware reader, preserving the lenient empty-list contract.
    /// <para>经 Firmware 读取器提取所有 tag-2 哈希描述符，保持空列表的容错约定。</para>
    /// </summary>
    public IReadOnlyList<AvbHashDescriptor> ExtractHashDescriptors(byte[] data)
        => data is null ? [] : AvbDescriptorReader.ExtractHashDescriptors(data);

    /// <summary>
    /// Attempts to parse a vbmeta header, supporting both the 256-byte and the shorter 196-204-byte layouts.
    /// <para>尝试解析 vbmeta 头，支持 256 字节与 196-204 字节两种头部布局。</para>
    /// </summary>
    private static bool TryParseHeader(byte[] data, out AvbVBMetaImageHeader header)
    {
        header = default;
        if (data == null || data.Length < 80 || !data.AsSpan(0, 4).SequenceEqual("AVB0"u8))
            return false;

        if (data.Length >= AvbVBMetaImageHeader.Size)
        {
            try { header = AvbVBMetaImageHeader.FromBytes(data); return true; }
            catch { }
        }

        ReadOnlySpan<byte> s = data;
        header = new AvbVBMetaImageHeader
        {
            Magic = Encoding.ASCII.GetString(s[..4]),
            RequiredLibavbVersionMajor = BinaryPrimitives.ReadUInt32BigEndian(s[4..]),
            RequiredLibavbVersionMinor = BinaryPrimitives.ReadUInt32BigEndian(s[8..]),
            AuthenticationDataBlockSize = BinaryPrimitives.ReadUInt64BigEndian(s[12..]),
            AuxiliaryDataBlockSize = BinaryPrimitives.ReadUInt64BigEndian(s[20..]),
            AlgorithmType = BinaryPrimitives.ReadUInt32BigEndian(s[28..]),
            HashOffset = BinaryPrimitives.ReadUInt64BigEndian(s[32..]),
            HashSize = BinaryPrimitives.ReadUInt64BigEndian(s[40..]),
            SignatureOffset = BinaryPrimitives.ReadUInt64BigEndian(s[48..]),
            SignatureSize = BinaryPrimitives.ReadUInt64BigEndian(s[56..]),
            PublicKeyOffset = data.Length >= 72 ? BinaryPrimitives.ReadUInt64BigEndian(s[64..]) : 0,
            PublicKeySize = data.Length >= 76 ? BinaryPrimitives.ReadUInt64BigEndian(s[68..]) : 0,
        };

        if (data.Length >= 160)
        {
            header = header with
            {
                RollbackIndex = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(140, 8)),
                Flags = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(148, 4)),
                RollbackIndexLocation = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(152, 4)),
                ReleaseString = DecodeAscii(s.Slice(156, Math.Min(48, data.Length - 156)))
            };
        }

        return true;
    }

    private static string GetHashAlgorithmName(uint type) => type switch
    {
        1 or 2 or 3 => "SHA256",
        4 or 5 or 6 => "SHA512",
        _ => string.Empty
    };

    private byte[]? ComputeVbmetaDigest(byte[] data, AvbVBMetaImageHeader header, int headerSize)
    {
        var algorithmName = GetHashAlgorithmName(header.AlgorithmType);
        if (string.IsNullOrEmpty(algorithmName)) return null;

        var hashInput = BuildHashInput(data, header, headerSize);
        var salt = ExtractSalt(data) ?? [];

        if (salt.Length == 0)
            return algorithmName == "SHA512" ? Sha512.ComputeHash(hashInput) : Sha256.ComputeHash(hashInput);

        var combined = new byte[salt.Length + hashInput.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(hashInput, 0, combined, salt.Length, hashInput.Length);
        return algorithmName == "SHA512" ? Sha512.ComputeHash(combined) : Sha256.ComputeHash(combined);
    }

    private static byte[] BuildHashInput(byte[] data, AvbVBMetaImageHeader header, int headerSize)
    {
        if (headerSize < 80 || data.Length < headerSize)
            throw new InvalidDataException("Vbmeta image is shorter than its header.");

        var headerBytes = new byte[headerSize];
        Buffer.BlockCopy(data, 0, headerBytes, 0, headerSize);
        BinaryPrimitives.WriteUInt64BigEndian(headerBytes.AsSpan(12, 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(headerBytes.AsSpan(20, 8), 0);

        var auxStart = headerSize + (long)header.AuthenticationDataBlockSize;
        var auxSize = (long)header.AuxiliaryDataBlockSize;
        if (auxStart < 0 || auxSize < 0 || auxStart + auxSize > data.Length)
            throw new InvalidDataException("Vbmeta auxiliary data block is out of range.");

        var input = new byte[headerSize + auxSize];
        Buffer.BlockCopy(headerBytes, 0, input, 0, headerSize);
        Buffer.BlockCopy(data, (int)auxStart, input, headerSize, (int)auxSize);
        return input;
    }

    private static byte[]? ExtractSalt(byte[] data)
    {
        foreach (var descriptor in AvbDescriptorReader.ExtractHashDescriptors(data))
        {
            if (descriptor.Salt.Length > 0) return descriptor.Salt;
        }
        return null;
    }

    private static byte[]? CopyRegion(byte[] data, long start, long length)
    {
        if (start < 0 || length < 0 || start + length > data.Length) return null;
        var result = new byte[length];
        Buffer.BlockCopy(data, (int)start, result, 0, (int)length);
        return result;
    }

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0) length = bytes.Length;
        return length == 0 ? string.Empty : Encoding.ASCII.GetString(bytes[..length]);
    }
}