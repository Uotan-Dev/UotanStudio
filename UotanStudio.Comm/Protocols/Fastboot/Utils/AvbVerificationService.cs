using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Comm.Protocols.Fastboot.Utils;

/// <summary>
/// AVB (Android Verified Boot) signature/hash algorithm types, matching libavb's
/// <c>AvbAlgorithmType</c> enumeration values.
/// <para>AVB（Android Verified Boot）签名/哈希算法类型，与 libavb 的
/// <c>AvbAlgorithmType</c> 枚举值一致。</para>
/// </summary>
public enum AvbAlgorithmType
{
    /// <summary>
    /// No algorithm (vbmeta image is not signed/hashed).
    /// <para>无算法（vbmeta 镜像未签名/未哈希）。</para>
    /// </summary>
    None = 0,

    /// <summary>
    /// SHA256 hash with an RSA-2048 signature.
    /// <para>SHA256 哈希与 RSA-2048 签名。</para>
    /// </summary>
    Sha256Rsa2048 = 1,

    /// <summary>
    /// SHA256 hash with an RSA-4096 signature.
    /// <para>SHA256 哈希与 RSA-4096 签名。</para>
    /// </summary>
    Sha256Rsa4096 = 2,

    /// <summary>
    /// SHA256 hash with an RSA-8192 signature.
    /// <para>SHA256 哈希与 RSA-8192 签名。</para>
    /// </summary>
    Sha256Rsa8192 = 3,

    /// <summary>
    /// SHA512 hash with an RSA-2048 signature.
    /// <para>SHA512 哈希与 RSA-2048 签名。</para>
    /// </summary>
    Sha512Rsa2048 = 4,

    /// <summary>
    /// SHA512 hash with an RSA-4096 signature.
    /// <para>SHA512 哈希与 RSA-4096 签名。</para>
    /// </summary>
    Sha512Rsa4096 = 5,

    /// <summary>
    /// SHA512 hash with an RSA-8192 signature.
    /// <para>SHA512 哈希与 RSA-8192 签名。</para>
    /// </summary>
    Sha512Rsa8192 = 6
}

/// <summary>
/// Parsed <c>AvbVBMetaImageHeader</c> of a vbmeta image. All fields are read big-endian
/// per the AVB spec; the field layout at offsets 0..79 is identical across every libavb
/// header revision, so parsing stays version-agnostic.
/// <para>vbmeta 镜像的 <c>AvbVBMetaImageHeader</c> 解析结果。所有字段按 AVB 规范以大端序读取；
/// 偏移 0..79 的字段布局在所有 libavb 头版本中一致，因此解析与版本无关。</para>
/// </summary>
public sealed class AvbVbmetaHeader
{
    /// <summary>
    /// Size of the current (libavb &gt;= 1.2) vbmeta header in bytes.
    /// <para>当前（libavb &gt;= 1.2）vbmeta 头的大小（字节）。</para>
    /// </summary>
    public const int Size = 204;

    /// <summary>
    /// Older header sizes used by earlier libavb revisions; candidate sizes tried when
    /// verifying the embedded hash so that legacy images verify correctly.
    /// <para>早期 libavb 修订版使用的旧头大小；校验内嵌哈希时尝试这些候选大小，
    /// 使旧版镜像也能正确校验。</para>
    /// </summary>
    internal static readonly int[] CandidateSizes = [204, 200, 196];

    private static ReadOnlySpan<byte> Magic => "AVB0"u8;

    /// <summary>Gets the required libavb version major. 所需 libavb 版本主版本号。</summary>
    public uint RequiredLibavbVersionMajor { get; }

    /// <summary>Gets the required libavb version minor. 所需 libavb 版本次版本号。</summary>
    public uint RequiredLibavbVersionMinor { get; }

    /// <summary>Gets the authentication data block size (bytes). 认证数据块大小（字节）。</summary>
    public ulong AuthenticationDataBlockSize { get; }

    /// <summary>Gets the auxiliary data block size (bytes). 辅助数据块大小（字节）。</summary>
    public ulong AuxiliaryDataBlockSize { get; }

    /// <summary>Gets the signature/hash algorithm type. 签名/哈希算法类型。</summary>
    public AvbAlgorithmType AlgorithmType { get; }

    /// <summary>Gets the raw algorithm type value. 原始算法类型值。</summary>
    public uint AlgorithmTypeValue { get; }

    /// <summary>Gets the hash offset within the authentication data block. 认证数据块内的哈希偏移。</summary>
    public ulong HashOffset { get; }

    /// <summary>Gets the hash size in bytes. 哈希大小（字节）。</summary>
    public ulong HashSize { get; }

    /// <summary>Gets the signature offset within the authentication data block. 认证数据块内的签名偏移。</summary>
    public ulong SignatureOffset { get; }

    /// <summary>Gets the signature size in bytes. 签名大小（字节）。</summary>
    public ulong SignatureSize { get; }

    /// <summary>Gets the public key offset within the auxiliary data block. 辅助数据块内的公钥偏移。</summary>
    public uint PublicKeyOffset { get; }

    /// <summary>Gets the public key size in bytes. 公钥大小（字节）。</summary>
    public uint PublicKeySize { get; }

    /// <summary>Gets the public key metadata offset within the auxiliary data block. 辅助数据块内的公钥元数据偏移。</summary>
    public uint PublicKeyMetadataOffset { get; }

    /// <summary>Gets the public key metadata size in bytes. 公钥元数据大小（字节）。</summary>
    public uint PublicKeyMetadataSize { get; }

    /// <summary>Gets the rollback index. 回滚索引。</summary>
    public ulong RollbackIndex { get; }

    /// <summary>Gets the vbmeta flags. vbmeta 标志。</summary>
    public uint Flags { get; }

    /// <summary>Gets the rollback index location. 回滚索引位置。</summary>
    public uint RollbackIndexLocation { get; }

    /// <summary>Gets the release string (e.g. "avbtool 1.2.0"). 版本发布字符串（如 "avbtool 1.2.0"）。</summary>
    public string ReleaseString { get; }

    private AvbVbmetaHeader(
        uint requiredLibavbVersionMajor,
        uint requiredLibavbVersionMinor,
        ulong authenticationDataBlockSize,
        ulong auxiliaryDataBlockSize,
        uint algorithmType,
        ulong hashOffset,
        ulong hashSize,
        ulong signatureOffset,
        ulong signatureSize,
        uint publicKeyOffset,
        uint publicKeySize,
        uint publicKeyMetadataOffset,
        uint publicKeyMetadataSize,
        ulong rollbackIndex,
        uint flags,
        uint rollbackIndexLocation,
        string releaseString)
    {
        RequiredLibavbVersionMajor = requiredLibavbVersionMajor;
        RequiredLibavbVersionMinor = requiredLibavbVersionMinor;
        AuthenticationDataBlockSize = authenticationDataBlockSize;
        AuxiliaryDataBlockSize = auxiliaryDataBlockSize;
        AlgorithmTypeValue = algorithmType;
        AlgorithmType = (AvbAlgorithmType)algorithmType;
        HashOffset = hashOffset;
        HashSize = hashSize;
        SignatureOffset = signatureOffset;
        SignatureSize = signatureSize;
        PublicKeyOffset = publicKeyOffset;
        PublicKeySize = publicKeySize;
        PublicKeyMetadataOffset = publicKeyMetadataOffset;
        PublicKeyMetadataSize = publicKeyMetadataSize;
        RollbackIndex = rollbackIndex;
        Flags = flags;
        RollbackIndexLocation = rollbackIndexLocation;
        ReleaseString = releaseString;
    }

    /// <summary>
    /// Attempts to parse a vbmeta image header from the beginning of <paramref name="data"/>.
    /// <para>尝试从 <paramref name="data"/> 起始处解析 vbmeta 镜像头。</para>
    /// </summary>
    /// <param name="data">The raw vbmeta image data. 原始 vbmeta 镜像数据。</param>
    /// <param name="header">The parsed header, or <c>null</c> on failure. 解析出的头；失败时为 <c>null</c>。</param>
    /// <returns><c>true</c> when the header was parsed successfully. 成功解析头时返回 <c>true</c>。</returns>
    public static bool TryParse(byte[] data, out AvbVbmetaHeader? header)
    {
        header = null;
        if (data == null || data.Length < 80 || !data.AsSpan(0, 4).SequenceEqual(Magic))
        {
            return false;
        }

        ReadOnlySpan<byte> s = data;

        // Fields at offsets 0..79 are identical in every libavb header revision.
        // 偏移 0..79 的字段在所有 libavb 头版本中一致。
        var major = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(4, 4));
        var minor = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(8, 4));
        var authSize = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(12, 8));
        var auxSize = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(20, 8));
        var algorithm = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(28, 4));
        var hashOffset = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(32, 8));
        var hashSize = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(40, 8));
        var sigOffset = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(48, 8));
        var sigSize = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(56, 8));
        var pubKeyOffset = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(64, 4));
        var pubKeySize = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(68, 4));
        var pubKeyMetaOffset = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(72, 4));
        var pubKeyMetaSize = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(76, 4));

        // Version-dependent tail (rollback index / flags / release string); read best-effort
        // when enough bytes are present.
        // 版本相关的尾部字段（回滚索引 / 标志 / 发布字符串）；有足够字节时尽力读取。
        ulong rollbackIndex = 0;
        uint flags = 0;
        uint rollbackIndexLocation = 0;
        var releaseString = string.Empty;

        if (data.Length >= Size)
        {
            rollbackIndex = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(140, 8));
            flags = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(148, 4));
            rollbackIndexLocation = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(152, 4));
            releaseString = DecodeAscii(s.Slice(156, 48));
        }

        header = new AvbVbmetaHeader(
            major, minor, authSize, auxSize, algorithm,
            hashOffset, hashSize, sigOffset, sigSize,
            pubKeyOffset, pubKeySize, pubKeyMetaOffset, pubKeyMetaSize,
            rollbackIndex, flags, rollbackIndexLocation, releaseString);
        return true;
    }

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return length == 0 ? string.Empty : Encoding.ASCII.GetString(bytes[..length]);
    }
}

/// <summary>
/// A parsed AVB hash descriptor (tag 0) found in the vbmeta auxiliary data block.
/// <para>在 vbmeta 辅助数据块中找到的 AVB 哈希描述符（tag 0）解析结果。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="AvbHashDescriptor"/> class.
/// <para>初始化 <see cref="AvbHashDescriptor"/> 类的新实例。</para>
/// </remarks>
public sealed class AvbHashDescriptor(
    ulong tag,
    uint dmVerityVersion,
    ulong imageSize,
    ulong hashBlockSize,
    string hashAlgorithm,
    string partitionName,
    byte[] salt,
    byte[] digest)
{
    /// <summary>Gets the descriptor tag (0 for a hash descriptor). 描述符标签（哈希描述符为 0）。</summary>
    public ulong Tag { get; } = tag;

    /// <summary>Gets the dm-verity version. dm-verity 版本。</summary>
    public uint DmVerityVersion { get; } = dmVerityVersion;

    /// <summary>Gets the image size in bytes. 镜像大小（字节）。</summary>
    public ulong ImageSize { get; } = imageSize;

    /// <summary>Gets the hash block size in bytes. 哈希块大小（字节）。</summary>
    public ulong HashBlockSize { get; } = hashBlockSize;

    /// <summary>Gets the hash algorithm name (e.g. "sha256"). 哈希算法名称（如 "sha256"）。</summary>
    public string HashAlgorithm { get; } = hashAlgorithm;

    /// <summary>Gets the target partition name. 目标分区名称。</summary>
    public string PartitionName { get; } = partitionName;

    /// <summary>Gets the salt used when computing the digest. 计算摘要时使用的盐值。</summary>
    public byte[] Salt { get; } = salt;

    /// <summary>Gets the expected digest stored in the descriptor. 描述符中存储的期望摘要。</summary>
    public byte[] Digest { get; } = digest;
}

/// <summary>
/// The 64-byte AVB footer appended to partition images (magic "AVBf"). All fields are
/// big-endian.
/// <para>附加到分区镜像末尾的 64 字节 AVB footer（魔数 "AVBf"）。所有字段为大端序。</para>
/// </summary>
public sealed class AvbFooter
{
    /// <summary>
    /// Size of the AVB footer in bytes.
    /// <para>AVB footer 的大小（字节）。</para>
    /// </summary>
    public const int Size = 64;

    private static ReadOnlySpan<byte> Magic => "AVBf"u8;

    /// <summary>Gets the footer format version major. footer 格式版本主版本号。</summary>
    public uint VersionMajor { get; }

    /// <summary>Gets the footer format version minor. footer 格式版本次版本号。</summary>
    public uint VersionMinor { get; }

    /// <summary>Gets the original (pre-vbmeta) image size. 原始（未含 vbmeta）镜像大小。</summary>
    public ulong OriginalImageSize { get; }

    /// <summary>Gets the offset of the vbmeta image. vbmeta 镜像偏移。</summary>
    public ulong VbmetaOffset { get; }

    /// <summary>Gets the size of the vbmeta image. vbmeta 镜像大小。</summary>
    public ulong VbmetaSize { get; }

    private AvbFooter(uint versionMajor, uint versionMinor, ulong originalImageSize, ulong vbmetaOffset, ulong vbmetaSize)
    {
        VersionMajor = versionMajor;
        VersionMinor = versionMinor;
        OriginalImageSize = originalImageSize;
        VbmetaOffset = vbmetaOffset;
        VbmetaSize = vbmetaSize;
    }

    /// <summary>
    /// Attempts to parse an AVB footer from the specified 64-byte span.
    /// <para>尝试从指定的 64 字节 span 解析 AVB footer。</para>
    /// </summary>
    /// <param name="data">A span of at least <see cref="Size"/> bytes. 至少 <see cref="Size"/> 字节的 span。</param>
    /// <param name="footer">The parsed footer, or <c>null</c> on failure. 解析出的 footer；失败时为 <c>null</c>。</param>
    /// <returns><c>true</c> when the footer magic matched and the footer was parsed. 魔数匹配并成功解析时返回 <c>true</c>。</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out AvbFooter? footer)
    {
        footer = null;
        if (data.Length < Size || !data[..4].SequenceEqual(Magic))
        {
            return false;
        }

        footer = new AvbFooter(
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)),
            BinaryPrimitives.ReadUInt64BigEndian(data.Slice(12, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(data.Slice(20, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(data.Slice(28, 8)));
        return true;
    }
}

/// <summary>
/// Represents the result of verifying an AVB vbmeta image.
/// <para>表示验证 AVB vbmeta 镜像的结果。</para>
/// </summary>
public sealed class AvbVerificationResult
{
    /// <summary>
    /// Gets or sets whether hash verification succeeded.
    /// <para>获取或设置哈希校验是否成功。</para>
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a human-readable Message describing the verification outcome.
    /// <para>获取或设置描述验证结果的人类可读消息。</para>
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the algorithm type used for the hash.
    /// <para>获取或设置用于哈希的算法类型。</para>
    /// </summary>
    public AvbAlgorithmType AlgorithmType { get; set; }

    /// <summary>
    /// Gets or sets the hash stored in the vbmeta header.
    /// <para>获取或设置 vbmeta 头中存储的哈希。</para>
    /// </summary>
    public byte[]? StoredHash { get; set; }

    /// <summary>
    /// Gets or sets the digest recomputed from the image contents.
    /// <para>获取或设置根据镜像内容重新计算的摘要。</para>
    /// </summary>
    public byte[]? ComputedHash { get; set; }

    /// <summary>
    /// Gets or sets whether the recomputed digest matched the stored hash.
    /// <para>获取或设置重新计算的摘要是否与存储的哈希匹配。</para>
    /// </summary>
    public bool HashVerificationSucceeded { get; set; }

    /// <summary>
    /// Gets or sets the signature extracted from the vbmeta image (raw RSA blob).
    /// <para>获取或设置从 vbmeta 镜像提取的签名（原始 RSA 数据块）。</para>
    /// </summary>
    public byte[]? Signature { get; set; }

    /// <summary>
    /// Gets or sets the parsed vbmeta header.
    /// <para>获取或设置解析出的 vbmeta 头。</para>
    /// </summary>
    public AvbVbmetaHeader? Header { get; set; }

    /// <summary>
    /// Gets or sets the hash descriptors found in the vbmeta image.
    /// <para>获取或设置在 vbmeta 镜像中找到的哈希描述符。</para>
    /// </summary>
    public IReadOnlyList<AvbHashDescriptor> Descriptors { get; set; } = [];

    /// <summary>
    /// Gets or sets whether a valid AVB footer was found at the end of the image.
    /// <para>获取或设置是否在镜像末尾找到有效的 AVB footer。</para>
    /// </summary>
    public bool HasFooter { get; set; }

    /// <summary>
    /// Gets or sets the AVB footer if present.
    /// <para>获取或设置 AVB footer（如果存在）。</para>
    /// </summary>
    public AvbFooter? Footer { get; set; }
}

/// <summary>
/// Represents the result of a signature verification attempt.
/// <para>表示签名验证尝试的结果。</para>
/// </summary>
public sealed class AvbSignatureResult
{
    /// <summary>
    /// Gets or sets whether signature verification succeeded.
    /// <para>获取或设置签名验证是否成功。</para>
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a human-readable Message describing the outcome.
    /// <para>获取或设置描述结果的人类可读消息。</para>
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the algorithm name used for the signature.
    /// <para>获取或设置用于签名的算法名称。</para>
    /// </summary>
    public string AlgorithmName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the signature that was (or was to be) verified.
    /// <para>获取或设置已（或待）验证的签名。</para>
    /// </summary>
    public byte[]? Signature { get; set; }

    /// <summary>
    /// Gets or sets the public key used for the signature.
    /// <para>获取或设置用于签名的公钥。</para>
    /// </summary>
    public byte[]? PublicKey { get; set; }
}

/// <summary>
/// Provides AVB (Android Verified Boot) verification services for vbmeta images: vbmeta
/// header parsing, hash (digest) verification against the embedded hash descriptor, and a
/// signature verification skeleton.
/// <para>为 vbmeta 镜像提供 AVB（Android Verified Boot）验证服务：vbmeta 头部解析、
/// 针对内嵌哈希描述符的哈希（摘要）校验，以及签名校验骨架。</para>
/// </summary>
/// <remarks>
/// No external AVB package is referenced. Signature verification (RSASSA-PSS over libavb's
/// raw big-endian RSA blobs) is intentionally left as a <see cref="NotSupportedException"/>
/// skeleton; only the structure is provided.
/// <para>未引用任何外部 AVB 包。签名验证（对 libavb 原始大端 RSA 数据块的 RSASSA-PSS）
/// 有意保留为 <see cref="NotSupportedException"/> 骨架；仅提供结构。</para>
/// </remarks>
public sealed class AvbVerificationService
{
    /// <summary>
    /// Verifies the integrity of a vbmeta image from raw bytes by recomputing the AVB digest
    /// (over the header with the two block-size fields zeroed plus the auxiliary data block)
    /// and comparing it with the hash stored in the header.
    /// <para>通过重新计算 AVB 摘要（对两个块大小字段清零后的头与辅助数据块求哈希）并与头中
    /// 存储的哈希比较，验证原始字节 vbmeta 镜像的完整性。</para>
    /// </summary>
    /// <param name="data">The raw vbmeta image data. 原始 vbmeta 镜像数据。</param>
    /// <returns>The verification result. 验证结果。</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>. <paramref name="data"/> 为 <c>null</c> 时抛出。</exception>
    public AvbVerificationResult VerifyVbmetaImage(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        var result = new AvbVerificationResult();
        if (data.Length == 0)
        {
            result.Success = false;
            result.Message = "Vbmeta data is empty. vbmeta 数据为空。";
            return result;
        }

        try
        {
            if (!AvbVbmetaHeader.TryParse(data, out var header) || header == null)
            {
                result.Success = false;
                result.Message = "Not a valid AVB vbmeta image (bad magic or short header). 不是有效的 AVB vbmeta 镜像（魔数错误或头过短）。";
                return result;
            }

            result.Header = header;
            result.AlgorithmType = header.AlgorithmType;

            if (header.HashSize > 0)
            {
                result.StoredHash = CopyRegion(data, AvbVbmetaHeader.Size + (long)header.HashOffset, (long)header.HashSize);
            }

            if (header.SignatureSize > 0)
            {
                result.Signature = CopyRegion(data, AvbVbmetaHeader.Size + (long)header.SignatureOffset, (long)header.SignatureSize);
            }

            result.Descriptors = ExtractHashDescriptors(data, header);

            if (result.StoredHash is { Length: > 0 })
            {
                // Try every known header size so legacy (196/200-byte) headers verify too.
                // 尝试所有已知头大小，使旧版（196/200 字节）头也能校验。
                byte[]? computed = null;
                foreach (var headerSize in AvbVbmetaHeader.CandidateSizes)
                {
                    try
                    {
                        computed = ComputeVbmetaDigest(data, headerSize);
                    }
                    catch (InvalidDataException)
                    {
                        continue;
                    }

                    if (computed != null &&
                        CryptographicOperations.FixedTimeEquals(computed, result.StoredHash))
                    {
                        result.ComputedHash = computed;
                        result.HashVerificationSucceeded = true;
                        break;
                    }
                }

                result.ComputedHash ??= computed;
                result.Success = result.HashVerificationSucceeded;
                result.Message = result.HashVerificationSucceeded
                    ? "VBMeta image hash verification succeeded (signature not verified — skeleton). vbmeta 镜像哈希校验成功（签名未验证——骨架实现）。"
                    : "VBMeta image hash verification failed. vbmeta 镜像哈希校验失败。";
            }
            else
            {
                result.Success = false;
                result.Message = "Vbmeta header declares no hash; nothing to verify. vbmeta 头未声明哈希，无可校验内容。";
            }

            if (data.Length >= AvbFooter.Size &&
                AvbFooter.TryParse(data.AsSpan(data.Length - AvbFooter.Size, AvbFooter.Size), out var footer) &&
                footer != null)
            {
                result.HasFooter = true;
                result.Footer = footer;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Failed to parse or verify vbmeta image: {ex.Message} 解析或校验 vbmeta 镜像失败。";
        }

        return result;
    }

    /// <summary>
    /// Verifies the integrity of a vbmeta image from a file.
    /// <para>从文件验证 vbmeta 镜像的完整性。</para>
    /// </summary>
    /// <param name="filePath">The path to the vbmeta image file. vbmeta 镜像文件路径。</param>
    /// <returns>The verification result. 验证结果。</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="filePath"/> is null or whitespace. <paramref name="filePath"/> 为 null 或空白时抛出。</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist. 文件不存在时抛出。</exception>
    /// <exception cref="IOException">Thrown when the file cannot be read. 文件无法读取时抛出。</exception>
    public AvbVerificationResult VerifyVbmetaFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required. 文件路径必填。", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Vbmeta image file not found: {filePath} vbmeta 镜像文件不存在。", filePath);
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(filePath);
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new IOException($"Failed to read vbmeta image file: {filePath} 读取 vbmeta 镜像文件失败。", ex);
        }

        return VerifyVbmetaImage(data);
    }

    /// <summary>
    /// Returns whether the byte array starts with the vbmeta magic ("AVB0").
    /// <para>返回字节数组是否以 vbmeta 魔数（"AVB0"）开头。</para>
    /// </summary>
    /// <param name="data">The raw image data. 原始镜像数据。</param>
    /// <returns><c>true</c> when the magic matches. 魔数匹配时返回 <c>true</c>。</returns>
    public bool IsVbmetaImage(byte[] data) => data != null && data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("AVB0"u8);

    /// <summary>
    /// Recomputes the AVB digest of a vbmeta image using the current 204-byte header size.
    /// The digest covers the header (with <c>authentication_data_block_size</c> and
    /// <c>auxiliary_data_block_size</c> zeroed) followed by the auxiliary data block, with
    /// the hash descriptor's salt prepended.
    /// <para>使用当前 204 字节头大小重新计算 vbmeta 镜像的 AVB 摘要。摘要覆盖清零
    /// <c>authentication_data_block_size</c> 与 <c>auxiliary_data_block_size</c> 后的头及其后的
    /// 辅助数据块，并在前面拼接哈希描述符的盐值。</para>
    /// </summary>
    /// <param name="data">The raw vbmeta image data. 原始 vbmeta 镜像数据。</param>
    /// <returns>The computed digest, or <c>null</c> when the image is not a valid vbmeta
    /// image or uses an unsupported algorithm. 计算出的摘要；镜像不是有效的 vbmeta 镜像或算法不受支持时返回 <c>null</c>。</returns>
    public byte[]? ComputeVbmetaDigest(byte[] data)
        => data == null ? null : ComputeVbmetaDigest(data, AvbVbmetaHeader.Size);

    /// <summary>
    /// Verifies the signature of a vbmeta image.
    /// <para>验证 vbmeta 镜像的签名。</para>
    /// </summary>
    /// <remarks>
    /// Skeleton only: full verification requires an RSASSA-PSS verifier that understands
    /// libavb's raw big-endian RSA key/signature blobs, which .NET's RSA API cannot express
    /// without an external AVB crypto package (e.g. FirmwareKit.AVB.Security). This method
    /// validates the inputs and documents the required steps, then throws
    /// <see cref="NotSupportedException"/> instead of returning a misleading result.
    /// <para>仅骨架：完整验证需要能理解 libavb 原始大端 RSA 密钥/签名数据块的 RSASSA-PSS
    /// 验证器，.NET 的 RSA API 在无外部 AVB 加密包（如 FirmwareKit.AVB.Security）时无法表达。
    /// 本方法校验输入并说明所需步骤，然后抛出 <see cref="NotSupportedException"/>，而不是返回误导性结果。</para>
    /// </remarks>
    /// <param name="data">The vbmeta image data. vbmeta 镜像数据。</param>
    /// <param name="signature">The raw RSA signature blob. 原始 RSA 签名数据块。</param>
    /// <param name="publicKey">The raw public key blob. 原始公钥数据块。</param>
    /// <param name="algorithmType">The algorithm type. 算法类型。</param>
    /// <returns>This method always throws. 本方法始终抛出异常。</returns>
    /// <exception cref="ArgumentNullException">Thrown when any input is <c>null</c>. 任一输入为 <c>null</c> 时抛出。</exception>
    /// <exception cref="ArgumentException">Thrown when the algorithm type is unsupported. 算法类型不受支持时抛出。</exception>
    /// <exception cref="NotSupportedException">Thrown because no external AVB crypto package is referenced. 因未引用外部 AVB 加密包而抛出。</exception>
    public AvbSignatureResult VerifySignature(byte[] data, byte[] signature, byte[] publicKey, AvbAlgorithmType algorithmType)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (signature == null)
        {
            throw new ArgumentNullException(nameof(signature));
        }

        if (publicKey == null)
        {
            throw new ArgumentNullException(nameof(publicKey));
        }

        var algorithmName = GetHashAlgorithmName(algorithmType);
        if (string.IsNullOrEmpty(algorithmName))
        {
            throw new ArgumentException($"Unsupported AVB algorithm type: {algorithmType} 不支持的 AVB 算法类型。", nameof(algorithmType));
        }

        // Steps a real implementation must perform (structure skeleton):
        // 1. Parse the vbmeta header and locate signature / public key regions in the
        //    authentication and auxiliary data blocks.
        // 2. Import the raw big-endian modulus/exponent from `publicKey` into an RSAParameters.
        // 3. Verify RSASSA-PSS over `data` (digest = H(salt || header_with_zeroed_sizes || aux))
        //    with the hash derived from `algorithmType`.
        // 实际实现必须执行的步骤（结构骨架）：
        // 1. 解析 vbmeta 头，在认证数据块/辅助数据块中定位签名与公钥区域。
        // 2. 将 `publicKey` 的原始大端模数/指数导入 RSAParameters。
        // 3. 使用 `algorithmType` 派生的哈希对 `data` 进行 RSASSA-PSS 验证
        //    （摘要 = H(盐值 || 清零尺寸后的头 || 辅助数据块)）。
        throw new NotSupportedException(
            "AVB signature verification is not implemented because no external AVB crypto " +
            "package is referenced; see the XML remarks for the required RSASSA-PSS steps. " +
            "未引用外部 AVB 加密包，未实现 AVB 签名验证；所需 RSASSA-PSS 步骤见 XML 注释。");
    }

    /// <summary>
    /// Extracts all hash descriptors (tag 0) from the vbmeta auxiliary data block.
    /// <para>从 vbmeta 辅助数据块中提取所有哈希描述符（tag 0）。</para>
    /// </summary>
    /// <param name="data">The raw vbmeta image data. 原始 vbmeta 镜像数据。</param>
    /// <returns>The parsed hash descriptors. 解析出的哈希描述符列表。</returns>
    public IReadOnlyList<AvbHashDescriptor> ExtractHashDescriptors(byte[] data)
    {
        return data == null
            ? []
            : !AvbVbmetaHeader.TryParse(data, out var header) || header == null ? [] : ExtractHashDescriptors(data, header);
    }

    private static byte[]? CopyRegion(byte[] data, long start, long length)
    {
        if (start < 0 || length < 0 || start + length > data.Length)
        {
            return null;
        }

        var result = new byte[length];
        Buffer.BlockCopy(data, (int)start, result, 0, (int)length);
        return result;
    }

    private static string GetHashAlgorithmName(AvbAlgorithmType type) => type switch
    {
        AvbAlgorithmType.Sha256Rsa2048 or AvbAlgorithmType.Sha256Rsa4096 or AvbAlgorithmType.Sha256Rsa8192 => "SHA256",
        AvbAlgorithmType.Sha512Rsa2048 or AvbAlgorithmType.Sha512Rsa4096 or AvbAlgorithmType.Sha512Rsa8192 => "SHA512",
        AvbAlgorithmType.None => throw new NotImplementedException(),
        _ => string.Empty
    };

    private byte[]? ComputeVbmetaDigest(byte[] data, int headerSize)
    {
        if (!AvbVbmetaHeader.TryParse(data, out var header) || header == null)
        {
            return null;
        }

        var algorithmName = GetHashAlgorithmName(header.AlgorithmType);
        if (string.IsNullOrEmpty(algorithmName))
        {
            return null;
        }

        var hashInput = BuildHashInput(data, header, headerSize);
        var salt = ExtractSalt(data, header) ?? [];

        if (salt.Length == 0)
        {
            return algorithmName == "SHA512"
                ? SHA512.HashData(hashInput)
                : SHA256.HashData(hashInput);
        }

        var combined = new byte[salt.Length + hashInput.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(hashInput, 0, combined, salt.Length, hashInput.Length);
        return algorithmName == "SHA512"
            ? SHA512.HashData(combined)
            : SHA256.HashData(combined);
    }

    private static byte[] BuildHashInput(byte[] data, AvbVbmetaHeader header, int headerSize)
    {
        if (headerSize < 80 || data.Length < headerSize)
        {
            throw new InvalidDataException("Vbmeta image is shorter than its header. vbmeta 镜像短于其头部。");
        }

        var headerBytes = new byte[headerSize];
        Buffer.BlockCopy(data, 0, headerBytes, 0, headerSize);

        // Per the AVB spec these two block-size fields are zeroed before hashing.
        // 按 AVB 规范，哈希前这两个块大小字段清零。
        BinaryPrimitives.WriteUInt64BigEndian(headerBytes.AsSpan(12, 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(headerBytes.AsSpan(20, 8), 0);

        var auxStart = headerSize + (long)header.AuthenticationDataBlockSize;
        var auxSize = (long)header.AuxiliaryDataBlockSize;
        if (auxStart < 0 || auxSize < 0 || auxStart + auxSize > data.Length)
        {
            throw new InvalidDataException("Vbmeta auxiliary data block is out of range. vbmeta 辅助数据块越界。");
        }

        var input = new byte[headerSize + auxSize];
        Buffer.BlockCopy(headerBytes, 0, input, 0, headerSize);
        Buffer.BlockCopy(data, (int)auxStart, input, headerSize, (int)auxSize);
        return input;
    }

    private static byte[]? ExtractSalt(byte[] data, AvbVbmetaHeader header)
    {
        foreach (var descriptor in ExtractHashDescriptors(data, header))
        {
            if (descriptor.Salt.Length > 0)
            {
                return descriptor.Salt;
            }
        }

        return null;
    }

    private static IReadOnlyList<AvbHashDescriptor> ExtractHashDescriptors(byte[] data, AvbVbmetaHeader header)
    {
        const ulong hashDescriptorTag = 0;

        var result = new List<AvbHashDescriptor>();
        var auxStart = AvbVbmetaHeader.Size + (long)header.AuthenticationDataBlockSize;
        var auxSize = (long)header.AuxiliaryDataBlockSize;
        if (auxStart < 0 || auxSize < 0 || auxStart + auxSize > data.Length)
        {
            return result;
        }

        var cursor = auxStart;
        var auxEnd = auxStart + auxSize;
        while (cursor + 16 <= auxEnd)
        {
            ReadOnlySpan<byte> s = data;
            var tag = BinaryPrimitives.ReadUInt64BigEndian(s.Slice((int)cursor, 8));
            var numBytesFollowing = BinaryPrimitives.ReadUInt64BigEndian(s.Slice((int)cursor + 8, 8));
            if (numBytesFollowing > (ulong)(auxEnd - cursor - 16))
            {
                break;
            }

            if (tag == hashDescriptorTag && numBytesFollowing >= 56)
            {
                ParseHashDescriptor(data, (int)cursor + 16, result);
            }

            cursor += 16 + (int)numBytesFollowing;
        }

        return result;
    }

    private static void ParseHashDescriptor(byte[] data, int bodyStart, List<AvbHashDescriptor> output)
    {
        ReadOnlySpan<byte> s = data.AsSpan(bodyStart);
        var dmVerityVersion = BinaryPrimitives.ReadUInt32BigEndian(s[..4]);
        var imageSize = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(4, 8));
        var hashBlockSize = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(12, 8));
        var hashAlgorithm = DecodeAscii(s.Slice(20, 32));
        var partitionNameLen = s[52];
        var saltLen = s[53];
        var digestLen = s[54];

        var partitionNameStart = 56;
        if (partitionNameStart + partitionNameLen + saltLen + digestLen > s.Length)
        {
            return;
        }

        var partitionName = Encoding.UTF8.GetString(s.Slice(partitionNameStart, partitionNameLen))
            .TrimEnd('\0');
        var saltStart = partitionNameStart + partitionNameLen;
        var salt = s.Slice(saltStart, saltLen).ToArray();
        var digestStart = saltStart + saltLen;
        var digest = s.Slice(digestStart, digestLen).ToArray();

        output.Add(new AvbHashDescriptor(
            tag: 0,
            dmVerityVersion,
            imageSize,
            hashBlockSize,
            hashAlgorithm,
            partitionName,
            salt,
            digest));
    }

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return length == 0 ? string.Empty : Encoding.ASCII.GetString(bytes[..length]);
    }
}