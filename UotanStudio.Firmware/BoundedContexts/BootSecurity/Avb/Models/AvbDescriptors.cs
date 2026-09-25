namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;

/// <summary>
/// Verified-boot hashtree descriptor: the dm-verity geometry of a whole partition whose blocks are hashed with a
/// salt-prefixed SHA-256 tree.
/// <para>验证启动哈希树描述符：描述整个分区的 dm-verity 几何信息，其数据块由带盐值前缀的 SHA-256 树保护。</para>
/// </summary>
/// <remarks>
/// Signing writes this as a 16-byte big-endian tag and length header followed by a 164-byte body, the ASCII
/// partition name, the salt and the root digest, padded to an 8-byte boundary.
/// <para>签名时按大端序写出：16 字节的标签与长度头部，其后是 164 字节的主体、ASCII 分区名、盐值和根摘要，并对齐到 8 字节边界。</para>
/// </remarks>
public sealed class AvbHashtreeDescriptor
{
    /// <summary>dm-verity on-disk format version written as the first body field.
    /// <para>写入主体的 dm-verity 磁盘格式版本，位于主体首字段。</para></summary>
    /// <value>The version number; defaults to 1. <para>版本号；默认为 1。</para></value>
    public uint DmVerityVersion { get; init; } = 1;

    /// <summary>Size in bytes of the hashed partition content, block aligned.
    /// <para>被哈希的分区内容字节数，已按块对齐。</para></summary>
    /// <value>The block-aligned size of the hashed content, as reported by the hash-tree builder.
    /// <para>被哈希内容的块对齐大小，与哈希树构建器给出的结果一致。</para></value>
    public ulong ImageSize { get; init; }

    /// <summary>Absolute byte offset of the hash tree inside the partition.
    /// <para>哈希树在分区内的绝对字节偏移。</para></summary>
    /// <value>The offset where verifier readers must seek to find the tree; 0 means no offset is recorded.
    /// <para>校验方需要定位到哈希树的偏移；0 表示未记录偏移。</para></value>
    public ulong TreeOffset { get; init; }

    /// <summary>Size in bytes of the hash tree.
    /// <para>哈希树的字节大小。</para></summary>
    /// <value>Total tree bytes following <see cref="TreeOffset"/>, or 0 when the content fits a single data block.
    /// <para><see cref="TreeOffset"/> 之后的树总字节数；内容仅占单个数据块时为 0。</para></value>
    public ulong TreeSize { get; init; }

    /// <summary>Bytes per hashed data block. <para>每个被哈希数据块的字节数。</para></summary>
    /// <value>Defaults to 4096; the builder requires a positive multiple of 32.
    /// <para>默认为 4096；构建器要求其必须是 32 的正整数倍。</para></value>
    public uint DataBlockSize { get; init; } = 4096;

    /// <summary>Bytes per hash-tree block. <para>每个哈希树块的字节数。</para></summary>
    /// <value>Defaults to 4096; the builder requires a positive multiple of 32.
    /// <para>默认为 4096；构建器要求其必须是 32 的正整数倍。</para></value>
    public uint HashBlockSize { get; init; } = 4096;

    /// <summary>Reed-Solomon roots configured for forward error correction.
    /// <para>为纠错码配置的 Reed-Solomon 根数。</para></summary>
    /// <value>0 when the partition carries no FEC block. <para>分区不含纠错块时为 0。</para></value>
    public uint FecNumRoots { get; init; }

    /// <summary>Absolute byte offset of the FEC block inside the partition.
    /// <para>纠错块在分区内的绝对字节偏移。</para></summary>
    /// <value>0 when <see cref="FecNumRoots"/> is 0. <para>当 <see cref="FecNumRoots"/> 为 0 时取 0。</para></value>
    public ulong FecOffset { get; init; }

    /// <summary>Size in bytes of the FEC block. <para>纠错块的字节大小。</para></summary>
    /// <value>0 when no FEC data is present. <para>不存在纠错数据时为 0。</para></value>
    public ulong FecSize { get; init; }

    /// <summary>Hash algorithm name written into the fixed 32-byte ASCII field.
    /// <para>写入固定 32 字节 ASCII 字段的哈希算法名。</para></summary>
    /// <value>Defaults to <c>sha256</c>; the value must encode to 32 ASCII bytes or fewer, and the remaining bytes
    /// are zero padded. <para>默认为 <c>sha256</c>；其 ASCII 编码长度必须不超过 32 字节，其余字节以零填充。</para></value>
    public string HashAlgorithm { get; init; } = "sha256";

    /// <summary>Partition protected by this descriptor. <para>该描述符所保护的分区。</para></summary>
    /// <value>Non-empty ASCII name of at most 255 bytes, validated during signing.
    /// <para>不超过 255 字节的非空 ASCII 名称，签名时会校验。</para></value>
    public string PartitionName { get; init; } = string.Empty;

    /// <summary>Salt prepended to every hashed block. <para>每个被哈希块前拼接的盐值。</para></summary>
    /// <value>Raw digest-salt bytes appended after the partition name; an empty array means hashing without a salt.
    /// <para>追加在分区名之后的原始摘要盐字节；空数组表示不加盐哈希。</para></value>
    public byte[] Salt { get; init; } = [];

    /// <summary>Merkle root digest of the tree. <para>哈希树的 Merkle 根摘要。</para></summary>
    /// <value>Digest bytes appended after the salt, as produced by the hash-tree builder; an empty array stores no
    /// digest. <para>追加在盐值之后、由哈希树构建器产生的摘要字节；空数组表示不存储摘要。</para></value>
    public byte[] RootDigest { get; init; } = [];

    /// <summary>Descriptor flags written verbatim as a big-endian 32-bit field.
    /// <para>按大端 32 位字段原样写出的描述符标志。</para></summary>
    /// <value>0 unless a checker requires another value; the library does not interpret these bits.
    /// <para>除非校验方另有要求，否则为 0；本库不解释这些标志位。</para></value>
    public uint Flags { get; init; }
}

/// <summary>
/// Verified-boot chain partition descriptor: delegates verification of another partition to a public key carried
/// in this descriptor.
/// <para>验证启动链式分区描述符：将另一个分区的校验委托给本描述符携带的公钥。</para>
/// </summary>
/// <remarks>
/// Signing writes a 16-byte big-endian header, a 76-byte body, the ASCII partition name and the raw public-key
/// bytes, padded to an 8-byte boundary.
/// <para>签名时写出 16 字节大端头部、76 字节主体、ASCII 分区名与原始公钥字节，并对齐到 8 字节边界。</para>
/// </remarks>
public sealed class AvbChainPartitionDescriptor
{
    /// <summary>Rollback-slot index the chained partition consumes.
    /// <para>链式分区所使用的回滚槽索引。</para></summary>
    /// <value>A big-endian 32-bit location written in the body; 0 selects the first slot.
    /// <para>主体中以大端序写出的 32 位位置；0 表示第一个槽位。</para></value>
    public uint RollbackIndexLocation { get; init; }

    /// <summary>Partition whose vbmeta is verified by <see cref="PublicKey"/>.
    /// <para>其 vbmeta 由 <see cref="PublicKey"/> 校验的分区。</para></summary>
    /// <value>Non-empty ASCII name of at most 255 bytes, validated during signing.
    /// <para>不超过 255 字节的非空 ASCII 名称，签名时会校验。</para></value>
    public string PartitionName { get; init; } = string.Empty;

    /// <summary>Public key trusted to sign the chained partition's vbmeta.
    /// <para>被信任用于签名链式分区 vbmeta 的公钥。</para></summary>
    /// <value>Raw bytes appended after the partition name; an empty array carries no key material.
    /// <para>追加在分区名之后的原始字节；空数组表示不含密钥材料。</para></value>
    public byte[] PublicKey { get; init; } = [];
}

/// <summary>
/// Verified-boot kernel command line descriptor: extra parameters injected into the kernel command line when the
/// chain verifies successfully.
/// <para>验证启动内核命令行描述符：在验证链通过后注入内核命令行的附加参数。</para>
/// </summary>
public sealed class AvbKernelCmdlineDescriptor
{
    /// <summary>Descriptor flags written verbatim as a big-endian 32-bit field.
    /// <para>按大端 32 位字段原样写出的描述符标志。</para></summary>
    /// <value>0 means the text is appended unconditionally; the library does not interpret other bits.
    /// <para>0 表示无条件追加该文本；本库不解释其他标志位。</para></value>
    public uint Flags { get; init; }

    /// <summary>Command-line text contributed to the kernel. <para>贡献给内核的命令行文本。</para></summary>
    /// <value>UTF-8 encoded bytes whose length is stored in the body; an empty string contributes no parameters.
    /// <para>以 UTF-8 编码的字节，其长度记录在主体中；空字符串不贡献任何参数。</para></value>
    public string KernelCmdline { get; init; } = string.Empty;
}

/// <summary>
/// Verified-boot property descriptor: a single key/value property covered by the verified chain.
/// <para>验证启动属性描述符：受验证链覆盖的单个键值属性。</para>
/// </summary>
public sealed class AvbPropertyDescriptor
{
    /// <summary>Property name. <para>属性名。</para></summary>
    /// <value>UTF-8 text stored with a trailing NUL in the serialized body.
    /// <para>以 UTF-8 存储，并在序列化主体中附带结尾 NUL。</para></value>
    public string Key { get; init; } = string.Empty;

    /// <summary>Property payload. <para>属性负载。</para></summary>
    /// <value>Raw bytes stored with a trailing NUL after them; an empty array is a property with no value bytes.
    /// <para>原始字节，其后附带结尾 NUL；空数组表示没有值字节的属性。</para></value>
    public byte[] Value { get; init; } = [];
}