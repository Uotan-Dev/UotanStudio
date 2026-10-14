using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.Core.Text;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.BootSecurity.Avb.Verification;

/// <summary>
/// A parsed view of one AVB descriptor from the vbmeta auxiliary data block; fields that do not belong
/// to the descriptor's tag keep their default (<see langword="null"/> or zero).
/// <para>对 vbmeta 辅助数据块中单个 AVB 描述符的解析视图；不属于该描述符标签的字段保持默认值（<see langword="null"/> 或零）。</para>
/// </summary>
public sealed class AvbDescriptorInfo
{
    /// <summary>Numeric tag identifying which kind of descriptor this entry was parsed from.
    /// <para>标识该条目解析自哪种描述符的数字标签。</para></summary>
    /// <value><c>0</c> property, <c>1</c> hashtree, <c>2</c> hash, <c>3</c> kernel command line, <c>4</c> chain partition; other values are kept undecoded.
    /// <para><c>0</c> 属性、<c>1</c> 哈希树、<c>2</c> 哈希、<c>3</c> 内核命令行、<c>4</c> 链式分区；其他值保留但不解码。</para></value>
    public ulong Tag { get; init; }

    /// <summary>Display name of the descriptor kind derived from <see cref="Tag"/>.
    /// <para>由 <see cref="Tag"/> 推导的描述符种类显示名。</para></summary>
    /// <value><c>prop</c>, <c>hashtree</c>, <c>hash</c>, <c>kernel_cmdline</c>, <c>chain</c> or <c>unknown</c>; <see cref="string.Empty"/> when the entry was never populated.
    /// <para><c>prop</c>、<c>hashtree</c>、<c>hash</c>、<c>kernel_cmdline</c>、<c>chain</c> 或 <c>unknown</c>；条目未被填充时为 <see cref="string.Empty"/>。</para></value>
    public string TypeName { get; init; } = string.Empty;

    /// <summary>Byte length of the descriptor body following its 16-byte tag-and-size header.
    /// <para>描述符主体在 16 字节标签与长度头之后的字节数。</para></summary>
    /// <value>The num_bytes_following value, already padded to 8-byte alignment.
    /// <para>num_bytes_following 值，已填充至 8 字节对齐。</para></value>
    public ulong Size { get; init; }

    /// <summary>Partition name the descriptor refers to, decoded and trimmed of trailing NUL characters.
    /// <para>描述符所引用的分区名，已解码并去除结尾 NUL 字符。</para></summary>
    /// <value>Populated for hash, hashtree and chain partition descriptors; <see langword="null"/> for other kinds.
    /// <para>仅在哈希、哈希树与链式分区描述符中有值；其他类型为 <see langword="null"/>。</para></value>
    public string? PartitionName { get; init; }

    /// <summary>ASCII hash algorithm name recorded in the descriptor, e.g. <c>sha256</c>.
    /// <para>描述符中记录的 ASCII 哈希算法名，如 <c>sha256</c>。</para></summary>
    /// <value>Populated for hash and hashtree descriptors; <see langword="null"/> for other kinds.
    /// <para>仅在哈希与哈希树描述符中有值；其他类型为 <see langword="null"/>。</para></value>
    public string? HashAlgorithm { get; init; }

    /// <summary>Salt bytes bound into the hash or dm-verity tree construction.
    /// <para>参与哈希或 dm-verity 树构造的盐值字节。</para></summary>
    /// <value>Populated for hash and hashtree descriptors; <see langword="null"/> for other kinds.
    /// <para>仅在哈希与哈希树描述符中有值；其他类型为 <see langword="null"/>。</para></value>
    public byte[]? Salt { get; init; }

    /// <summary>Digest of the partition image content stored by a hash descriptor.
    /// <para>哈希描述符存储的分区镜像内容摘要。</para></summary>
    /// <value>Non-<see langword="null"/> only for hash descriptors; <see langword="null"/> for other kinds.
    /// <para>仅哈希描述符有值；其他类型为 <see langword="null"/>。</para></value>
    public byte[]? Digest { get; init; }

    /// <summary>dm-verity Merkle root digest stored by a hashtree descriptor.
    /// <para>哈希树描述符存储的 dm-verity Merkle 根摘要。</para></summary>
    /// <value>Non-<see langword="null"/> only for hashtree descriptors; <see langword="null"/> for other kinds.
    /// <para>仅哈希树描述符有值；其他类型为 <see langword="null"/>。</para></value>
    public byte[]? RootDigest { get; init; }

    /// <summary>Byte size of the image content covered by the descriptor.
    /// <para>描述符所覆盖的镜像内容字节大小。</para></summary>
    /// <value>Content size for hash descriptors and data-region size for hashtree descriptors; <c>0</c> for other kinds.
    /// <para>哈希描述符为内容大小，哈希树描述符为数据区大小；其他类型为 <c>0</c>。</para></value>
    public ulong ImageSize { get; init; }

    /// <summary>Offset of the dm-verity hash tree inside the partition image.
    /// <para>dm-verity 哈希树在分区镜像内的偏移。</para></summary>
    /// <value>Meaningful only for hashtree descriptors; <c>0</c> for other kinds.
    /// <para>仅哈希树描述符有意义；其他类型为 <c>0</c>。</para></value>
    public ulong TreeOffset { get; init; }

    /// <summary>Byte size of the dm-verity hash tree.
    /// <para>dm-verity 哈希树的字节大小。</para></summary>
    /// <value>Meaningful only for hashtree descriptors; <c>0</c> for other kinds.
    /// <para>仅哈希树描述符有意义；其他类型为 <c>0</c>。</para></value>
    public ulong TreeSize { get; init; }

    /// <summary>dm-verity data block size in bytes.
    /// <para>dm-verity 数据块大小（字节）。</para></summary>
    /// <value>Meaningful only for hashtree descriptors; <c>0</c> for other kinds.
    /// <para>仅哈希树描述符有意义；其他类型为 <c>0</c>。</para></value>
    public uint DataBlockSize { get; init; }

    /// <summary>dm-verity hash block size in bytes.
    /// <para>dm-verity 哈希块大小（字节）。</para></summary>
    /// <value>Meaningful only for hashtree descriptors; <c>0</c> for other kinds.
    /// <para>仅哈希树描述符有意义；其他类型为 <c>0</c>。</para></value>
    public uint HashBlockSize { get; init; }

    /// <summary>Number of forward-error-correction roots declared by a hashtree descriptor.
    /// <para>哈希树描述符声明的前向纠错根数量。</para></summary>
    /// <value><c>0</c> when FEC is unused or the descriptor kind is not hashtree.
    /// <para>未使用 FEC 或类型不是哈希树时为 <c>0</c>。</para></value>
    public uint FecNumRoots { get; init; }

    /// <summary>Offset of the FEC data inside the partition image.
    /// <para>FEC 数据在分区镜像内的偏移。</para></summary>
    /// <value>Meaningful only for hashtree descriptors; <c>0</c> for other kinds.
    /// <para>仅哈希树描述符有意义；其他类型为 <c>0</c>。</para></value>
    public ulong FecOffset { get; init; }

    /// <summary>Byte size of the FEC data.
    /// <para>FEC 数据的字节大小。</para></summary>
    /// <value>Meaningful only for hashtree descriptors; <c>0</c> for other kinds.
    /// <para>仅哈希树描述符有意义；其他类型为 <c>0</c>。</para></value>
    public ulong FecSize { get; init; }

    /// <summary>dm-verity on-disk format version declared by a hashtree descriptor.
    /// <para>哈希树描述符声明的 dm-verity 磁盘格式版本。</para></summary>
    /// <value>Typically <c>1</c>; <c>0</c> for other descriptor kinds.
    /// <para>通常为 <c>1</c>；其他类型为 <c>0</c>。</para></value>
    public uint DmVerityVersion { get; init; }

    /// <summary>Raw descriptor flags word.
    /// <para>描述符原始标志字。</para></summary>
    /// <value>Interpretation depends on the descriptor kind (hash, hashtree, kernel command line, chain); <c>0</c> when unset.
    /// <para>含义取决于描述符类型（哈希、哈希树、内核命令行、链式）；未设置时为 <c>0</c>。</para></value>
    public uint Flags { get; init; }

    /// <summary>Kernel command line string carried by a kernel command line descriptor.
    /// <para>内核命令行描述符携带的命令行字符串。</para></summary>
    /// <value>UTF-8 decoded and trimmed of trailing NUL characters; <see langword="null"/> for other kinds.
    /// <para>UTF-8 解码并去除结尾 NUL 字符；其他类型为 <see langword="null"/>。</para></value>
    public string? KernelCmdline { get; init; }

    /// <summary>Rollback index location declared by a chain partition descriptor.
    /// <para>链式分区描述符声明的回滚索引位置。</para></summary>
    /// <value>Meaningful only for chain descriptors; <c>0</c> for other kinds.
    /// <para>仅链式描述符有意义；其他类型为 <c>0</c>。</para></value>
    public uint RollbackIndexLocation { get; init; }

    /// <summary>Public key blob a chain partition descriptor pins for the child partition.
    /// <para>链式分区描述符为子分区固定的公钥数据块。</para></summary>
    /// <value>Non-<see langword="null"/> only for chain descriptors; <see langword="null"/> for other kinds.
    /// <para>仅链式描述符有值；其他类型为 <see langword="null"/>。</para></value>
    public byte[]? PublicKey { get; init; }

    /// <summary>Property key carried by a property descriptor.
    /// <para>属性描述符携带的属性键。</para></summary>
    /// <value>UTF-8 decoded; <see langword="null"/> for other kinds.
    /// <para>UTF-8 解码；其他类型为 <see langword="null"/>。</para></value>
    public string? Key { get; init; }

    /// <summary>Property value bytes carried by a property descriptor.
    /// <para>属性描述符携带的属性值字节。</para></summary>
    /// <value>Non-<see langword="null"/> only for property descriptors; <see langword="null"/> for other kinds.
    /// <para>仅属性描述符有值；其他类型为 <see langword="null"/>。</para></value>
    public byte[]? Value { get; init; }
}

/// <summary>
/// Outcome of verifying a vbmeta image: signature and stored-hash validity, the algorithm used, the raw
/// cryptographic material and the descriptors parsed from the auxiliary data block.
/// <para>vbmeta 镜像校验结果：签名与存储哈希是否有效、所用算法、原始密码学材料，以及从辅助数据块解析出的描述符。</para>
/// </summary>
public sealed class AvbSignatureVerifyResult
{
    /// <summary>Whether a signature of exactly the algorithm key size was located in range inside the authentication block.
    /// <para>是否在认证块内的有效范围内找到长度恰为算法密钥长度的签名。</para></summary>
    /// <value><see langword="true"/> only when the signature was extracted; <see langword="false"/> for unsigned images and every error path.
    /// <para>仅在成功提取签名时为 <see langword="true"/>；未签名镜像及所有错误路径为 <see langword="false"/>。</para></value>
    public bool IsSigned { get; init; }

    /// <summary>Whether the RSA signature over the header plus auxiliary block verified against the trusted or embedded public key.
    /// <para>头部加辅助块的 RSA 签名是否通过受信任或内嵌公钥验证。</para></summary>
    /// <value><see langword="true"/> only on successful verification; <see langword="false"/> when no key was available or verification failed.
    /// <para>仅验证成功时为 <see langword="true"/>；无可用密钥或验证失败时为 <see langword="false"/>。</para></value>
    public bool SignatureIsValid { get; init; }

    /// <summary>Whether the hash stored in the authentication block matches the recomputed SHA-256 or SHA-512 over the signing input.
    /// <para>认证块中存储的哈希是否与重算的 SHA-256/SHA-512 一致。</para></summary>
    /// <value><see langword="true"/> when the stored hash matches; <see langword="false"/> when absent, out of range or mismatched.
    /// <para>匹配时为 <see langword="true"/>；缺失、越界或不一致时为 <see langword="false"/>。</para></value>
    public bool HashIsValid { get; init; }

    /// <summary>Combined English and Chinese description of why verification could not complete.
    /// <para>说明校验无法完成的英文与中文合并描述。</para></summary>
    /// <value><see langword="null"/> when the attempt recorded no failure; otherwise a human-readable message.
    /// <para>本次尝试未记录失败时为 <see langword="null"/>；否则为可读消息。</para></value>
    public string? ErrorMessage { get; init; }

    /// <summary>Display name of the algorithm declared by the vbmeta header.
    /// <para>vbmeta 头声明的算法显示名。</para></summary>
    /// <value>E.g. <c>SHA256_RSA4096</c>, <c>NONE</c> or <c>UNKNOWN(n)</c>; <see cref="string.Empty"/> until the header is read.
    /// <para>如 <c>SHA256_RSA4096</c>、<c>NONE</c> 或 <c>UNKNOWN(n)</c>；头部读取前为 <see cref="string.Empty"/>。</para></value>
    public string AlgorithmName { get; init; } = string.Empty;

    /// <summary>Signature bytes read from the authentication block.
    /// <para>从认证块读取的签名字节。</para></summary>
    /// <value><see langword="null"/> when no in-range signature was found.
    /// <para>未找到有效范围内签名时为 <see langword="null"/>。</para></value>
    public byte[]? StoredSignature { get; init; }

    /// <summary>Hash bytes stored in the authentication block.
    /// <para>认证块中存储的哈希字节。</para></summary>
    /// <value><see langword="null"/> when the declared hash range is invalid or absent.
    /// <para>声明的哈希范围无效或缺失时为 <see langword="null"/>。</para></value>
    public byte[]? StoredHash { get; init; }

    /// <summary>Hash recomputed by this verifier over the header plus auxiliary block.
    /// <para>由本验证器对头部加辅助块重算的哈希。</para></summary>
    /// <value><see langword="null"/> when the auxiliary range is out of bounds or the algorithm is unknown.
    /// <para>辅助块范围越界或算法未知时为 <see langword="null"/>。</para></value>
    public byte[]? ComputedHash { get; init; }

    /// <summary>Descriptors parsed from the auxiliary data block of a signed, structurally valid vbmeta.
    /// <para>从已签名且结构有效的 vbmeta 辅助数据块解析出的描述符。</para></summary>
    /// <value>An immutable snapshot; empty when parsing stopped early or the image was rejected before descriptor extraction.
    /// <para>不可变快照；提前停止解析或镜像在描述符提取前即被拒绝时为空。</para></value>
    public IReadOnlyList<AvbDescriptorInfo> Descriptors { get; init; } = [];
}

/// <summary>
/// Verifies AVB vbmeta signatures and stored hashes against a trusted or embedded RSA public key, and decodes
/// the descriptors carried in the auxiliary data block. All parsing is bounds-checked: malformed input produces
/// error results or empty collections instead of exceptions.
/// <para>对照受信任或内嵌的 RSA 公钥验证 AVB vbmeta 的签名与存储哈希，并解码辅助数据块中的描述符。
/// 所有解析均做边界检查：畸形输入返回错误结果或空集合，而不抛出异常。</para>
/// </summary>
public static partial class AvbSignatureVerifier
{
    /// <summary>Size of the fixed AVB0 vbmeta header in bytes. <para>固定 AVB0 vbmeta 头部的字节大小。</para></summary>
    public const int HeaderSize = 256;

    /// <summary>Algorithm type value declaring an unsigned vbmeta image. <para>声明 vbmeta 镜像未签名的算法类型值。</para></summary>
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
    /// <param name="algorithmType">
    /// Header algorithm type: <c>1</c>–<c>3</c> are SHA-256 RSA, <c>4</c>–<c>6</c> SHA-512 RSA, <see cref="AlgorithmNone"/> is unsigned.
    /// <para>头部算法类型：<c>1</c>–<c>3</c> 为 SHA-256 RSA，<c>4</c>–<c>6</c> 为 SHA-512 RSA，<see cref="AlgorithmNone"/> 表示未签名。</para>
    /// </param>
    /// <param name="keySizeBits">
    /// RSA modulus size in bits (2048, 4096 or 8192); <c>0</c> for <see cref="AlgorithmNone"/> and unknown types.
    /// <para>RSA 模数位数（2048、4096 或 8192）；<see cref="AlgorithmNone"/> 与未知类型为 <c>0</c>。</para>
    /// </param>
    /// <param name="hashName">
    /// <c>SHA256</c> or <c>SHA512</c>; <see cref="string.Empty"/> for <see cref="AlgorithmNone"/> and unknown types.
    /// <para><c>SHA256</c> 或 <c>SHA512</c>；<see cref="AlgorithmNone"/> 与未知类型为空字符串。</para>
    /// </param>
    /// <returns><see langword="true"/> when the type is recognized (including <see cref="AlgorithmNone"/>); <see langword="false"/> for unknown types.
    /// <para>类型可识别（含 <see cref="AlgorithmNone"/>）时返回 <see langword="true"/>；未知类型返回 <see langword="false"/>。</para></returns>
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
    /// <param name="algorithmType">Header algorithm type word. <para>头部算法类型字。</para></param>
    /// <returns><c>NONE</c> for <see cref="AlgorithmNone"/>, a name like <c>SHA512_RSA4096</c> for known types, otherwise <c>UNKNOWN(n)</c>.
    /// <para><see cref="AlgorithmNone"/> 返回 <c>NONE</c>，已知类型返回形如 <c>SHA512_RSA4096</c> 的名称，否则返回 <c>UNKNOWN(n)</c>。</para></returns>
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
    /// <param name="avbRsaPublicKeyBlob">
    /// libavb public-key blob: 8-byte header plus modulus and R² words; <see langword="null"/> yields <see langword="null"/>.
    /// <para>libavb 公钥数据块：8 字节头加模数与 R² 字；传入 <see langword="null"/> 返回 <see langword="null"/>。</para>
    /// </param>
    /// <returns>An <see cref="RSA"/> instance to dispose after use, or <see langword="null"/> when the blob fails structural or Montgomery-field validation.
    /// <para>用后需释放的 <see cref="RSA"/> 实例；数据块未通过结构或 Montgomery 域校验时返回 <see langword="null"/>。</para></returns>
    public static RSA? ImportAvbRsaPublicKey(byte[]? avbRsaPublicKeyBlob)
        => avbRsaPublicKeyBlob is null ? null : AvbRsaPublicKeyCodec.TryImport(avbRsaPublicKeyBlob);

    /// <summary>
    /// Imports a PEM or DER public key / certificate into an RSA instance.
    /// <para>将 PEM 或 DER 公钥 / 证书导入 RSA 实例。</para>
    /// </summary>
    /// <param name="pemOrDerData">
    /// Candidate key material tried in order as PEM text, SubjectPublicKeyInfo DER, then an X.509 certificate; <see langword="null"/> or empty yields <see langword="null"/>.
    /// <para>候选密钥材料，依次按 PEM 文本、SubjectPublicKeyInfo DER、X.509 证书尝试；<see langword="null"/> 或空数组返回 <see langword="null"/>。</para>
    /// </param>
    /// <returns>An <see cref="RSA"/> instance to dispose after use, or <see langword="null"/> when none of the encodings parses.
    /// <para>用后需释放的 <see cref="RSA"/> 实例；所有编码均无法解析时返回 <see langword="null"/>。</para></returns>
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
    /// <param name="vbmetaData">
    /// Untrusted full vbmeta buffer; every header-declared offset and size is bounds-checked and reported as an error result instead of throwing.
    /// <para>不可信的完整 vbmeta 缓冲区；头部声明的每个偏移与大小都做边界检查，以错误结果而非异常报告。</para>
    /// </param>
    /// <param name="trustedPublicKey">
    /// External RSA public key to verify against; when <see langword="null"/>, the key embedded in the auxiliary block is used.
    /// <para>用于验证的外部 RSA 公钥；为 <see langword="null"/> 时使用辅助块中内嵌的密钥。</para>
    /// </param>
    /// <returns>
    /// Verification outcome with hash and signature flags, raw material and parsed descriptors; <see cref="AvbSignatureVerifyResult.ErrorMessage"/>
    /// explains any rejected input. This method does not throw on malformed data.
    /// <para>包含哈希与签名标志、原始材料及已解析描述符的校验结果；被拒绝的输入由 <see cref="AvbSignatureVerifyResult.ErrorMessage"/> 说明。本方法对畸形数据不抛异常。</para>
    /// </returns>
    /// <example>
    /// <code>
    /// var result = AvbSignatureVerifier.Verify(File.ReadAllBytes("vbmeta.img"));
    /// bool trusted = result.IsSigned &amp;&amp; result.SignatureIsValid &amp;&amp; result.HashIsValid;
    /// </code>
    /// </example>
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

        try
        {
            result = With(result, Descriptors: ReadDescriptorsStrict(vbmetaData));
        }
        catch (InvalidDataException ex)
        {
            return With(result, ErrorMessage: ex.Message);
        }
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
    /// <param name="vbmetaData">Full vbmeta buffer; must be at least 256 bytes with the AVB0 magic, otherwise an error result is returned.
    /// <para>完整 vbmeta 缓冲区；至少 256 字节且带 AVB0 魔数，否则返回错误结果。</para></param>
    /// <param name="signature">Raw RSA signature bytes; a length other than key-size/8 fails verification.
    /// <para>原始 RSA 签名字节；长度不等于密钥字节数时验证失败。</para></param>
    /// <param name="avbRsaPublicKeyBlob">libavb RSA public-key blob to import and verify against.
    /// <para>用于导入并验证的 libavb RSA 公钥数据块。</para></param>
    /// <param name="algorithmType">Algorithm type selecting the digest and expected key size.
    /// <para>算法类型，决定摘要算法与预期密钥大小。</para></param>
    /// <returns>Result with <see cref="AvbSignatureVerifyResult.IsSigned"/> set and <see cref="AvbSignatureVerifyResult.SignatureIsValid"/> reflecting the check;
    /// error results for bad magic, a failed key import, or a too-short buffer.
    /// <para>结果中 <see cref="AvbSignatureVerifyResult.IsSigned"/> 已置位，<see cref="AvbSignatureVerifyResult.SignatureIsValid"/> 反映验证结论；魔数错误、密钥导入失败或缓冲区过短返回错误结果。</para></returns>
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

        IReadOnlyList<AvbDescriptorInfo> descriptors;
        try
        {
            descriptors = ReadDescriptorsStrict(vbmetaData);
        }
        catch (InvalidDataException ex)
        {
            return new AvbSignatureVerifyResult { ErrorMessage = ex.Message };
        }
        var valid = TryVerifySignature(vbmetaData, signature, key, algorithmType);
        return new AvbSignatureVerifyResult
        {
            IsSigned = true,
            SignatureIsValid = valid,
            AlgorithmName = GetAlgorithmName(algorithmType),
            StoredSignature = signature,
            Descriptors = descriptors
        };
    }

    /// <summary>
    /// Extracts all descriptors from the vbmeta auxiliary data block.
    /// <para>从 vbmeta 辅助数据块中提取所有描述符。</para>
    /// </summary>
    /// <param name="vbmetaData">Untrusted vbmeta buffer; header-declared ranges are validated before any read.
    /// <para>不可信的 vbmeta 缓冲区；读取前先校验头部声明的范围。</para></param>
    /// <returns>One entry per descriptor, or an empty list if any descriptor is malformed or exceeds the inspection limits.
    /// Use <see cref="ReadDescriptorsStrict"/> when an empty list must be distinguished from invalid input.
    /// <para>每个描述符一条记录；任一描述符畸形或超出检查限制时返回空列表。需要区分空列表与无效输入时使用 <see cref="ReadDescriptorsStrict"/>。</para></returns>
    public static IReadOnlyList<AvbDescriptorInfo> ExtractDescriptors(byte[] vbmetaData)
    {
        try
        {
            return ReadDescriptorsStrict(vbmetaData);
        }
        catch (InvalidDataException)
        {
            return [];
        }
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
                    var hashAlgorithm = NulText.ReadCString(data.AsSpan(bodyStart + 56, 32), Encoding.ASCII);
                    var nameLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 88, 4));
                    var saltLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 92, 4));
                    var rootDigestLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 96, 4));
                    if (bodyStart + 164 + (long)nameLen + (long)saltLen + (long)rootDigestLen <= data.Length)
                    {
                        var partitionName = Encoding.UTF8.GetStringTrimNul(data, bodyStart + 164, (int)nameLen);
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
                    var hashAlgorithm = NulText.ReadCString(data.AsSpan(bodyStart + 8, 32), Encoding.ASCII);
                    var partitionNameLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 40, 4));
                    var saltLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 44, 4));
                    var digestLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 48, 4));

                    if (bodyStart + 116 + (long)partitionNameLen + (long)saltLen + (long)digestLen <= data.Length)
                    {
                        var partitionName = Encoding.UTF8.GetStringTrimNul(data, bodyStart + 116, (int)partitionNameLen);
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
                        var cmdline = Encoding.UTF8.GetStringTrimNul(data, bodyStart + 8, (int)cmdlineLen);
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
                        var partitionName = Encoding.UTF8.GetStringTrimNul(data, bodyStart + 76, (int)nameLen);
                        var publicKey = data.AsSpan(bodyStart + 76 + (int)nameLen, (int)keyLen).ToArray();
                        return new AvbDescriptorInfo
                        {
                            Tag = tag,
                            TypeName = GetDescriptorTypeName(tag),
                            Size = (ulong)numBytesFollowing,
                            PartitionName = partitionName,
                            RollbackIndexLocation = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart, 4)),
                            Flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 12, 4)),
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
        try
        {
            var hashAlgorithm = hashName == "SHA512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256;
            return key.VerifyData(signedData, signature, hashAlgorithm, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
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
        var computed = hashName == "SHA512" ? Sha512.ComputeHash(signedData) : Sha256.ComputeHash(signedData);
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