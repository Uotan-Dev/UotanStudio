namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;

/// <summary>
/// Typed form of an AVB hash descriptor (tag 2): binds one partition name to the digest of its
/// image content, serialized in the upstream libavb big-endian layout.
/// <para>AVB 哈希描述符（标签 2）的类型化表示：将单个分区名与其镜像内容的摘要绑定，按上游 libavb 大端布局序列化。</para>
/// </summary>
public sealed class AvbHashDescriptor
{
    /// <summary>
    /// ASCII partition name, non-empty and at most 255 bytes; validated when the descriptor is
    /// serialized for signing.
    /// <para>ASCII 分区名，非空且至多 255 字节；在签名序列化时校验。</para>
    /// </summary>
    /// <value>The partition name. <para>分区名。</para></value>
    public string PartitionName { get; init; } = string.Empty;

    /// <summary>Byte size of the image content the digest covers, written as a big-endian uint64 at
    /// descriptor body offset 0. <para>摘要所覆盖的镜像内容字节大小，以描述符体偏移 0 处的大端 uint64 写出。</para></summary>
    /// <value>The covered image size. <para>所覆盖的镜像大小。</para></value>
    public ulong ImageSize { get; init; }

    /// <summary>
    /// ASCII hash algorithm name copied verbatim into the fixed 32-byte body field at offset 8;
    /// names longer than 32 bytes are rejected at signing. The footer builders compute SHA-256 digests.
    /// <para>逐字复制到体偏移 8 处固定 32 字节字段的 ASCII 哈希算法名；超过 32 字节的算法名在签名时被拒绝。
    /// 尾部构建器计算的均为 SHA-256 摘要。</para>
    /// </summary>
    /// <value>The algorithm name, defaulting to <c>sha256</c>. <para>算法名，默认 <c>sha256</c>。</para></value>
    public string HashAlgorithm { get; init; } = "sha256";

    /// <summary>Raw descriptor flags written as a big-endian uint32 at body offset 52; the signing
    /// flows emit the default 0 verbatim.
    /// <para>以体偏移 52 处大端 uint32 写出的原始描述符标志；签名流程原样输出默认值 0。</para></summary>
    /// <value>The flags bit field. <para>标志位字段。</para></value>
    public uint Flags { get; init; }

    /// <summary>
    /// Salt bytes prepended to the image content before hashing (digest = H(salt || data)); at most
    /// 255 bytes. <see cref="Signing.AvbSigner"/> uses 32 random bytes when the caller supplies none;
    /// an empty array means no salt.
    /// <para>哈希前拼在镜像内容之前的盐值（摘要 = H(salt || 数据)）；至多 255 字节。
    /// 调用方未提供时，<see cref="Signing.AvbSigner"/> 使用 32 字节随机盐；空数组表示不加盐。</para>
    /// </summary>
    /// <value>The salt bytes. <para>盐值字节。</para></value>
    public byte[] Salt { get; init; } = [];

    /// <summary>
    /// Raw digest over salt plus image content (32 bytes for SHA-256), at most 255 bytes; serialized
    /// after the partition name and salt.
    /// <para>对盐值与镜像内容拼接后计算的原始摘要（SHA-256 为 32 字节），至多 255 字节；序列化在分区名与盐值之后。</para>
    /// </summary>
    /// <value>The digest bytes. <para>摘要字节。</para></value>
    public byte[] Digest { get; init; } = [];
}

/// <summary>
/// Outcome report returned by <see cref="Signing.AvbSigner"/> operations: published output location
/// and size, the RSA signature algorithm selected from the key, and the failure text when the
/// operation did not succeed.
/// <para>由 <see cref="Signing.AvbSigner"/> 各操作返回的结果报告：已发布的输出位置与大小、按密钥选定的
/// RSA 签名算法，以及操作未成功时的失败文本。</para>
/// </summary>
public sealed class AvbSignResult
{
    /// <summary><see langword="true"/> when the operation completed and its output file was
    /// published; on <see langword="false"/> read <see cref="ErrorMessage"/>.
    /// <para>操作完成且输出文件已发布时为 <see langword="true"/>；为 <see langword="false"/> 时读取
    /// <see cref="ErrorMessage"/>。</para></summary>
    /// <value>The success flag. <para>成功标志。</para></value>
    public bool Success { get; init; }

    /// <summary>Human-readable bilingual failure reason; <see langword="null"/> when
    /// <see cref="Success"/> is <see langword="true"/>.
    /// <para>中英双语的可读失败原因；<see cref="Success"/> 为 <see langword="true"/> 时为
    /// <see langword="null"/>。</para></summary>
    /// <value>The failure text or <see langword="null"/>. <para>失败文本，或为 <see langword="null"/>。</para></value>
    public string? ErrorMessage { get; init; }

    /// <summary>Path of the written vbmeta or signed image; <see langword="null"/> on failure.
    /// <para>写出的 vbmeta 或已签名镜像路径；失败时为 <see langword="null"/>。</para></summary>
    /// <value>The output path or <see langword="null"/>. <para>输出路径，或为 <see langword="null"/>。</para></value>
    public string? OutputPath { get; init; }

    /// <summary>
    /// Numeric VBMeta signature algorithm id recorded in the header: 1, 2 or 3 select SHA-256 with
    /// RSA 2048, 4096 or 8192 according to the signing key's modulus size; 0 is left when the
    /// operation only patched flags. AVB signing here is RSA-only.
    /// <para>记录在头部中的数字 VBMeta 签名算法 id：1、2、3 按签名密钥模数长度分别对应 SHA-256 配合
    /// RSA 2048、4096、8192；仅改写标志位的操作保持 0。此处的 AVB 签名仅支持 RSA。</para>
    /// </summary>
    /// <value>The algorithm type id. <para>算法类型 id。</para></value>
    public uint AlgorithmType { get; init; }

    /// <summary>Display name resolved from <see cref="AlgorithmType"/> (for example
    /// <c>SHA256_RSA2048</c>); the verification-disable flow reports the fixed string
    /// <c>flags-patched</c>.
    /// <para>由 <see cref="AlgorithmType"/> 解析出的显示名（例如 <c>SHA256_RSA2048</c>）；
    /// 禁用校验流程固定报告 <c>flags-patched</c>。</para></summary>
    /// <value>The algorithm display name. <para>算法显示名。</para></value>
    public string AlgorithmName { get; init; } = string.Empty;

    /// <summary>Number of descriptors serialized into the signed output; 0 when the operation only
    /// patched flags. <para>序列化进已签名输出的描述符数量；仅改写标志位的操作为 0。</para></summary>
    /// <value>The descriptor count. <para>描述符数量。</para></value>
    public int DescriptorCount { get; init; }

    /// <summary>Total byte size of the published output, i.e. the written vbmeta or the signed image
    /// including tree, vbmeta and footer regions.
    /// <para>已发布输出的总字节大小，即写出的 vbmeta，或包含哈希树、vbmeta 与尾部区域的已签名镜像。</para></summary>
    /// <value>The output size in bytes. <para>输出的字节大小。</para></value>
    public long OutputSize { get; init; }
}