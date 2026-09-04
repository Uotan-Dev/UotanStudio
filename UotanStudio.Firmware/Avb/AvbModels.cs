namespace UotanStudio.Firmware.Avb;

/// <summary>
/// Describes one AVB hash descriptor to embed into a signed VBMeta image: the target partition
/// name, the salt and the expected digest (computed over <c>salt || partition_data</c> with SHA-256).
/// <para>描述要嵌入已签名 VBMeta 镜像的单个 AVB 哈希描述符：目标分区名、盐值与期望摘要
/// （对 <c>salt || partition_data</c> 计算 SHA-256）。</para>
/// </summary>
public sealed class AvbHashDescriptor
{
    /// <summary>
    /// Gets or sets the target partition name (ASCII, 1-255 bytes).
    /// <para>获取或设置目标分区名称（ASCII，1-255 字节）。</para>
    /// </summary>
    public string PartitionName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the salt prefixed to the partition data before hashing.
    /// <para>获取或设置哈希前与分区数据拼接的盐值。</para>
    /// </summary>
    public byte[] Salt { get; init; } = [];

    /// <summary>
    /// Gets or sets the expected digest of <c>salt || partition_data</c> (SHA-256, 32 bytes).
    /// <para>获取或设置 <c>salt || partition_data</c> 的期望摘要（SHA-256，32 字节）。</para>
    /// </summary>
    public byte[] Digest { get; init; } = [];
}

/// <summary>
/// Result of an AVB signing operation.
/// <para>AVB 签名操作的结果。</para>
/// </summary>
public sealed class AvbSignResult
{
    /// <summary>
    /// Gets whether the signing operation succeeded.
    /// <para>获取签名操作是否成功。</para>
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the error message when signing failed.
    /// <para>获取签名失败时的错误信息。</para>
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the output file path.
    /// <para>获取输出文件路径。</para>
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// Gets the libavb algorithm type used (1 = SHA256_RSA2048, 2 = SHA256_RSA4096, 3 = SHA256_RSA8192).
    /// <para>获取使用的 libavb 算法类型（1 = SHA256_RSA2048，2 = SHA256_RSA4096，3 = SHA256_RSA8192）。</para>
    /// </summary>
    public uint AlgorithmType { get; init; }

    /// <summary>
    /// Gets the algorithm display name (e.g. "SHA256_RSA2048").
    /// <para>获取算法显示名称（如 "SHA256_RSA2048"）。</para>
    /// </summary>
    public string AlgorithmName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of hash descriptors embedded in the VBMeta image.
    /// <para>获取嵌入 VBMeta 镜像的哈希描述符数量。</para>
    /// </summary>
    public int DescriptorCount { get; init; }

    /// <summary>
    /// Gets the output file size in bytes.
    /// <para>获取输出文件的字节大小。</para>
    /// </summary>
    public long OutputSize { get; init; }
}

/// <summary>
/// Signs Android Verified Boot (AVB) VBMeta images and appends AVB hash footers to partition images.
/// The emitted signature is RSASSA-PKCS1-v1_5 SHA-256 over the serialized 256-byte VBMeta header
/// followed by the auxiliary data block — exactly the scheme verified by
/// <see cref="AvbSignatureVerifier"/> (its <c>RsaDigestSigner</c> implementation, and the scheme
/// asserted by the <c>Verify_PssSignature_IsRejected</c> test). Signed output therefore verifies
/// round-trip with <see cref="AvbVerifier"/>.
/// <para>为 Android Verified Boot（AVB）VBMeta 镜像签名，并为分区镜像追加 AVB 哈希尾部。
/// 生成的签名对序列化的 256 字节 VBMeta 头及其后的辅助数据块使用 RSASSA-PKCS1-v1_5 SHA-256——
/// 与 <see cref="AvbSignatureVerifier"/>（其 <c>RsaDigestSigner</c> 实现，以及
/// <c>Verify_PssSignature_IsRejected</c> 测试所断言的算法）完全一致，因此签名输出可被
/// <see cref="AvbVerifier"/> 往返验证通过。</para>
/// </summary>
/// <remarks>
/// All I/O is streaming and bounded: partition images are copied chunk-by-chunk and never fully
/// loaded into memory, and size arithmetic is performed in <c>checked</c> context.
/// <para>所有 IO 均为流式且有界：分区镜像按块复制、从不整文件载入内存；长度运算均在
/// <c>checked</c> 上下文中执行。</para>
/// </remarks>
